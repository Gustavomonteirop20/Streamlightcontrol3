using System.IO;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Core.Services;

/// <summary>
/// Laadt en bewaart settings.json in %LocalAppData%\StreamLightControl2 (zelfde map als versie 2.0).
/// Schrijft atomisch (eerst .tmp, dan vervangen) en maakt bij een beschadigd bestand een back-up.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly object _gate = new();
    private Timer? _debounce;
    private bool _disposed;

    public string DataDirectory { get; }
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public AppSettings Settings { get; private set; } = new();
    public string? LoadWarning { get; private set; }
    public bool Migrated { get; private set; }

    /// <summary>Wordt aangeroepen nadat er (automatisch) is opgeslagen. Argument: gelukt of niet.</summary>
    public event Action<bool, string?>? Saved;

    /// <summary>Optioneel: voert het opslaan uit op de UI-thread (waar ook de wijzigingen plaatsvinden).</summary>
    public Action<Action>? Dispatch { get; set; }

    public SettingsStore(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamLightControl2");
    }

    public AppSettings Load()
    {
        Directory.CreateDirectory(DataDirectory);
        AppSettings? loaded = null;
        if (File.Exists(SettingsPath))
        {
            try { loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions); }
            catch (Exception ex)
            {
                var backup = SettingsPath + $".kapot-{DateTime.Now:yyyyMMdd-HHmmss}.json";
                try { File.Copy(SettingsPath, backup, true); } catch { }
                LoadWarning = $"settings.json kon niet worden gelezen ({ex.Message}). Er is een back-up gemaakt: {Path.GetFileName(backup)}. De app start met standaardinstellingen.";
            }
        }
        Settings = loaded ?? new AppSettings();
        Repair(Settings);
        Migrated = Migrate(Settings);
        EnsureDefaults(Settings, isFresh: loaded == null);
        Watch(Settings);
        if (Migrated || loaded == null) SaveNow();
        return Settings;
    }

    /// <summary>
    /// Vult ontbrekende of lege onderdelen aan (een settings.json van een oudere versie of met de hand bewerkt),
    /// zodat de rest van de app nooit op een leeg onderdeel stuit.
    /// </summary>
    public static void Repair(AppSettings s)
    {
        s.BetterTok ??= new();
        s.Devices ??= new();
        s.Shows ??= new();
        s.Triggers ??= new();
        s.Follow ??= new();
        s.Gift ??= new();
        s.Queue ??= new();
        s.Lighting ??= new();
        s.General ??= new();
        s.Dashboard ??= new();
        s.Dashboard.VisibleStats ??= new();
        s.Updates ??= new();
        s.Spotify ??= new();
        s.DonateWave ??= new();
        s.Overlays ??= new();
        foreach (var d in s.Devices.Where(d => d == null).ToList()) s.Devices.Remove(d!);
        foreach (var t in s.Triggers.Where(t => t == null).ToList()) s.Triggers.Remove(t!);
        foreach (var show in s.Shows.Where(x => x == null).ToList()) s.Shows.Remove(show!);
        foreach (var show in s.Shows)
        {
            show.Steps ??= new();
            foreach (var st in show.Steps.Where(x => x == null).ToList()) show.Steps.Remove(st!);
            show.FastData ??= ""; show.SafeData ??= "";
            show.PresetKey ??= "";
        }
    }

    /// <summary>Zet het 2.0-formaat (Rules, GoveeIp, BetterTokUrl) om naar het nieuwe formaat.</summary>
    public static bool Migrate(AppSettings s)
    {
        var changed = false;
        var fromLegacy = s.LegacyRules is { Count: > 0 };
        var startVersion = s.Version; // elke updatestap vergelijkt met de versie van vóór de update
        if (!string.IsNullOrWhiteSpace(s.LegacyBetterTokUrl))
        {
            s.BetterTok.LocalUrl = s.LegacyBetterTokUrl!;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(s.LegacyGoveeIp) && s.Devices.All(d => d.Ip != s.LegacyGoveeIp))
        {
            s.Devices.Add(new GoveeDeviceConfig
            {
                Ip = s.LegacyGoveeIp!,
                Sku = string.IsNullOrWhiteSpace(s.LegacyGoveeSku) ? "H619E" : s.LegacyGoveeSku!,
                Name = string.IsNullOrWhiteSpace(s.LegacyGoveeName) || s.LegacyGoveeName == "Handmatig apparaat" ? "Govee H619E" : s.LegacyGoveeName!
            });
            changed = true;
        }
        if (s.LegacyRules is { Count: > 0 })
        {
            var priority = Priorities.Normal;
            foreach (var rule in s.LegacyRules)
            {
                var hex = ColorUtil.Normalize(rule.ColorHex, "#9B6BFF");
                var flashes = Math.Clamp(rule.Flashes, 1, 10);
                var show = new LightShow
                {
                    Name = rule.DisplayName + " (uit 2.0)",
                    Steps = new ObservableCollection<LightStep>
                    {
                        LightStep.Color(hex, Math.Clamp(rule.FlashDurationMs, 100, 5000) * flashes, Math.Clamp(rule.Brightness, 1, 100), flashes)
                    }
                };
                s.Shows.Add(show);
                var type = NormalizeLegacyEvent(rule.EventName);
                s.Triggers.Add(new EventTrigger
                {
                    Name = rule.DisplayName,
                    EventType = type,
                    ShowId = show.Id,
                    Enabled = rule.Enabled,
                    Priority = priority,
                    GiftNames = type == EventTypes.Gift ? rule.MatchText : "",
                    MatchText = type == EventTypes.Comment ? rule.MatchText : ""
                });
            }
            changed = true;
        }
        // Versie 3 → 4: snel knipperen via zwart (lamp uit/aan was te traag voor Police en strobes),
        // en de verbeterde Police/Party-presets.
        if (startVersion < 4)
        {
            s.Lighting.FlashMode = FlashMode.Black;
            foreach (var key in new[] { Presets.Police, Presets.Party })
                s.Shows.FirstOrDefault(x => x.PresetKey == key)?.CopyFrom(Presets.Create(key));
            s.Version = 4;
            changed = true;
        }
        // Versie 4 → 5: gift-regels gelijk aan de BetterTok Sound Alerts, met de ♪-song-shows gekoppeld.
        if (startVersion < 10 || fromLegacy)
        {
            ApplyGiftRules(s);
            s.Version = 10;
            changed = true;
        }
        // Versie 7 → 8: song-shows vervangen door de festival-versie (helft/helft, tempo dat de strip aankan).
        if (startVersion < 10)
        {
            // Versie 10: één uptempo-festivalshow per song (±4 wissels per seconde), geen Rave-varianten en geen helft/helft meer.
            foreach (var t in s.Triggers.Where(t => t.ShowId.StartsWith("preset-song-") && t.ShowId.EndsWith("-rave")).ToList())
                t.ShowId = t.ShowId[..^"-rave".Length];
            foreach (var old in s.Shows.Where(x => x.PresetKey.StartsWith("song-") && x.PresetKey.EndsWith("-rave")).ToList())
                s.Shows.Remove(old);
            s.Lighting.SegmentEffects = false;
            s.Lighting.MinChangeMs = 250;
            foreach (var show in s.Shows.Where(x => x.PresetKey.StartsWith("song-") && SongShows.All.Any(z => z.Key == x.PresetKey)))
                show.CopyFrom(Presets.Create(show.PresetKey));
            // Presets met flitsen die sneller waren dan een Govee-strip aankan: strip-vriendelijke versie
            foreach (var key in new[] { Presets.Police, Presets.Party, Presets.Follow, Presets.Victory, Presets.BigGift, Presets.SmallGift, Presets.MediumGift, Presets.MegaGift })
                s.Shows.FirstOrDefault(x => x.PresetKey == key)?.CopyFrom(Presets.Create(key));
            s.Version = 10;
            changed = true;
        }
        s.LegacyBetterTokUrl = s.LegacyGoveeIp = s.LegacyGoveeSku = s.LegacyGoveeName = null;
        s.LegacyRules = null;
        // Versie 11: song-shows met contrastkleuren en witte flitsen
        if (startVersion < 11)
        {
            foreach (var show in s.Shows.Where(x => x.PresetKey.StartsWith("song-") && SongShows.All.Any(z => z.Key == x.PresetKey)))
                show.CopyFrom(Presets.Create(show.PresetKey));
            s.Version = 11;
            changed = true;
        }
        if (startVersion < 12)
        {
            // Versie 12: de strip pakt niet elke kleur onder ±550 ms → rustiger shows in scènes, presets ≥600 ms.
            s.Lighting.MinChangeMs = Math.Max(s.Lighting.MinChangeMs, 550);
            foreach (var show in s.Shows.Where(x => x.PresetKey.StartsWith("song-") && SongShows.All.Any(z => z.Key == x.PresetKey)))
                show.CopyFrom(Presets.Create(show.PresetKey));
            foreach (var key in new[] { Presets.Police, Presets.Party, Presets.Follow, Presets.Victory, Presets.BigGift, Presets.SmallGift, Presets.MediumGift, Presets.MegaGift })
                s.Shows.FirstOrDefault(x => x.PresetKey == key)?.CopyFrom(Presets.Create(key));
            s.Version = 12;
            changed = true;
        }
        if (startVersion < 13)
        {
            // Versie 13: song-shows in twee versies (club voor strips die direct springen, veilig voor strips met fade)
            foreach (var show in s.Shows.Where(x => x.PresetKey.StartsWith("song-") && SongShows.All.Any(z => z.Key == x.PresetKey)))
                show.CopyFrom(Presets.Create(show.PresetKey));
            foreach (var key in Presets.FastKeys)
                s.Shows.FirstOrDefault(x => x.PresetKey == key)?.CopyFrom(Presets.Create(key));
            // Direct springen staat al aan: de strip kan sneller → club-versies
            if (s.Lighting.DirectColor && s.Lighting.MinChangeMs > 300) s.Lighting.MinChangeMs = 300;   // (versie 14 zet dit verder op 40)
            s.Version = 13;
            changed = true;
        }
        if (startVersion < 14)
        {
            // Versie 14: rave-shows volgens de spec (¼-beat strobes, flash op elke kick, strip A/B om-en-om)
            foreach (var show in s.Shows.Where(x => x.PresetKey.StartsWith("song-") && SongShows.All.Any(z => z.Key == x.PresetKey)))
                show.CopyFrom(Presets.Create(show.PresetKey));
            // Direct springen: geen snelheidslimiet meer nodig
            if (s.Lighting.DirectColor && s.Lighting.MinChangeMs > 40) s.Lighting.MinChangeMs = 40;
            s.Version = 14;
            changed = true;
        }
        if (startVersion < 15)
        {
            // Versie 15: shows minder fel voor de streamcamera
            s.Lighting.ShowBrightness = 60;
            s.Version = 15;
            changed = true;
        }
        if (startVersion < 16)
        {
            // Versie 16: alle grote dono-sounds uit BetterTok (32 songs), gift-regels gelijk aan de BetterTok-actions,
            // song-shows waarvan de sound niet meer gebruikt wordt verdwijnen.
            RemoveUnknownSongs(s);
            ApplyGiftRules(s);
            var known = Presets.GiftRules().Select(t => t.Id).ToHashSet();
            foreach (var t in s.Triggers.Where(t => t.Id.StartsWith("tf-") && !known.Contains(t.Id)).ToList())
                s.Triggers.Remove(t);
            foreach (var show in s.Shows.Where(x => x.PresetKey.StartsWith("song-") && SongShows.All.Any(z => z.Key == x.PresetKey)))
                show.CopyFrom(Presets.Create(show.PresetKey));
            s.Version = 16;
            changed = true;
        }
        if (startVersion < 17)
        {
            // Versie 17: drops op de echte aanslagen in de sound (wit exact op de kick, kleurwissel op de zaag)
            foreach (var show in s.Shows.Where(x => x.PresetKey.StartsWith("song-") && SongShows.All.Any(z => z.Key == x.PresetKey)))
                show.CopyFrom(Presets.Create(show.PresetKey));
            s.Version = 17;
            changed = true;
        }
        if (startVersion < 18)
        {
            // Versie 18: DonateWave-tips (drie bedrag-regels, gelijk aan DonateWave › Alert-effecten)
            foreach (var t in Presets.DonateWaveRules())
                if (s.Triggers.All(x => x.Id != t.Id)) s.Triggers.Add(t);
            s.Version = 18;
            changed = true;
        }
        if (startVersion < 19)
        {
            // Versie 19: 15 DonateWave-sounds als song-show; tips spelen de show van de sound die DonateWave afspeelt.
            // Alleen regels die nog op de oude standaardshow staan worden omgezet.
            var oldDefault = new Dictionary<string, string>
            {
                ["tw-1"] = "preset-" + Presets.BigGift, ["tw-10"] = "preset-" + Presets.MegaGift, ["tw-30"] = "preset-song-nevercomingdown"
            };
            foreach (var t in Presets.DonateWaveRules())
            {
                var cur = s.Triggers.FirstOrDefault(x => x.Id == t.Id);
                if (cur == null) s.Triggers.Add(t);
                else if (oldDefault.TryGetValue(t.Id, out var od) && cur.ShowId == od) { cur.ShowId = t.ShowId; cur.Name = t.Name; }
            }
            foreach (var show in s.Shows.Where(x => x.PresetKey.StartsWith("song-") && SongShows.All.Any(z => z.Key == x.PresetKey)))
                show.CopyFrom(Presets.Create(show.PresetKey));
            s.Version = 19;
            changed = true;
        }
        if (startVersion < 20)
        {
            // Versie 20: eigen lichtshow voor DonateWave shout-outs
            foreach (var t in Presets.DonateWaveRules())
                if (s.Triggers.All(x => x.Id != t.Id)) s.Triggers.Add(t);
            s.Version = 20;
            changed = true;
        }
        if (startVersion < 21)
        {
            // Versie 21: shows volgen de echte kicks (beat-tracking, TikTok-edits), drops exact op de kick
            foreach (var show in s.Shows.Where(x => x.PresetKey.StartsWith("song-") && SongShows.All.Any(z => z.Key == x.PresetKey)))
                show.CopyFrom(Presets.Create(show.PresetKey));
            s.Version = 21;
            changed = true;
        }
        if (startVersion < 22)
        {
            // Versie 22: shows achter elkaar (nooit door elkaar), DonateWave shout-outs per special met de juiste sound,
            // show pas na de animatie
            s.Queue.InOrder = true;
            if (s.Queue.MaxLength < 50) s.Queue.MaxLength = 50;
            foreach (var t in s.Triggers.Where(t => t.Id.StartsWith("tf-") || t.Id.StartsWith("tw-"))) t.InterruptCurrent = false;
            var so = s.Triggers.FirstOrDefault(t => t.Id == "tw-shoutout");
            if (so != null && so.Priority == Priorities.Critical) { so.Priority = Priorities.Normal; so.Name = "DonateWave shout-out (overige)"; }
            foreach (var t in Presets.DonateWaveRules())
                if (s.Triggers.All(x => x.Id != t.Id)) s.Triggers.Add(t);
            s.Version = 22;
            changed = true;
        }
        if (startVersion < 23)
        {
            // Versie 23: animatieduur = langste van video en soundtrack (de overlay wacht op allebei);
            // alleen bijwerken als de gebruiker de oude gemeten waarde nog niet zelf heeft aangepast
            var old = new Dictionary<string, int> { ["desertking"] = 10560, ["spikygroove"] = 7500, ["enchantedhat"] = 7300,
                                                    ["lionscrown"] = 7766, ["acornfortune"] = 6600, ["cosmiclaunch"] = 7366 };
            foreach (var (id, _, _, animMs, _, _) in Presets.Specials)
            {
                var t = s.Triggers.FirstOrDefault(x => x.Id == "tw-special-" + id);
                if (t != null && old.TryGetValue(id, out var o) && t.StartDelayMs == o) t.StartDelayMs = animMs;
            }
            s.Version = 23;
            changed = true;
        }
        if (startVersion < 24)
        {
            // Versie 24: na de sound blijven naam en bedrag ~2 s in beeld (was 300 ms pauze)
            if (s.DonateWave.AlertGapMs == 300) s.DonateWave.AlertGapMs = 2000;
            s.Version = 24;
            changed = true;
        }
        if (startVersion < 25)
        {
            // Versie 25: tijden exact volgens de overlay-code van DonateWave: 1200 ms voorbereiden vóór elke animatie
            // (zit nu vast in het model), sound start op het einde van de animatievideo, 500 ms na elke alert.
            if (s.DonateWave.OverlayDelayMs == 1000) s.DonateWave.OverlayDelayMs = 250;
            if (s.DonateWave.BetweenAnimationsMs == 1000) s.DonateWave.BetweenAnimationsMs = 1200;
            var soundtrackBased = new Dictionary<string, int> { ["desertking"] = 10598, ["spikygroove"] = 7509, ["enchantedhat"] = 7317,
                                                                ["lionscrown"] = 7787, ["acornfortune"] = 6613, ["cosmiclaunch"] = 7403 };
            foreach (var (id, _, _, animMs, _, _) in Presets.Specials)
            {
                var t = s.Triggers.FirstOrDefault(x => x.Id == "tw-special-" + id);
                if (t != null && soundtrackBased.TryGetValue(id, out var o) && t.StartDelayMs == o) t.StartDelayMs = animMs;
            }
            s.Version = 25;
            changed = true;
        }
        if (startVersion < 26)
        {
            // Versie 26: tijden opnieuw gemeten op de echte overlay (12-11-2026). De vaste overlaytijden zitten nu in
            // het model; deze instellingen zijn alleen nog correcties. Alleen aanpassen als ze nog op de oude standaard staan.
            if (s.DonateWave.OverlayDelayMs == 250) s.DonateWave.OverlayDelayMs = 0;
            if (s.DonateWave.AlertGapMs == 2000) s.DonateWave.AlertGapMs = 3240;
            if (s.DonateWave.BetweenAnimationsMs == 1200) s.DonateWave.BetweenAnimationsMs = 1735;
            s.Version = 26;
            changed = true;
        }
        if (startVersion < 27)
        {
            // Versie 27: binnenkomst-shows voor Thomas en Diego (BetterTok join + eigen sound)
            foreach (var t in Presets.EntranceRules())
                if (s.Triggers.All(x => x.Id != t.Id)) s.Triggers.Add(t);
            s.Version = 27;
            changed = true;
        }
        if (startVersion < 28)
        {
            // Versie 28: binnenkomst Thomas is een kinderliedje → gezellige show in plaats van de rave-versie
            foreach (var show in s.Shows.Where(x => x.PresetKey == "song-thomas"))
                show.CopyFrom(Presets.Create(show.PresetKey));
            s.Version = 28;
            changed = true;
        }
        if (startVersion < 29)
        {
            // Versie 29: zichtbare stukjes strip op stream (opgegeven: segmenten 9 t/m 16) voor de zone-effecten
            var strips = s.Devices.Where(d => d.Enabled).ToList();
            if (strips.Count == 1 && strips[0].VisibleZones.Length == 0) strips[0].VisibleZones = "9-16";
            s.Version = 29;
            changed = true;
        }
        if (startVersion < 30)
        {
            // Versie 30: rustige shows (minder knipperen) en de zichtbare Direct-zones 11 t/m 20
            s.Lighting.CalmShows = true;
            foreach (var d in s.Devices.Where(d => d.Enabled && (d.VisibleZones is "" or "9-16")))
            {
                d.VisibleZones = "11-20";
                d.ZoneMethod = "direct";
                d.ZoneCount = 20;
            }
            s.Version = 30;
            changed = true;
        }
        if (startVersion < 31)
        {
            // Versie 31: binnenkomst-show voor Martin (C4-sound)
            foreach (var t in Presets.EntranceRules())
                if (s.Triggers.All(x => x.Id != t.Id)) s.Triggers.Add(t);
            s.Version = 31;
            changed = true;
        }
        if (startVersion < 32)
        {
            // Versie 32: binnenkomst van nickstreams2 zoals in de video. Heeft de streamer al een eigen regel voor
            // nickstreams2 gemaakt, dan krijgt díe regel de nieuwe show (de rest van de regel blijft zoals hij was).
            var own = s.Triggers.Where(t => t.Id != "te-nickstreams2" && t.EventType == EventTypes.Member &&
                (t.MatchText ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Any(n => n.TrimStart('@').Equals("nickstreams2", StringComparison.OrdinalIgnoreCase))).ToList();
            foreach (var t in own) t.ShowId = "preset-song-nick";
            if (own.Count == 0 && s.Triggers.All(t => t.Id != "te-nickstreams2"))
                s.Triggers.Add(Presets.EntranceRules().Single(t => t.Id == "te-nickstreams2"));
            foreach (var sh in s.Shows.Where(x => Presets.ExactSongs.Contains(x.PresetKey))) sh.PlayExact = true;
            s.Version = 32;
            changed = true;
        }
        if (startVersion < 33)
        {
            // Versie 33: binnenkomst-shows voor een bepaalde kijker maar één keer per live (ook eigen regels)
            foreach (var t in s.Triggers.Where(t => t.EventType == EventTypes.Member && !string.IsNullOrWhiteSpace(t.MatchText)))
                t.OncePerLive = true;
            s.Version = 33;
            changed = true;
        }
        if (changed && s.Version < 33) s.Version = 33;
        return changed;
    }

    /// <summary>Haalt song-shows weg waarvan de dono-sound niet (meer) in de app zit, met de standaardkoppelingen ernaartoe.</summary>
    public static void RemoveUnknownSongs(AppSettings s)
    {
        foreach (var show in s.Shows.Where(x => x.PresetKey.StartsWith("song-") && SongShows.All.All(z => z.Key != x.PresetKey)).ToList())
        {
            s.Shows.Remove(show);
            foreach (var t in s.Triggers.Where(t => t.ShowId == show.Id).ToList())
            {
                if (t.Id.StartsWith("tf-")) s.Triggers.Remove(t);
                else t.ShowId = "";
            }
        }
    }

    /// <summary>Vervangt de oude standaard coin-bereiken door de BetterTok-regels. Eigen regels blijven staan.</summary>
    public static void ApplyGiftRules(AppSettings s)
    {
        var oldDefaults = new[] { "Gift 1–99 coins", "Gift 100–499 coins", "Gift 500–999 coins", "Gift 1000+ coins" };
        foreach (var t in s.Triggers.Where(t => t.EventType == EventTypes.Gift && t.GiftNameList.Count == 0 && oldDefaults.Contains(t.Name)).ToList())
            s.Triggers.Remove(t);
        foreach (var t in Presets.GiftRules())
        {
            var existing = s.Triggers.FirstOrDefault(x => x.Id == t.Id);
            if (existing == null) { s.Triggers.Add(t); continue; }
            // bijwerken naar de huidige BetterTok-indeling
            existing.Name = t.Name; existing.MinCoins = t.MinCoins; existing.MaxCoins = t.MaxCoins;
            existing.GiftNames = t.GiftNames; existing.ShowId = t.ShowId; existing.Priority = t.Priority; existing.InterruptCurrent = t.InterruptCurrent;
        }
    }

    private static string NormalizeLegacyEvent(string name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "follow" => EventTypes.Follow,
        "like" => EventTypes.Like,
        "share" => EventTypes.Share,
        "member" => EventTypes.Member,
        "chat" or "comment" => EventTypes.Comment,
        _ => EventTypes.Gift
    };

    /// <summary>Zorgt dat alle presets bestaan. Bij een verse installatie ook standaardkoppelingen.</summary>
    public static void EnsureDefaults(AppSettings s, bool isFresh)
    {
        foreach (var key in Presets.AllKeys)
            if (s.Shows.All(x => x.PresetKey != key && x.Id != "preset-" + key))
                s.Shows.Add(Presets.Create(key));
        if (s.Triggers.Count == 0)
            foreach (var t in Presets.DefaultTriggers()) s.Triggers.Add(t);
        s.Updates ??= new UpdateOptions();
        if (string.IsNullOrWhiteSpace(s.Updates.Repo)) s.Updates.Repo = UpdateService.DefaultRepo;
        if (isFresh) s.Lighting.CalmShows = true;   // standaard rustige shows (minder knipperen)
        s.Dashboard.VisibleStats ??= new();
        if (s.Dashboard.VisibleStats.Count == 0) s.Dashboard.VisibleStats.AddRange(new[] { "follows", "gifts", "coins", "shows" });
    }

    // ---------- automatisch opslaan ----------

    private void Watch(AppSettings s)
    {
        foreach (var o in new INotifyPropertyChanged[] { s.BetterTok, s.Follow, s.Gift, s.Queue, s.Lighting, s.General, s.Updates, s.DonateWave, s.Overlays })
            o.PropertyChanged += (_, _) => RequestSave();
        WatchCollection(s.Devices);
        WatchCollection(s.Triggers);
        WatchCollection(s.Shows);
    }

    private void WatchCollection<T>(ObservableCollection<T> collection) where T : INotifyPropertyChanged
    {
        foreach (var item in collection) item.PropertyChanged += ItemChanged;
        collection.CollectionChanged += (_, e) =>
        {
            if (e.OldItems != null) foreach (T item in e.OldItems) item.PropertyChanged -= ItemChanged;
            if (e.NewItems != null) foreach (T item in e.NewItems) item.PropertyChanged += ItemChanged;
            if (e.Action == NotifyCollectionChangedAction.Reset) foreach (var item in collection) { item.PropertyChanged -= ItemChanged; item.PropertyChanged += ItemChanged; }
            RequestSave();
        };
    }

    private static readonly HashSet<string> RuntimeProps = new() { "Online", "Status", "LastSeen", "OnlineLabel" };

    private void ItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != null && RuntimeProps.Contains(e.PropertyName)) return;
        RequestSave();
    }

    /// <summary>Slaat 600 ms na de laatste wijziging op (voorkomt schrijven bij elke toetsaanslag).</summary>
    public void RequestSave()
    {
        lock (_gate)
        {
            _debounce ??= new Timer(_ => { if (Dispatch != null) Dispatch(() => SaveNow()); else SaveNow(); }, null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(600, Timeout.Infinite);
        }
    }

    public bool SaveNow()
    {
        if (_disposed) return false;
        string json;
        try
        {
            lock (_gate) json = JsonSerializer.Serialize(Settings, JsonOptions);
        }
        catch (InvalidOperationException)
        {
            // Collectie werd tijdens het serialiseren gewijzigd – later opnieuw.
            RequestSave();
            return false;
        }
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, SettingsPath, true);
            Saved?.Invoke(true, null);
            return true;
        }
        catch (Exception ex)
        {
            Saved?.Invoke(false, ex.Message);
            return false;
        }
    }

    public void ExportTo(string path) => File.WriteAllText(path, JsonSerializer.Serialize(Settings, JsonOptions));

    /// <summary>Leest een back-up en controleert hem. Het toepassen gebeurt via een herstart van de app.</summary>
    public static AppSettings ValidateBackup(string path)
    {
        var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("Leeg back-upbestand.");
        Migrate(s);
        EnsureDefaults(s, false);
        return s;
    }

    public void ReplaceFileWith(AppSettings s)
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, JsonOptions));
    }

    /// <summary>Stopt het automatisch opslaan (daarna schrijft SaveNow niets meer).</summary>
    public void Dispose()
    {
        lock (_gate) { _disposed = true; _debounce?.Dispose(); _debounce = null; }
    }
}
