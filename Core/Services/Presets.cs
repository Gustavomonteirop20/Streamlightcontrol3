using System.Collections.ObjectModel;
using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Core.Services;

/// <summary>Standaard lichtshows. Ze zijn volledig aan te passen en per stuk te herstellen.</summary>
public static class Presets
{
    public const string Rainbow = "rainbow", Police = "police", Fire = "fire", Ice = "ice", Party = "party",
        Victory = "victory", Follow = "follow", BigGift = "biggift", Like = "like", Share = "share",
        SmallGift = "smallgift", MediumGift = "mediumgift", MegaGift = "megagift";

    public static IReadOnlyList<string> AllKeys { get; } = new[]
        { Rainbow, Police, Fire, Ice, Party, Victory, Follow, BigGift, Like, Share, SmallGift, MediumGift, MegaGift }
        .Concat(SongShows.All.Select(s => s.Key)).ToArray();

    /// <summary>Song-shows: elke stap valt op een beat van de dono-sound. Twee versies: club (snel) en veilig.</summary>
    private static LightShow CreateSong(SongShows.Song song, bool fast)
    {
        var show = FromData("preset-" + song.Key, "♪ " + song.Name, fast ? song.Data : song.SafeData);
        show.PresetKey = song.Key;
        show.FastData = song.Data;
        show.SafeData = song.SafeData;
        return show;
    }

    // ---------------------------------------------------------------- stapdata (compact tekstformaat)
    // Per stap "RRGGBB,helderheid,duur_ms,overgang_ms[,RRGGBB tweede helft[,flitsen|off]]", gescheiden door |

    public static ObservableCollection<LightStep> ParseData(string data)
    {
        var steps = new ObservableCollection<LightStep>();
        foreach (var part in (data ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = part.Split(',');
            if (f.Length < 4) throw new FormatException("Ongeldige stap: " + part);
            var dur = int.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture);
            if (f.Length > 5 && f[5] == "off") { steps.Add(LightStep.Off(dur)); continue; }
            var step = LightStep.Color("#" + f[0], dur, int.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture), 1,
                int.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture));
            if (f.Length > 4 && f[4].Length == 6) step.ColorHex2 = "#" + f[4];
            if (f.Length > 5 && int.TryParse(f[5], out var fl)) step.Flashes = fl;
            steps.Add(step);
        }
        return steps;
    }

    public static string ToData(IEnumerable<LightStep> steps) => string.Join("|", steps.Select(st =>
    {
        if (st.Kind == StepKind.Off) return $"000000,1,{st.DurationMs},0,,off";
        var s = $"{st.ColorHex[1..]},{st.Brightness},{st.DurationMs},{st.TransitionMs}";
        if (st.IsSplit || st.Flashes > 1) s += "," + (st.IsSplit ? st.ColorHex2[1..] : "");
        if (st.Flashes > 1) s += "," + st.Flashes;
        return s;
    }));

    public static LightShow FromData(string id, string name, string data) => new()
    {
        Id = id, Name = name, Repeats = 1, Steps = ParseData(data)
    };

    /// <summary>Presets met een snelle club-versie voor strips die direct springen.</summary>
    public static readonly IReadOnlySet<string> FastKeys = new HashSet<string> { Police, Party, Follow, Victory, BigGift, SmallGift, MediumGift, MegaGift };

    public static LightShow Create(string key) => Create(key, fast: false);

    public static LightShow Create(string key, bool fast)
    {
        var song = SongShows.All.FirstOrDefault(x => x.Key == key);
        if (song != null) return CreateSong(song, fast);
        if (fast && FastKeys.Contains(key)) return CreateFast(key);
        return CreateSafe(key);
    }

    /// <summary>Club-versies: ±4 wissels per seconde, strobes en flashbangs (alleen voor strips zonder fade).</summary>
    private static LightShow CreateFast(string key)
    {
        LightShow Show(string name, int repeats, params LightStep[] steps) => new()
        {
            Id = "preset-" + key, Name = name, Repeats = repeats, PresetKey = key,
            Steps = new ObservableCollection<LightStep>(steps)
        };
        LightStep C(string hex, int ms, int bri = 100, int flashes = 1, int fade = 0) => LightStep.Color(hex, ms, bri, flashes, fade);
        const string W = "#FFFFFF", K = "#000000";
        return key switch
        {
            Police => Show("Police", 4, C("#FF0000", 250), C("#0033FF", 250), C("#FF0000", 250), C("#0033FF", 250), C(W, 250), C(K, 250)),
            Party => Show("Party", 3, C("#FF00C8", 250), C("#00F0FF", 250), C("#FFE600", 250), C("#8B3DFF", 250), C("#7CFF00", 250), C(W, 250), C(K, 250)),
            Follow => Show("Follow", 1, C(W, 250), C("#3478F6", 1800, flashes: 3), C(W, 250), C("#3478F6", 500)),
            SmallGift => Show("Kleine gift", 1, C(W, 250), C("#FF5FA2", 1800, flashes: 3), C("#00E5FF", 400)),
            MediumGift => Show("Middelgrote gift", 2, C(W, 250), C("#00C2FF", 300), C("#FF7A00", 300), C("#9B5CFF", 300), C(K, 250), C("#00C2FF", 600, fade: 300)),
            BigGift => Show("Big Gift", 2,
                C(W, 250), C("#8B3DFF", 1200, flashes: 2), C(W, 250), C("#FF3DC8", 250), C("#FFC400", 250), C("#00C2FF", 250), C(K, 250), C(W, 250), C("#8B3DFF", 800, fade: 300)),
            Victory => Show("Victory", 2, C(W, 250), C("#FFC400", 2400, flashes: 4), C(W, 300), C("#FFC400", 1200, fade: 400)),
            MegaGift => Show("Speciale lichtshow", 2,
                C("#FF0000", 250), C("#00B7FF", 250), C("#FFE600", 250), C("#B026FF", 250),
                C("#00D84A", 250), C("#FF00C8", 250), C("#FF7A00", 250), C("#0047FF", 250),
                C(W, 250), C(K, 250), C(W, 250), C(K, 250),
                C("#FFC400", 2400, flashes: 4), C(W, 900, fade: 400)),
            _ => CreateSafe(key)
        };
    }

    private static LightShow CreateSafe(string key)
    {
        LightShow Show(string name, int repeats, params LightStep[] steps) => new()
        {
            Id = "preset-" + key, Name = name, Repeats = repeats, PresetKey = key,
            Steps = new ObservableCollection<LightStep>(steps)
        };
        LightStep C(string hex, int ms, int bri = 100, int flashes = 1, int fade = 0) => LightStep.Color(hex, ms, bri, flashes, fade);

        return key switch
        {
            Rainbow => Show("Rainbow", 2,
                C("#FF0000", 700, fade: 500), C("#FF8000", 700, fade: 500), C("#FFE600", 700, fade: 500),
                C("#00D84A", 700, fade: 500), C("#00B7FF", 700, fade: 500), C("#3F48FF", 700, fade: 500),
                C("#B026FF", 700, fade: 500)),
            Police => Show("Police", 3, C("#FF0000", 600), C("#0033FF", 600), C("#FF0000", 600), C("#0033FF", 600), C("#FFFFFF", 600)),
            Fire => Show("Fire", 3,
                C("#FF3D00", 700, 100, fade: 300), C("#FF8C00", 650, 80, fade: 250),
                C("#FFB000", 600, 100, fade: 200), C("#FF2200", 750, 70, fade: 300)),
            Ice => Show("Ice", 2,
                C("#BFF3FF", 1000, 90, fade: 700), C("#4FC3F7", 1000, 100, fade: 700),
                C("#FFFFFF", 700, 80, fade: 500), C("#1E6FE5", 1000, 100, fade: 700)),
            Party => Show("Party", 3,
                C("#FF00C8", 600), C("#00F0FF", 600), C("#FFE600", 600), C("#8B3DFF", 600), C("#7CFF00", 600)),
            Victory => Show("Victory", 2,
                C("#FFC400", 2400, flashes: 2), C("#FFFFFF", 700, fade: 300), C("#FFC400", 1200, fade: 400)),
            Follow => Show("Follow", 1, C("#3478F6", 2400, flashes: 2)),
            BigGift => Show("Big Gift", 2,
                C("#8B3DFF", 2400, flashes: 2), C("#FFC400", 700), C("#FF3DC8", 700),
                C("#00C2FF", 900, fade: 300), C("#8B3DFF", 900, fade: 400)),
            Like => Show("Like", 1, C("#2ECC71", 600)),
            Share => Show("Share", 1, C("#9B5CFF", 1400, fade: 350)),
            SmallGift => Show("Kleine gift", 1, C("#FF5FA2", 2200, flashes: 2)),
            MediumGift => Show("Middelgrote gift", 2, C("#00C2FF", 700), C("#FF7A00", 700), C("#9B5CFF", 900, fade: 300)),
            MegaGift => Show("Speciale lichtshow", 2,
                C("#FF0000", 600), C("#00B7FF", 600), C("#FFE600", 600),
                C("#B026FF", 600), C("#00D84A", 600), C("#FF00C8", 600),
                C("#FFC400", 3300, flashes: 3), C("#FFFFFF", 900, fade: 400)),
            _ => throw new ArgumentException("Onbekende preset: " + key)
        };
    }

    /// <summary>Standaardkoppelingen voor een nieuwe installatie (alleen als er nog geen koppelingen zijn).</summary>
    public static IEnumerable<EventTrigger> DefaultTriggers()
    {
        yield return new EventTrigger { Name = "Nieuwe follower", EventType = EventTypes.Follow, ShowId = "preset-" + Follow, Priority = Priorities.Normal, MaxInQueue = 3 };
        yield return new EventTrigger { Name = "Likes", EventType = EventTypes.Like, ShowId = "preset-" + Like, Priority = Priorities.Low, CooldownSeconds = 10, MaxInQueue = 1 };
        yield return new EventTrigger { Name = "Share", EventType = EventTypes.Share, ShowId = "preset-" + Share, Priority = Priorities.Normal, MaxInQueue = 2 };
        foreach (var t in GiftRules()) yield return t;
        foreach (var t in DonateWaveRules()) yield return t;
        foreach (var t in EntranceRules()) yield return t;
    }

    /// <summary>Binnenkomst-sounds in BetterTok: (sleutel, TikTok-naam, weergavenaam, duur van de sound in ms).</summary>
    public static readonly (string Id, string User, string Name, int SoundMs)[] Entrances =
    {
        ("thomas", "thomas", "Thomas", 28440),
        ("diego", "diego", "Diego", 31974),
    };

    /// <summary>Als deze kijker de LIVE binnenkomt speelt zijn eigen ♪-show (op zijn binnenkomst-sound in BetterTok).</summary>
    public static IEnumerable<EventTrigger> EntranceRules()
    {
        foreach (var (id, user, name, soundMs) in Entrances)
            yield return new EventTrigger
            {
                Id = "te-" + id, Name = $"♪ Binnenkomst {name}", EventType = EventTypes.Member, MatchText = user,
                ShowId = "preset-song-" + id, Priority = Priorities.VeryHigh, InterruptCurrent = false,
                CooldownSeconds = 60, MaxInQueue = 1, SoundDurationMs = soundMs
            };
    }

    /// <summary>
    /// Gift-regels gelijk aan de BetterTok Sound Alerts (Actions) van de streamer: een gift speelt de actie met de
    /// hoogste coin-drempel die hij haalt. Waar een dono-sound bij hoort, speelt de bijpassende ♪-song-show.
    /// </summary>
    public static IEnumerable<EventTrigger> GiftRules()
    {
        EventTrigger R(string id, string name, int min, int? max, string show, int prio) => new()
        {
            Id = "tf-" + id, Name = name, EventType = EventTypes.Gift, MinCoins = min, MaxCoins = max,
            ShowId = "preset-" + show, Priority = prio, InterruptCurrent = false   // shows spelen achter elkaar, net als de sounds
        };
        yield return R("1", "Gift 1–89 coins", 1, 89, SmallGift, Priorities.Normal);
        yield return R("justthewayyouare", "♪ Just the way you are (≥ 90 coins)", 90, 99, "song-justthewayyouare", Priorities.High);
        yield return R("kickpong", "♪ Kickpong (≥ 100 coins)", 100, 198, "song-kickpong", Priorities.High);
        yield return R("199", "Gift 199 coins (199 muziek)", 199, 199, MediumGift, Priorities.High);
        yield return R("incomingadjuzt", "♪ Incoming (Adjuzt) (≥ 200 coins)", 200, 298, "song-incomingadjuzt", Priorities.High);
        yield return R("hitthegas", "♪ Hit the gas (≥ 299 coins)", 299, 498, "song-hitthegas", Priorities.High);
        yield return R("surgingsparks", "♪ Surging Sparks (≥ 499 coins)", 499, 599, "song-surgingsparks", Priorities.VeryHigh);
        yield return R("krachwhore", "♪ Krachwhore (≥ 600 coins)", 600, 998, "song-krachwhore", Priorities.VeryHigh);
        yield return R("numb", "♪ Numb (≥ 999 coins)", 999, 1499, "song-numb", Priorities.VeryHigh);
        yield return R("1500", "♪ Pyrotechnik (Noiseflow) (≥ 1500 coins)", 1500, 1999, "song-pyrotechnik", Priorities.VeryHigh);
        yield return R("sweetzaagoline", "♪ Sweet zaagoline (≥ 2000 coins)", 2000, 3998, "song-sweetzaagoline", Priorities.Critical);
        yield return R("shivers", "♪ Noah – Shivers (≥ 3999 coins)", 3999, 4999, "song-shivers", Priorities.Critical);
        yield return R("5000", "♪ Dual Damage – Everywhere I go (≥ 5000 coins)", 5000, 5999, "song-dualdamage", Priorities.Critical);
        yield return R("mirror", "♪ Mirror (≥ 6000 coins)", 6000, 9999, "song-mirror", Priorities.Critical);
        yield return R("10000", "♪ Fireflies remix (≥ 10000 coins)", 10000, 14999, "song-fireflies", Priorities.Critical);
        yield return R("runaway", "♪ Runaway (Aura Dono sound, ≥ 15000 coins)", 15000, 29999, "song-runaway", Priorities.Critical);
        yield return R("30000", "♪ Titanium (Rebelion) (≥ 30000 coins)", 30000, null, "song-titanium", Priorities.Critical);
        // Gifts met een eigen dono-sound in BetterTok: de giftnaam gaat altijd voor het coin-bereik
        foreach (var (id, name, gifts) in GiftSongs)
        {
            var g = R(id, $"♪ {SongShows.All.First(x => x.Key == "song-" + id).Name} ({gifts.Replace(",", " /")})", 0, null, "song-" + id, Priorities.Critical);
            g.MinCoins = null;
            g.GiftNames = gifts;
            yield return g;
        }
    }

    /// <summary>DonateWave-tips, gelijk aan DonateWave › Alert-effecten (bedragen in eurocenten):
    /// € 1–9,99 = SaveTik-sound, € 10–29,99 = B.O.A., € 30+ = All I want for Christmas (Lunos).</summary>
    public static IEnumerable<EventTrigger> DonateWaveRules()
    {
        EventTrigger T(string id, string name, int? min, int? max, string key, int prio) => new()
        {
            Id = "tw-" + id, Name = name, EventType = EventTypes.Tip, MinCoins = min, MaxCoins = max,
            ShowId = "preset-" + key, Priority = prio, InterruptCurrent = false
        };
        yield return T("1", "♪ DonateWave € 0,01 – € 9,99 (SaveTik-sound)", 1, 999, "song-savetik", Priorities.VeryHigh);
        yield return T("10", "♪ DonateWave € 10 – € 29,99 (B.O.A.)", 1000, 2999, "song-boa", Priorities.Critical);
        yield return T("30", "♪ DonateWave € 30 en hoger (All I want for Christmas)", 3000, null, "song-christmaslunos", Priorities.Critical);
        var so = T("shoutout", "DonateWave shout-out (overige)", null, null, Party, Priorities.Normal);
        so.TipKind = TipKinds.Shoutout;
        yield return so;
        foreach (var (id, name, cents, animMs, song, prio) in Specials)
        {
            var t = T("special-" + id, $"♪ Shout-out {name} € {cents / 100} → {SongShows.All.First(x => x.Key == "song-" + song).Name}", cents, cents, "song-" + song, prio);
            t.TipKind = TipKinds.Shoutout;
            t.MatchText = name;                // als DonateWave de naam meestuurt, kiest de app op naam
            t.StartDelayMs = animMs;           // de extra sound (en dus de show) begint pas na de animatie
            yield return t;
        }
    }

    /// <summary>
    /// DonateWave › Speciale shout-outs: naam, prijs, duur van de animatievideo (gemeten op de ms; de overlay start de
    /// extra sound op het einde van de video, niet van de soundtrack)
    /// en de "extra sound na de animatie". Twee specials met dezelfde prijs zijn via de API niet uit elkaar te
    /// houden (alleen het bedrag komt mee); de hoogste prioriteit wint dan.
    /// </summary>
    public static readonly (string Id, string Name, int Cents, int AnimMs, string Song, int Priority)[] Specials =
    {
        ("desertking", "Desert King", 50000, 10560, "pumpit", Priorities.Critical),
        ("royalvoyage", "Royal Voyage", 2000, 6374, "imaginary", Priorities.Critical),
        ("goldenrush", "Golden Rush", 5000, 7053, "banger", Priorities.Critical),
        ("starlightkingdom", "Starlight Kingdom", 1000, 6060, "sound2708", Priorities.Critical),
        ("spikygroove", "Spiky Groove", 500, 7500, "helpless", Priorities.Critical),
        ("enchantedhat", "Enchanted Hat", 800, 7300, "burningskies", Priorities.Critical),
        ("infernoheir", "Inferno Heir", 10000, 8000, "moveitfull", Priorities.Critical),
        ("lionscrown", "Lion's Crown", 8000, 7766, "paradise", Priorities.Critical),
        ("acornfortune", "Acorn Fortune", 1000, 6600, "oceans", Priorities.VeryHigh),
        ("cosmiclaunch", "Cosmic Launch", 5000, 7366, "citylights", Priorities.VeryHigh),
    };

    /// <summary>Gifts die in BetterTok een eigen dono-sound hebben (song-sleutel, giftnaam/-namen).</summary>
    public static readonly (string Id, string Name, string Gifts)[] GiftSongs =
    {
        ("nevercomingdown", "Never Coming Down", "Diamantboom"),
        ("partyrock", "Party Rock Remix", "Vuurwerk"),
        ("sogoodtobebad", "So good to be bad", "Sportwagen"),
        ("onekiss", "One kiss", "Hartjes"),
        ("f1hardstyle", "F1 hardstyle remix", "Sterke finish,Racedebuut"),
        ("fuckthehaters", "F#ck the haters", "Super GG"),
        ("cancan", "Can can", "Misha-beer"),
        ("soincredible", "So Incredible", "Sterrenstelsel"),
        ("elsonidito", "El sonidito", "Hoed en snor"),
        ("outsiders", "1 team Outsiders", "Side by Side"),
        ("feelinhigh", "Feelin' high", "Harthanden"),
    };
}
