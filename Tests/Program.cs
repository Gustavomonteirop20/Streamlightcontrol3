using System.Collections.ObjectModel;
using System.Diagnostics;
using StreamLightControl2.Core;
using StreamLightControl2.Core.Models;
using StreamLightControl2.Core.Services;
using StreamLightControl2.Tests;

// ======================================================================================
//  Stream Light Control – testrunner voor de kernlogica
//  Uitvoeren:  dotnet run  (in de map Tests)
// ======================================================================================

var tests = new List<(string name, Func<Task> run)>();
void Test(string name, Func<Task> run) => tests.Add((name, run));
void TestSync(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));

static void Eq<T>(T expected, T actual, string what = "")
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{what} verwacht '{expected}', kreeg '{actual}'");
}
static void True(bool cond, string what) { if (!cond) throw new Exception(what); }
static async Task WaitFor(Func<bool> cond, int timeoutMs, string what)
{
    var sw = Stopwatch.StartNew();
    while (!cond()) { if (sw.ElapsedMilliseconds > timeoutMs) throw new Exception("Time-out: " + what); await Task.Delay(20); }
}
static string TempDir() { var d = Path.Combine(Path.GetTempPath(), "slc-test-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return d; }
static AppSettings DefaultSettings() { var s = new AppSettings(); SettingsStore.EnsureDefaults(s, true); s.Lighting.ShowBrightness = 100; s.Spotify.Rave = RaveMode.Never; return s; }

// ---------------------------------------------------------------- BetterTok parser

TestSync("Parser: Local follow", () =>
{
    var f = BetterTokEventParser.Parse("{\"event\":\"follow\",\"data\":{\"uniqueId\":\"anna_01\",\"nickname\":\"Anna\",\"msgId\":\"7311\"}}");
    Eq(FrameKind.Event, f.Kind);
    Eq(EventTypes.Follow, f.Event!.Type);
    Eq("anna_01", f.Event.User);
    Eq("Anna", f.Event.UserLabel);
    Eq(false, f.Event.FromCloud);
});

TestSync("Parser: Local gift-combo bezig (giftType 1, repeatEnd false)", () =>
{
    var e = BetterTokEventParser.Parse("{\"event\":\"gift\",\"data\":{\"uniqueId\":\"bob\",\"giftName\":\"Rose\",\"giftId\":5655,\"giftType\":1,\"repeatCount\":3,\"repeatEnd\":false,\"diamondCount\":1,\"msgId\":\"99\"}}").Event!;
    Eq(true, e.StreakInProgress, "streak");
    Eq(3L, e.TotalCoins, "coins");
});

TestSync("Parser: Local gift-combo klaar → coins = diamondCount × repeatCount", () =>
{
    var e = BetterTokEventParser.Parse("{\"event\":\"gift\",\"data\":{\"uniqueId\":\"bob\",\"giftName\":\"Doughnut\",\"giftType\":1,\"repeatCount\":5,\"repeatEnd\":true,\"diamondCount\":30,\"msgId\":\"100\"}}").Event!;
    Eq(false, e.StreakInProgress);
    Eq(150L, e.TotalCoins);
    Eq(5, e.GiftCount);
    Eq("Doughnut", e.GiftName);
});

TestSync("Parser: Cloud gift (voorbeeld uit de BetterTok-docs)", () =>
{
    var raw = "{\"v\":1,\"id\":\"evt_9f1c0f7a-1\",\"type\":\"gift\",\"data\":{\"username\":\"yourhandle\",\"viewer_id\":\"6829471055\",\"viewer_username\":\"someviewer\",\"display_name\":\"Some Viewer\",\"gift_id\":5655,\"gift_name\":\"Rose\",\"gift_count\":10,\"gift_value\":1,\"gift_total_value\":10}}";
    var e = BetterTokEventParser.Parse(raw).Event!;
    Eq(EventTypes.Gift, e.Type);
    Eq(10L, e.TotalCoins);
    Eq(10, e.GiftCount);
    Eq("evt_9f1c0f7a-1", e.DedupeKey);
    Eq("someviewer", e.User, "Cloud gebruikt viewer_username (username = creator)");
    Eq(true, e.FromCloud);
});

TestSync("Parser: Local/Cloud-namen worden genormaliseerd", () =>
{
    Eq(EventTypes.Comment, BetterTokEventParser.Parse("{\"event\":\"chat\",\"data\":{\"comment\":\"hoi\"}}").Event!.Type);
    Eq(EventTypes.Member, BetterTokEventParser.Parse("{\"event\":\"member\",\"data\":{}}").Event!.Type);
    Eq(EventTypes.Member, BetterTokEventParser.Parse("{\"type\":\"join\",\"id\":\"evt_2\",\"data\":{}}").Event!.Type);
    Eq(EventTypes.Subscribe, BetterTokEventParser.Parse("{\"type\":\"superfan\",\"id\":\"evt_3\",\"data\":{}}").Event!.Type);
    Eq(EventTypes.Follow, BetterTokEventParser.Parse("{\"event\":\"social\",\"data\":{\"displayType\":\"pm_main_follow_message_viewer_2\"}}").Event!.Type);
    Eq(EventTypes.Share, BetterTokEventParser.Parse("{\"event\":\"social\",\"data\":{\"displayType\":\"pm_mt_guidance_share\"}}").Event!.Type);
    Eq(25, BetterTokEventParser.Parse("{\"event\":\"like\",\"data\":{\"likeCount\":25}}").Event!.LikeCount);
});

TestSync("Parser: control frames (connected / pong / error)", () =>
{
    Eq(FrameKind.Connected, BetterTokEventParser.Parse("{\"type\":\"connected\",\"username\":\"x\"}").Kind);
    Eq(FrameKind.Pong, BetterTokEventParser.Parse("{\"type\":\"pong\"}").Kind);
    var err = BetterTokEventParser.Parse("{\"type\":\"error\",\"code\":\"TIER_REQUIRED\",\"message\":\"Creator plan\"}");
    Eq(FrameKind.Error, err.Kind);
    Eq("TIER_REQUIRED", err.ErrorCode);
});

// ---------------------------------------------------------------- Regels

TestSync("Regels: gift-regels gelijk aan BetterTok – juiste song per coin-bedrag", () =>
{
    var s = DefaultSettings();
    string Pick(long coins, string gift = "X") => s.FindShow(RuleEngine.SelectTrigger(StreamEvent.Test(EventTypes.Gift, gift, coins), s.Triggers)!.ShowId)!.Name;
    Eq("Kleine gift", Pick(1));
    Eq("Kleine gift", Pick(89));
    Eq("♪ Just the way you are", Pick(90));
    Eq("♪ Kickpong", Pick(100));
    Eq("♪ Kickpong", Pick(198));
    Eq("Middelgrote gift", Pick(199), "199 muziek");
    Eq("♪ Incoming (Adjuzt)", Pick(200));
    Eq("♪ Hit the gas", Pick(299));
    Eq("♪ Hit the gas", Pick(498));
    Eq("♪ Surging Sparks", Pick(499));
    Eq("♪ Krachwhore", Pick(600));
    Eq("♪ Krachwhore", Pick(998));
    Eq("♪ Numb", Pick(999));
    Eq("♪ Pyrotechnik (Noiseflow)", Pick(1500));
    Eq("♪ Sweet zaagoline", Pick(2000));
    Eq("♪ Noah – Shivers", Pick(3999));
    Eq("♪ Dual Damage – Everywhere I go", Pick(5000));
    Eq("♪ Mirror", Pick(6000));
    Eq("♪ Mirror", Pick(9999));
    Eq("♪ Fireflies remix", Pick(10000));
    Eq("♪ Runaway", Pick(15000), "Aura Dono sound");
    Eq("♪ Runaway", Pick(29999));
    Eq("♪ Titanium (Rebelion)", Pick(30000));
    Eq("♪ Never Coming Down", Pick(3000, "Diamantboom"), "Diamantboom gaat altijd voor");
    Eq("♪ Party Rock Remix", Pick(1, "Vuurwerk"));
    Eq("♪ So good to be bad", Pick(1, "Sportwagen"));
    Eq("♪ One kiss (Cyber Gunz)", Pick(1, "Hartjes"));
    Eq("♪ F1 hardstyle remix", Pick(1, "Sterke finish"));
    Eq("♪ F1 hardstyle remix", Pick(1, "Racedebuut"));
    Eq("♪ F#ck the haters (Multilator)", Pick(1, "Super GG"));
    Eq("♪ Can can", Pick(1, "Misha-beer"));
    Eq("♪ So Incredible", Pick(1, "Sterrenstelsel"));
    Eq("♪ El sonidito", Pick(1, "Hoed en snor"));
    Eq("♪ 1 team (Outsiders)", Pick(1, "Side by Side"));
    Eq("♪ Feelin' high (Krowdexx)", Pick(1, "Harthanden"));
    Eq(0, RuleEngine.ValidateGiftRules(s).Count, "geen overlap of gaten");
});

TestSync("Opslag: versie 4 → 5 vervangt oude coin-bereiken door de BetterTok-regels", () =>
{
    var s = new AppSettings { Version = 4 };
    s.Triggers.Add(new EventTrigger { Id = "tf-10000", Name = "Gift 10000+ coins", EventType = EventTypes.Gift, MinCoins = 10000, ShowId = "preset-megagift" });
    s.Triggers.Add(new EventTrigger { Name = "Gift 100–499 coins", EventType = EventTypes.Gift, MinCoins = 100, MaxCoins = 499 });
    s.Triggers.Add(new EventTrigger { Name = "Mijn Rose", EventType = EventTypes.Gift, GiftNames = "Rose", ShowId = "preset-police" });
    SettingsStore.Migrate(s);
    SettingsStore.EnsureDefaults(s, false);
    Eq(28, s.Version);
    True(s.Triggers.All(t => t.Name != "Gift 100–499 coins"), "oude standaard weg");
    Eq(14999, s.Triggers.Single(t => t.Id == "tf-10000").MaxCoins, "bestaande regel bijgewerkt");
    True(s.Triggers.Any(t => t.Name == "Mijn Rose"), "eigen regel blijft");
    Eq(26, s.Triggers.Count(t => t.Name.StartsWith("♪") && t.EventType == EventTypes.Gift), "26 dono-sounds gekoppeld");
    Eq(0, RuleEngine.ValidateGiftRules(s).Count);
    SettingsStore.Migrate(s); // nogmaals: niets dubbel
    Eq(26, s.Triggers.Count(t => t.Name.StartsWith("♪") && t.EventType == EventTypes.Gift));
});

TestSync("Regels: specifieke giftnaam gaat voor coin-bereik", () =>
{
    var s = DefaultSettings();
    s.Triggers.Add(new EventTrigger { Name = "Rose/Heart", EventType = EventTypes.Gift, GiftNames = "Rose, Heart Me", ShowId = "preset-police", Priority = 1 });
    var t = RuleEngine.SelectTrigger(StreamEvent.Test(EventTypes.Gift, "rose", 1), s.Triggers)!;
    Eq("Rose/Heart", t.Name);
    Eq("Rose/Heart", RuleEngine.SelectTrigger(StreamEvent.Test(EventTypes.Gift, "Heart Me", 1), s.Triggers)!.Name, "meerdere gifts → zelfde show");
    Eq("Gift 1–89 coins", RuleEngine.SelectTrigger(StreamEvent.Test(EventTypes.Gift, "Finger Heart", 5), s.Triggers)!.Name);
});

TestSync("Regels: uitgeschakelde koppeling wordt overgeslagen", () =>
{
    var s = DefaultSettings();
    foreach (var t in s.Triggers.Where(t => t.EventType == EventTypes.Follow)) t.Enabled = false;
    Eq(null, RuleEngine.SelectTrigger(StreamEvent.Test(EventTypes.Follow), s.Triggers));
});

TestSync("Regels: dubbel bericht, follow-herhaling en bundelen", () =>
{
    var s = DefaultSettings();
    s.Follow.GroupWindowSeconds = 0;
    var r = new RuleEngine();
    var now = new DateTime(2026, 1, 1, 20, 0, 0);
    r.Clock = () => now;
    StreamEvent Follow(string user, string id) => new() { Type = EventTypes.Follow, User = user, DedupeKey = id };
    Eq(DecisionKind.Play, r.Evaluate(Follow("a", "1"), s, _ => 0).Kind);
    Eq(DecisionKind.Duplicate, r.Evaluate(Follow("a", "1"), s, _ => 0).Kind);
    Eq(DecisionKind.FollowRepeat, r.Evaluate(Follow("a", "2"), s, _ => 0).Kind);
    Eq(DecisionKind.Play, r.Evaluate(Follow("b", "3"), s, _ => 0).Kind);
    s.Follow.GroupWindowSeconds = 5;
    now = now.AddSeconds(2);
    Eq(DecisionKind.FollowGrouped, r.Evaluate(Follow("c", "4"), s, _ => 0).Kind);
    now = now.AddSeconds(10);
    Eq(DecisionKind.Play, r.Evaluate(Follow("d", "5"), s, _ => 0).Kind);
});

TestSync("Regels: likes optellen tot drempel + cooldown", () =>
{
    var s = DefaultSettings();
    var like = s.Triggers.First(t => t.EventType == EventTypes.Like);
    like.LikeThreshold = 50;
    like.CooldownSeconds = 10;
    var r = new RuleEngine();
    var now = DateTime.Now;
    r.Clock = () => now;
    StreamEvent L(int n) => new() { Type = EventTypes.Like, LikeCount = n, DedupeKey = Guid.NewGuid().ToString() };
    Eq(DecisionKind.BelowThreshold, r.Evaluate(L(30), s, _ => 0).Kind);
    Eq(DecisionKind.Play, r.Evaluate(L(30), s, _ => 0).Kind);
    Eq(DecisionKind.BelowThreshold, r.Evaluate(L(10), s, _ => 0).Kind);
    Eq(DecisionKind.Cooldown, r.Evaluate(L(60), s, _ => 0).Kind);
    now = now.AddSeconds(11);
    Eq(DecisionKind.Play, r.Evaluate(L(60), s, _ => 0).Kind);
});

TestSync("Regels: combo bezig wordt niet afgespeeld, Cloud-naschot samengevoegd, wachtrijlimiet", () =>
{
    var s = DefaultSettings();
    var r = new RuleEngine();
    Eq(DecisionKind.StreakInProgress, r.Evaluate(new StreamEvent { Type = EventTypes.Gift, StreakInProgress = true, TotalCoins = 5, DedupeKey = "s1" }, s, _ => 0).Kind);
    var cloud1 = new StreamEvent { Type = EventTypes.Gift, FromCloud = true, User = "v", GiftId = "5655", GiftName = "Rose", TotalCoins = 10, DedupeKey = "evt_a" };
    var cloud2 = new StreamEvent { Type = EventTypes.Gift, FromCloud = true, User = "v", GiftId = "5655", GiftName = "Rose", TotalCoins = 3, DedupeKey = "evt_b" };
    Eq(DecisionKind.Play, r.Evaluate(cloud1, s, _ => 0).Kind);
    Eq(DecisionKind.ComboRemainder, r.Evaluate(cloud2, s, _ => 0).Kind);
    var follow = s.Triggers.First(t => t.EventType == EventTypes.Follow);
    Eq(DecisionKind.QueueLimit, r.Evaluate(new StreamEvent { Type = EventTypes.Follow, User = "z", DedupeKey = "f9" }, s, id => id == follow.Id ? 3 : 0).Kind);
});

TestSync("Regelbouwer: waarschuwt voor overlap en gaten", () =>
{
    var s = DefaultSettings();
    Eq(0, RuleEngine.ValidateGiftRules(s).Count, "standaardregels zijn netjes");
    s.Triggers.First(t => t.Id == "tf-199").MinCoins = 150;
    True(RuleEngine.ValidateGiftRules(s).Any(w => w.Contains("overlappen")), "overlap gemeld");
    s.Triggers.First(t => t.Id == "tf-199").MinCoins = 199;
    s.Triggers.First(t => t.Id == "tf-incomingadjuzt").MinCoins = 250;
    True(RuleEngine.ValidateGiftRules(s).Any(w => w.Contains("Gat")), "gat gemeld");
});

// ---------------------------------------------------------------- Opslag

TestSync("Opslag: settings.json van versie 2.0 wordt omgezet", () =>
{
    var dir = TempDir();
    File.WriteAllText(Path.Combine(dir, "settings.json"), """
    {
      "BetterTokUrl": "ws://127.0.0.1:21215/",
      "GoveeIp": "192.168.1.42",
      "GoveeSku": "H619E",
      "GoveeName": "Govee LED-strip",
      "Rules": [
        { "EventName": "follow", "MatchText": "", "DisplayName": "Follow blauw", "ColorHex": "#3478F6", "Brightness": 90, "Flashes": 2, "FlashDurationMs": 400, "Enabled": true },
        { "EventName": "gift", "MatchText": "Rose", "DisplayName": "Rose", "ColorHex": "#FF3B5C", "Brightness": 80, "Flashes": 1, "FlashDurationMs": 500, "Enabled": false }
      ]
    }
    """);
    var store = new SettingsStore(dir);
    var s = store.Load();
    True(store.Migrated, "gemigreerd");
    Eq("ws://127.0.0.1:21215/", s.BetterTok.LocalUrl);
    Eq("192.168.1.42", s.Devices.Single().Ip);
    var follow = s.Triggers.First(t => t.Name == "Follow blauw");
    var show = s.FindShow(follow.ShowId)!;
    Eq("#3478F6", show.Steps[0].ColorHex);
    Eq(2, show.Steps[0].Flashes);
    Eq(800, show.Steps[0].DurationMs);
    var rose = s.Triggers.First(t => t.Name == "Rose");
    Eq("Rose", rose.GiftNames);
    Eq(false, rose.Enabled);
    Eq(Presets.AllKeys.Count, s.Shows.Count(x => x.IsPreset), "presets toegevoegd");
    var text = File.ReadAllText(Path.Combine(dir, "settings.json"));
    True(!text.Contains("\"Rules\""), "oude velden verwijderd na opslaan");
    store.Dispose();
});

TestSync("Opslag: alles blijft bewaard na herstart", () =>
{
    var dir = TempDir();
    var a = new SettingsStore(dir);
    var s = a.Load();
    var show = new LightShow { Name = "Mijn show", Repeats = 3, Steps = new ObservableCollection<LightStep> { LightStep.Color("#112233", 700, 55, 3, 0) } };
    s.Shows.Add(show);
    s.Triggers.Add(new EventTrigger { Name = "Lion", EventType = EventTypes.Gift, GiftNames = "Lion", ShowId = show.Id, Priority = 5, CooldownSeconds = 7 });
    s.Devices.Add(new GoveeDeviceConfig { Name = "Bureau", Ip = "10.0.0.5", Enabled = false });
    s.General.SafeMode = true;
    s.Lighting.FlashMode = FlashMode.Black;
    s.Dashboard.VisibleStats = new() { "coins", "likes" };
    Eq(true, a.SaveNow(), "opgeslagen");
    a.Dispose();

    var b = new SettingsStore(dir);
    var t = b.Load();
    var show2 = t.Shows.First(x => x.Name == "Mijn show");
    Eq(3, show2.Repeats);
    Eq("#112233", show2.Steps[0].ColorHex);
    Eq(3, show2.Steps[0].Flashes);
    var lion = t.Triggers.First(x => x.Name == "Lion");
    Eq(show2.Id, lion.ShowId);
    Eq(7, lion.CooldownSeconds);
    Eq(false, t.Devices.First(d => d.Name == "Bureau").Enabled);
    Eq(true, t.General.SafeMode);
    Eq(FlashMode.Black, t.Lighting.FlashMode);
    Eq("coins,likes", string.Join(",", t.Dashboard.VisibleStats));
    b.Dispose();
});

Test("Opslag: automatisch opslaan na een wijziging", async () =>
{
    var dir = TempDir();
    var store = new SettingsStore(dir);
    var s = store.Load();
    var saved = 0;
    store.Saved += (ok, _) => { if (ok) saved++; };
    s.Triggers[0].Name = "Hernoemd";
    await WaitFor(() => saved > 0, 3000, "auto-save");
    True(File.ReadAllText(store.SettingsPath).Contains("Hernoemd"), "wijziging staat in bestand");
    store.Dispose();
});

TestSync("Opslag: beschadigd bestand → back-up en waarschuwing", () =>
{
    var dir = TempDir();
    File.WriteAllText(Path.Combine(dir, "settings.json"), "{ dit is geen json");
    var store = new SettingsStore(dir);
    store.Load();
    True(store.LoadWarning != null, "waarschuwing");
    True(Directory.GetFiles(dir, "settings.json.kapot-*").Length == 1, "back-up gemaakt");
    store.Dispose();
});

TestSync("Opslag: API-key wordt niet als leesbare tekst bewaard", () =>
{
    var p = SecretProtector.Protect("btk_live_abcdef123456");
    True(!p.Contains("btk_live_abcdef123456"), "niet leesbaar");
    Eq("btk_live_abcdef123456", SecretProtector.Unprotect(p));
    Eq("btk_live_••••••••3456", SecretProtector.Mask("btk_live_abcdef123456"));
});

// ---------------------------------------------------------------- Govee + engine (nagebootste lamp)

const int ScanPort = 24001, ListenPort = 24002, ControlPort = 24003;
GoveeLanClient NewGovee()
{
    var g = new GoveeLanClient { ScanPort = ScanPort, ListenPort = ListenPort, ControlPort = ControlPort, UseMulticast = false, CommandDelayMs = 30 };
    g.ExtraScanTargets.Add(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, ScanPort));
    return g;
}

Test("Govee: zoeken, status opvragen en bedienen via LAN-protocol", async () =>
{
    using var lamp = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort);
    using var govee = NewGovee();
    var found = await govee.DiscoverAsync(800);
    Eq(1, found.Count, "gevonden");
    Eq("H619E", found[0].Sku);
    Eq(lamp.Mac, found[0].Mac);
    var status = await govee.GetStatusAsync("127.0.0.1", 1500);
    True(status != null, "status ontvangen");
    Eq(40, status!.Brightness);
    await govee.ColorAsync("127.0.0.1", 10, 20, 30);
    await govee.BrightnessAsync("127.0.0.1", 77);
    await Task.Delay(100);
    Eq((10, 20, 30), lamp.Color);
    Eq(77, lamp.Brightness);
    lamp.RespondToStatus = false;
    Eq(null, await govee.GetStatusAsync("127.0.0.1", 600), "geen antwoord → null");
});

Test("Engine: Follow-preset = 2× blauw knipperen, timing klopt", async () =>
{
    using var lamp = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort);
    using var govee = NewGovee();
    var opts = new LightingOptions { ShowBrightness = 100, CommandDelayMs = 30, FlashMode = FlashMode.PowerToggle };
    var engine = new LightShowEngine(govee, () => opts);
    var frames = new List<LightFrame>();
    engine.Frame += f => { lock (frames) frames.Add(f); };
    var sw = Stopwatch.StartNew();
    await engine.PlayAsync(Presets.Create(Presets.Follow), new[] { "127.0.0.1" }, CancellationToken.None);
    sw.Stop();
    await Task.Delay(100);
    var colors = lamp.ColorHistory();
    Eq(2, colors.Count(c => c == "#3478F6"), "2 flitsen, laatste blijft staan");
    True(lamp.Commands.Count(c => c.cmd == "turn" && c.data.GetProperty("value").GetInt32() == 0) >= 1, "lamp gaat tussendoor uit");
    True(sw.ElapsedMilliseconds is >= 2300 and < 3000, $"duur {sw.ElapsedMilliseconds} ms ≈ 2400 ms");
    True(frames.Count >= 3, "preview kreeg frames");
});

Test("Engine: overgang (fade) stuurt tussenkleuren", async () =>
{
    using var lamp = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort);
    using var govee = NewGovee();
    var opts = new LightingOptions { ShowBrightness = 100, CommandDelayMs = 30, MinChangeMs = 100 };
    var engine = new LightShowEngine(govee, () => opts);
    var show = new LightShow { Steps = new ObservableCollection<LightStep> { LightStep.Color("#000000", 200), LightStep.Color("#FF0000", 700, transitionMs: 600) } };
    await engine.PlayAsync(show, new[] { "127.0.0.1" }, CancellationToken.None);
    await Task.Delay(100);
    var reds = lamp.ColorHistory().Skip(1).ToList();
    True(reds.Count >= 3, $"meerdere tussenstappen ({reds.Count}), rustig genoeg voor de strip");
    Eq("#FF0000", reds.Last());
});

Test("Engine: toestand van vóór de show wordt hersteld", async () =>
{
    using var lamp = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort);
    using var govee = NewGovee();
    govee.StartListener();
    var opts = new LightingOptions { ShowBrightness = 100, CommandDelayMs = 30, RestoreMode = RestoreMode.Previous };
    var engine = new LightShowEngine(govee, () => opts);
    var ips = new[] { "127.0.0.1" };
    var baseline = await engine.CaptureAsync(ips);
    Eq(1, baseline.States.Count, "baseline");
    await engine.PlayAsync(Presets.Create(Presets.Like), ips, CancellationToken.None);
    await engine.RestoreAsync(baseline, ips);
    await Task.Delay(150);
    Eq((255, 255, 255), lamp.Color, "kleur terug");
    Eq(40, lamp.Brightness, "helderheid terug");
});

Test("Engine: Police wisselt rood/blauw in een tempo dat de strip aankan", async () =>
{
    using var lamp = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort);
    using var govee = NewGovee();
    var opts = new LightingOptions { ShowBrightness = 100, CommandDelayMs = 80 };
    Eq(FlashMode.Black, opts.FlashMode, "standaard snelle modus");
    var engine = new LightShowEngine(govee, () => opts);
    var police = Presets.Create(Presets.Police);
    police.Repeats = 1;
    var sw = Stopwatch.StartNew();
    await engine.PlayAsync(police, new[] { "127.0.0.1" }, CancellationToken.None);
    sw.Stop();
    await Task.Delay(150);
    var colors = lamp.ColorHistory();
    Eq(2, colors.Count(c => c == "#FF0000"), "rood 2×");
    Eq(2, colors.Count(c => c == "#0033FF"), "blauw 2×");
    Eq(1, colors.Count(c => c == "#FFFFFF"), "witte klap");
    Eq(0, lamp.Commands.Count(c => c.cmd == "turn" && c.data.GetProperty("value").GetInt32() == 0), "geen trage uit/aan-commando's");
    True(Math.Abs(sw.ElapsedMilliseconds - police.TotalDurationMs) < 250, $"timing {sw.ElapsedMilliseconds} ≈ {police.TotalDurationMs} ms");
});

TestSync("Presets: geen flitsfase sneller dan de standaard snelheidslimiet", () =>
{
    var limit = new LightingOptions().MinChangeMs * 0.8;
    foreach (var key in Presets.AllKeys.Where(k => !k.StartsWith("song-")))
        foreach (var st in Presets.Create(key).Steps)
        {
            var phase = st.Flashes > 1 ? st.DurationMs / (double)st.Flashes * 0.45 : st.DurationMs;
            True(phase >= limit, $"{key}: fase {phase:0} ms < {limit:0} ms");
        }
});

TestSync("Song-shows: alle 49 aanwezig (47 dono + 2 binnenkomst), lengte gelijk aan de sound, uitvoerbaar", () =>
{
    var expected = new Dictionary<string, double>
    {
        ["song-runaway"] = 56.79, ["song-numb"] = 38.35, ["song-justthewayyouare"] = 27.74, ["song-mirror"] = 73.35,
        ["song-shivers"] = 81.74, ["song-microwave"] = 16.67, ["song-mysterybag"] = 18.96, ["song-krachwhore"] = 53.58,
        ["song-numbepic"] = 47.54, ["song-nevercomingdown"] = 43.81, ["song-surgingsparks"] = 24.06, ["song-moveit"] = 18.96,
        ["song-somebody"] = 86.83, ["song-sweetzaagoline"] = 19.44, ["song-kickpong"] = 34.77,
        ["song-c4"] = 42.79, ["song-cancan"] = 29.23, ["song-dualdamage"] = 35.57, ["song-elsonidito"] = 24.58, ["song-f1hardstyle"] = 25.78, ["song-feelinhigh"] = 34.51, ["song-fireflies"] = 65.72, ["song-fuckthehaters"] = 29.6, ["song-hitthegas"] = 33.67, ["song-incomingadjuzt"] = 35.66, ["song-onekiss"] = 29.91, ["song-outsiders"] = 28.86, ["song-partyrock"] = 22.31, ["song-pyrotechnik"] = 26.67, ["song-sogoodtobebad"] = 67.0, ["song-soincredible"] = 26.02, ["song-titanium"] = 51.38,
        ["song-banger"] = 28.5, ["song-boa"] = 19.7, ["song-burningskies"] = 27.64, ["song-christmaslunos"] = 40.75, ["song-citylights"] = 34.13, ["song-deadlyguns"] = 13.54, ["song-helpless"] = 29.71, ["song-imaginary"] = 31.97, ["song-moveitfull"] = 21.8, ["song-oceans"] = 27.08, ["song-paradise"] = 22.65, ["song-pumpit"] = 35.7, ["song-savetik"] = 27.11, ["song-sound2708"] = 24.83, ["song-twilightzone"] = 46.21,
        ["song-thomas"] = 28.4, ["song-diego"] = 31.97
    };
    Eq(49, SongShows.All.Length, "één show per sound");
    foreach (var (key, seconds) in expected)
    {
        var show = Presets.Create(key);
        True(show.Name.StartsWith("♪ "), key + " naam");
        True(Math.Abs(show.TotalDurationMs / 1000.0 - seconds) < 0.1, $"{key}: {show.TotalDurationMs} ms ≠ {seconds} s");
        True(show.Steps.Skip(1).SkipLast(1).All(s => s.DurationMs >= 550), key + ": geen wissel sneller dan ±550 ms (de strip pakt anders niet elke kleur)");
        Eq(0, show.Steps.Count(s => s.IsSplit), key + ": geen helft/helft");
        var rate = show.Steps.Count / seconds;
        True(rate >= 0.6 && rate <= 1.8, $"{key}: {rate:0.0} wissels per seconde (doel ±1, nooit sneller dan de strip)");
        True(show.Steps.Skip(1).Count(s => s.ColorHex == "#000000") <= show.Steps.Count * 0.15, key + ": zwarte stukjes, maar niet te veel");
    }
    var s = DefaultSettings();
    Eq(49, s.Shows.Count(x => x.PresetKey.StartsWith("song-")), "staan in de lijst met lichtshows");
});

TestSync("Binnenkomst Thomas is gezellig: warme kleuren, zachte overgangen, geen wit, zwart of strobe", () =>
{
    True(Presets.CozySongs.Contains("song-thomas"), "Thomas staat bij de gezellige shows");
    // wie de rave-versie al had (versie 27), krijgt de gezellige
    var old = DefaultSettings(); var th = old.Shows.First(x => x.PresetKey == "song-thomas");
    th.Steps.Clear(); th.Steps.Add(LightStep.Color("#FFFFFF", 100)); old.Version = 27; SettingsStore.Migrate(old);
    Eq(0, old.Shows.First(x => x.PresetKey == "song-thomas").Steps.Count(st => st.ColorHex == "#FFFFFF"), "oude rave-versie vervangen");
    foreach (var fast in new[] { true, false })
    {
        var show = Presets.Create("song-thomas", fast);
        Eq(0, show.Steps.Count(st => st.ColorHex is "#FFFFFF" or "#000000"), (fast ? "snel" : "veilig") + ": geen wit of zwart");
        Eq(0, show.Steps.Count(st => st.Flashes > 1), "geen strobe");
        True(show.Steps.All(st => st.TransitionMs > 0), "overal een zachte overgang");
        True(Math.Abs(show.TotalDurationMs - 28395) <= 5, $"precies zo lang als de sound ({show.TotalDurationMs} ms)");
        True(show.Steps.Count / 28.4 <= 1.8, "rustig tempo, geen geflits");
    }
    True(Presets.Create("song-thomas", true).Steps.Count > Presets.Create("song-thomas", false).Steps.Count, "snelle versie gaat in het refrein op elke beat mee");
});

TestSync("Song-shows: opeenvolgende kleuren verschillen sterk (≥ 100°) en er zijn witte flitsen", () =>
{
    static double Hue(string hex)
    {
        ColorUtil.TryParseHex(hex, out var r, out var g, out var b);
        double R = r / 255.0, G = g / 255.0, B = b / 255.0, max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B)), d = max - min;
        if (d == 0) return 0;
        var h = max == R ? 60 * ((G - B) / d % 6) : max == G ? 60 * ((B - R) / d + 2) : 60 * ((R - G) / d + 4);
        return h < 0 ? h + 360 : h;
    }
    foreach (var song in SongShows.All.Where(x => !Presets.CozySongs.Contains(x.Key)))   // gezellige shows: zie aparte test
    {
        var show = Presets.Create(song.Key);
        var colored = show.Steps.Where(st => st.ColorHex is not ("#FFFFFF" or "#000000")).Select(st => st.ColorHex).ToList();
        for (var i = 1; i < colored.Count; i++)
        {
            if (colored[i] == colored[i - 1]) continue;
            var dist = Math.Abs(Hue(colored[i]) - Hue(colored[i - 1])) % 360;
            dist = Math.Min(dist, 360 - dist);
            True(dist >= 100, $"{song.Key}: {colored[i - 1]} → {colored[i]} lijkt te veel op elkaar ({dist:0}°)");
        }
        var flashes = show.Steps.Count(st => st.ColorHex == "#FFFFFF");
        True(flashes >= 1, $"{song.Key}: geen witte klap op de drop");
        True(colored.Distinct().Count() >= 5, $"{song.Key}: te weinig verschillende kleuren");
    }
});

TestSync("Opslag: versie 9 → 10: Rave-varianten weg, koppelingen omgezet, 4 wissels/s, geen helft/helft", () =>
{
    var s = new AppSettings { Version = 9 };
    s.Lighting.MinChangeMs = 300;
    s.Lighting.SegmentEffects = true;
    s.Shows.Add(Presets.Create("song-numb"));
    s.Shows.Add(new LightShow { Id = "preset-song-numb-rave", Name = "♪ Numb · Rave", PresetKey = "song-numb-rave" });
    s.Triggers.Add(new EventTrigger { Name = "eigen", EventType = EventTypes.Gift, GiftNames = "Rose", ShowId = "preset-song-numb-rave" });
    SettingsStore.Migrate(s);
    Eq(28, s.Version);
    Eq(0, s.Shows.Count(x => x.PresetKey.EndsWith("-rave")), "Rave-shows weg");
    Eq("preset-song-numb", s.Triggers.Single(t => t.Name == "eigen").ShowId, "koppeling omgezet");
    Eq(550, s.Lighting.MinChangeMs, "versie 12 zet de limiet op 550 ms");
    Eq(false, s.Lighting.SegmentEffects);
});

TestSync("Opslag: versie 11 → 12: limiet 550 ms, songs en presets vernieuwd, eigen tragere limiet blijft", () =>
{
    var s = new AppSettings { Version = 11 };
    s.Lighting.MinChangeMs = 250;
    var police = Presets.Create(Presets.Police); police.Steps[0].DurationMs = 320; s.Shows.Add(police);
    var song = Presets.Create("song-numb"); song.Steps.Add(LightStep.Color("#FF0000", 100, 100, 1, 0)); s.Shows.Add(song);
    SettingsStore.Migrate(s);
    Eq(28, s.Version);
    Eq(550, s.Lighting.MinChangeMs);
    Eq(600, s.Shows.Single(x => x.PresetKey == Presets.Police).Steps[0].DurationMs, "Police vernieuwd");
    Eq(Presets.Create("song-numb").Steps.Count, s.Shows.Single(x => x.PresetKey == "song-numb").Steps.Count, "song vernieuwd");
    var t = new AppSettings { Version = 11 }; t.Lighting.MinChangeMs = 800;
    SettingsStore.Migrate(t);
    Eq(800, t.Lighting.MinChangeMs, "eigen tragere instelling blijft");
});

TestSync("Opslag: versie 3 → 4 zet snelle flitsmodus aan en vernieuwt Police", () =>
{
    var s = new AppSettings { Version = 3 };
    s.Lighting.FlashMode = FlashMode.PowerToggle;
    var oldPolice = Presets.Create(Presets.Police);
    oldPolice.Steps = new ObservableCollection<LightStep> { LightStep.Color("#FF1A1A", 700, flashes: 3) };
    s.Shows.Add(oldPolice);
    SettingsStore.Migrate(s);
    Eq(FlashMode.Black, s.Lighting.FlashMode);
    Eq(28, s.Version);
    True(s.Shows.Single(x => x.PresetKey == Presets.Police).Steps.Count > 1, "Police vernieuwd");
});

Test("Song-show ‘Turn on the microwave’ volledig afgespeeld: timing loopt niet weg", async () =>
{
    using var lamp = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort);
    using var govee = NewGovee();
    var opts = new LightingOptions { ShowBrightness = 100, CommandDelayMs = 80 };
    var engine = new LightShowEngine(govee, () => opts);
    var show = Presets.Create("song-microwave");
    var starts = new System.Collections.Concurrent.ConcurrentDictionary<int, long>();
    var sw = Stopwatch.StartNew();
    engine.Frame += f => starts.TryAdd(f.StepIndex, sw.ElapsedMilliseconds);
    await engine.PlayAsync(show, new[] { "127.0.0.1" }, CancellationToken.None);
    var total = sw.ElapsedMilliseconds;
    True(Math.Abs(total - show.TotalDurationMs) < 200, $"duur {total} ms ≈ {show.TotalDurationMs} ms");
    // elke stap begint op zijn geplande moment (geen opstapelende vertraging)
    long planned = 0; var worst = 0L;
    for (var i = 0; i < show.Steps.Count; i++)
    {
        if (i > 0 && starts.TryGetValue(i, out var at)) worst = Math.Max(worst, Math.Abs(at - planned));
        planned += show.Steps[i].DurationMs;
    }
    True(worst < 120, $"grootste afwijking per stap {worst} ms");
    Console.WriteLine($"      (song-show: {show.Steps.Count} stappen, {lamp.Commands.Count} UDP-commando's, max. afwijking {worst} ms)");
});

TestSync("Govee: segmentpakket (ptReal) heeft juiste opbouw, masker en checksum", () =>
{
    var p = GoveeLanClient.SegmentPacket(0x00, 0x33, 0xFF, GoveeLanClient.SecondHalfMask(15));
    Eq(20, p.Length);
    Eq((byte)0x33, p[0]); Eq((byte)0x05, p[1]); Eq((byte)0x15, p[2]); Eq((byte)0x01, p[3]);
    Eq((byte)0x33, p[5]); Eq((byte)0xFF, p[6]);
    Eq(0b0111_1111_1000_0000, GoveeLanClient.SecondHalfMask(15), "segment 8 t/m 15");
    byte x = 0; for (var i = 0; i < 19; i++) x ^= p[i];
    Eq(x, p[19], "checksum");
});

Test("Engine: helft/helft – 1 lamp via segmenten, 2 lampen elk een eigen kleur", async () =>
{
    using var lamp = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort);
    using var lamp2 = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort, "127.0.0.2");
    using var govee = NewGovee();
    var opts = new LightingOptions { ShowBrightness = 100, CommandDelayMs = 30, SegmentEffects = true, SegmentCount = 15, MinChangeMs = 100 };
    var engine = new LightShowEngine(govee, () => opts);
    var show = new LightShow { Steps = new ObservableCollection<LightStep> { LightStep.Split("#FF0000", "#0000FF", 500) } };
    await engine.PlayAsync(show, new[] { "127.0.0.1" }, CancellationToken.None);
    await Task.Delay(100);
    Eq("#FF0000", lamp.ColorHistory().Last(), "hele strip rood");
    var pt = lamp.Commands.Single(c => c.cmd == "ptReal");
    var bytes = Convert.FromBase64String(pt.data.GetProperty("command")[0].GetString()!);
    Eq((byte)0xFF, bytes[6], "tweede helft blauw");
    lamp.Reset();
    await engine.PlayAsync(show, new[] { "127.0.0.1", "127.0.0.2" }, CancellationToken.None);
    await Task.Delay(100);
    Eq("#FF0000", lamp.ColorHistory().Last(), "lamp 1 rood");
    Eq("#0000FF", lamp2.ColorHistory().Last(), "lamp 2 blauw");
    Eq(0, lamp.Commands.Count(c => c.cmd == "ptReal"), "met 2 lampen geen segmenten nodig");
});

Test("Engine: snelheidslimiet dunt Rave uit tot wat de strip aankan, beats blijven", async () =>
{
    using var lamp = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort);
    using var govee = NewGovee();
    var opts = new LightingOptions { ShowBrightness = 100, CommandDelayMs = 30, MinChangeMs = 300 };
    var engine = new LightShowEngine(govee, () => opts);
    // 160 BPM: klap op de beat (375 ms) + halve-beat-flits ertussen
    var show = new LightShow();
    for (var i = 0; i < 8; i++) { show.Steps.Add(LightStep.Color(i % 2 == 0 ? "#FF0000" : "#00FF00", 188)); show.Steps.Add(LightStep.Color("#0000FF", 187)); }
    await engine.PlayAsync(show, new[] { "127.0.0.1" }, CancellationToken.None);
    await Task.Delay(100);
    var times = lamp.Commands.Where(c => c.cmd == "colorwc").Select(c => c.at).ToList();
    var gaps = times.Zip(times.Skip(1), (x, y) => (y - x).TotalMilliseconds).ToList();
    True(gaps.All(g => g >= 230), "nooit sneller dan de limiet (min " + gaps.Min().ToString("0") + " ms)");
    var colors = lamp.ColorHistory();
    Eq(0, colors.Count(c => c == "#0000FF"), "tussenflitsen overgeslagen");
    Eq(8, colors.Count(c => c is "#FF0000" or "#00FF00"), "alle 8 beats doorgekomen");
});

Test("Updates: nieuwere release vinden, downloaden, controlecode checken, uitpakken, vervangscript", async () =>
{
    var dir = TempDir();
    // Nep-release: zip met StreamLightControl.exe + sha256
    var zipSrc = Path.Combine(dir, "src"); Directory.CreateDirectory(Path.Combine(zipSrc, "app"));
    File.WriteAllText(Path.Combine(zipSrc, "app", "StreamLightControl.exe"), "nieuwe exe");
    var zipPath = Path.Combine(dir, "pkg.zip");
    System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(zipSrc, "app"), zipPath);
    var zipBytes = File.ReadAllBytes(zipPath);
    var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zipBytes)).ToLowerInvariant();
    var badSha = false;
    var http = new System.Net.HttpListener();
    http.Prefixes.Add("http://127.0.0.1:21299/");
    http.Start();
    _ = Task.Run(async () =>
    {
        while (http.IsListening)
        {
            System.Net.HttpListenerContext ctx;
            try { ctx = await http.GetContextAsync(); } catch { return; }
            var path = ctx.Request.Url!.AbsolutePath;
            byte[] body = path switch
            {
                "/repos/naam/slc/releases/latest" => System.Text.Encoding.UTF8.GetBytes("""
                    {"tag_name":"v9.4.1","draft":false,"prerelease":false,"body":"Nieuwe shows","published_at":"2026-11-11T12:00:00Z",
                     "assets":[{"name":"StreamLightControl-9.4.1-win-x64.zip","browser_download_url":"http://127.0.0.1:21299/dl/pkg.zip"},
                               {"name":"StreamLightControl-9.4.1-win-x64.zip.sha256","browser_download_url":"http://127.0.0.1:21299/dl/pkg.sha"}]}
                    """),
                "/dl/pkg.zip" => zipBytes,
                "/dl/pkg.sha" => System.Text.Encoding.ASCII.GetBytes(badSha ? new string('0', 64) : sha),
                _ => Array.Empty<byte>()
            };
            ctx.Response.StatusCode = body.Length == 0 ? 404 : 200;
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        }
    });
    try
    {
        var svc = new UpdateService(dir) { ApiBase = "http://127.0.0.1:21299" };
        var info = await svc.CheckAsync("naam/slc", new Version(3, 1, 0));
        True(info != null, "nieuwere versie gevonden");
        Eq(new Version(9, 4, 1), info!.Version);
        Eq(null, await svc.CheckAsync("naam/slc", new Version(9, 4, 1)), "zelfde versie → geen update");
        var files = await svc.DownloadAsync(info);
        Eq("nieuwe exe", File.ReadAllText(Path.Combine(files, "StreamLightControl.exe")));
        badSha = true;
        var rejected = false;
        try { await svc.DownloadAsync(info); } catch (InvalidDataException) { rejected = true; }
        True(rejected, "beschadigde download wordt geweigerd");
        var script = File.ReadAllText(UpdateService.WriteApplyScript(files, @"C:\Users\x\Desktop\Stream Light Control", 1234, "StreamLightControl.exe", Path.Combine(dir, "u")));
        True(script.Contains("Wait-Process -Id 1234") && script.Contains("Copy-Item") && script.Contains("--updated"), "vervangscript");
        Eq(new Version(3, 2, 0), UpdateService.ParseTag("v3.2.0"));
        Eq(false, UpdateService.CanSelfUpdate(Path.Combine(dir, "bin", "Debug")).ok, "niet vanuit Visual Studio");
        var gone = false;
        try { await svc.CheckAsync("naam/bestaatniet", new Version(1, 0, 0)); } catch (InvalidOperationException) { gone = true; }
        True(gone, "duidelijke fout als er geen release is");
    }
    finally { http.Stop(); http.Close(); }
});

// ---------------------------------------------------------------- Wachtrij

Test("Wachtrij: prioriteit, verwijderen, verplaatsen, leegmaken, limiet", async () =>
{
    using var govee = NewGovee();
    var engine = new LightShowEngine(govee, () => new LightingOptions { ShowBrightness = 100, CommandDelayMs = 30, RestoreMode = RestoreMode.None });
    var qopt = new QueueOptions { MaxLength = 4, InOrder = false };   // prioriteit-modus
    using var q = new AutomationQueue(engine, () => new[] { "127.0.0.1" }, () => qopt);
    var finished = new System.Collections.Concurrent.ConcurrentQueue<(string, FinishReason)>();
    q.Finished += (i, r, _) => finished.Enqueue((i.User, r));
    q.StopCurrent(); // gepauzeerd: niets wordt afgespeeld
    q.Start();
    var show = Presets.Create(Presets.Like);
    var a = q.Enqueue(show, 2, "a", "follow", "t1", QueueSource.Event, false);
    var b = q.Enqueue(show, 4, "b", "gift", "t2", QueueSource.Event, false);
    var c = q.Enqueue(show, 2, "c", "follow", "t1", QueueSource.Event, false);
    Eq("b,a,c", string.Join(",", q.PendingSnapshot().Select(x => x.User)), "hoogste prioriteit eerst");
    Eq(2, q.CountForTrigger("t1"));
    q.Move(c.Id, -1);
    Eq("b,c,a", string.Join(",", q.PendingSnapshot().Select(x => x.User)), "omhoog verplaatst");
    q.SetPriority(a.Id, 5);
    Eq("a,b,c", string.Join(",", q.PendingSnapshot().Select(x => x.User)), "prioriteit aangepast");
    q.Enqueue(show, 3, "d", "share", "t3", QueueSource.Event, false);
    q.Enqueue(show, 1, "e", "like", "t4", QueueSource.Event, false); // wachtrij vol (4) → laagste valt af
    Eq(4, q.PendingSnapshot().Count);
    True(finished.Any(f => f == ("e", FinishReason.Dropped)), "laagste prioriteit afgevallen");
    Eq(true, q.Remove(b.Id));
    Eq("a,d,c", string.Join(",", q.PendingSnapshot().Select(x => x.User)));
    q.Clear();
    Eq(0, q.PendingSnapshot().Count);
    await Task.CompletedTask;
});

Test("Wachtrij: overslaan, onderbreken en stoppen werken tijdens afspelen", async () =>
{
    using var lamp = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort);
    using var govee = NewGovee();
    var engine = new LightShowEngine(govee, () => new LightingOptions { ShowBrightness = 100, CommandDelayMs = 30, RestoreMode = RestoreMode.None });
    using var q = new AutomationQueue(engine, () => new[] { "127.0.0.1" }, () => new QueueOptions { GapMs = 0, InOrder = false });
    var finished = new System.Collections.Concurrent.ConcurrentQueue<(string user, FinishReason r)>();
    q.Finished += (i, r, _) => finished.Enqueue((i.User, r));
    q.Start();
    var longShow = new LightShow { Name = "Lang", Steps = new ObservableCollection<LightStep> { LightStep.Color("#00FF00", 5000) } };
    q.Enqueue(longShow, 2, "lang1", "follow", "t", QueueSource.Event, false);
    await WaitFor(() => q.Current != null, 2000, "start");
    q.SkipCurrent();
    await WaitFor(() => finished.Any(f => f.user == "lang1"), 2000, "overgeslagen");
    Eq(FinishReason.Skipped, finished.First(f => f.user == "lang1").r);

    q.Enqueue(longShow, 2, "lang2", "follow", "t", QueueSource.Event, false);
    await WaitFor(() => q.Current?.User == "lang2", 2000, "lang2 speelt");
    q.Enqueue(Presets.Create(Presets.Like), 5, "groot", "gift", "g", QueueSource.Event, interrupt: true);
    await WaitFor(() => finished.Any(f => f.user == "groot"), 3000, "groot afgespeeld");
    Eq(FinishReason.Interrupted, finished.First(f => f.user == "lang2").r);
    Eq(FinishReason.Completed, finished.First(f => f.user == "groot").r);

    q.Enqueue(longShow, 2, "lang3", "follow", "t", QueueSource.Event, false);
    await WaitFor(() => q.Current?.User == "lang3", 2000, "lang3 speelt");
    q.StopAll();
    await WaitFor(() => finished.Any(f => f.user == "lang3"), 2000, "gestopt");
    Eq(FinishReason.Stopped, finished.First(f => f.user == "lang3").r);
});

Test("Wachtrij achter elkaar (standaard): nooit door elkaar, volgorde van binnenkomst, ook bij hoge prioriteit", async () =>
{
    using var lamp = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort);
    using var govee = NewGovee();
    var engine = new LightShowEngine(govee, () => new LightingOptions { ShowBrightness = 100, CommandDelayMs = 30, RestoreMode = RestoreMode.None });
    var qopt = new QueueOptions { GapMs = 0 };
    Eq(true, qopt.InOrder, "standaard aan");
    using var q = new AutomationQueue(engine, () => new[] { "127.0.0.1" }, () => qopt);
    var order = new System.Collections.Concurrent.ConcurrentQueue<(string user, FinishReason r)>();
    q.Finished += (i, r, _) => order.Enqueue((i.User, r));
    q.Start();
    LightShow Show(int ms) => new() { Name = "S", Steps = new ObservableCollection<LightStep> { LightStep.Color("#00FF00", ms) } };
    q.Enqueue(Show(700), 2, "titanium", "gift", "a", QueueSource.Event, false);
    await WaitFor(() => q.Current?.User == "titanium", 2000, "titanium speelt");
    q.Enqueue(Show(300), 5, "mega", "gift", "b", QueueSource.Event, interrupt: true);      // kritiek + onderbreken
    q.Enqueue(Show(300), 3, "tip", "tip", "c", QueueSource.Event, false);
    Eq("mega,tip", string.Join(",", q.PendingSnapshot().Select(x => x.User)), "volgorde van binnenkomst");
    await WaitFor(() => order.Count == 3, 4000, "alle drie klaar");
    Eq("titanium,mega,tip", string.Join(",", order.Select(o => o.user)));
    True(order.All(o => o.r == FinishReason.Completed), "niets onderbroken");
});

Test("Wachtrij: show wacht op de sound (na de animatie) en haalt een paar seconden achterstand in", async () =>
{
    using var lamp = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort);
    using var govee = NewGovee();
    var engine = new LightShowEngine(govee, () => new LightingOptions { ShowBrightness = 100, CommandDelayMs = 30, RestoreMode = RestoreMode.None });
    using var q = new AutomationQueue(engine, () => new[] { "127.0.0.1" }, () => new QueueOptions { GapMs = 0 });
    var started = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>();
    var done = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>();
    q.Started += i => started[i.User] = DateTime.UtcNow;
    q.Finished += (i, r, _) => done[i.User] = DateTime.UtcNow;
    q.Start();
    var show = new LightShow { Name = "S", Steps = new ObservableCollection<LightStep> { LightStep.Color("#FF0000", 600), LightStep.Color("#00FF00", 1400) } };
    // shout-out: animatie van 0,8 s → show begint pas daarna
    var t0 = DateTime.UtcNow;
    q.Enqueue(show, 3, "shoutout", "tip", "s", QueueSource.Event, false, null, t0.AddMilliseconds(800), 2000);
    await WaitFor(() => done.ContainsKey("shoutout"), 5000, "shout-out klaar");
    var wait = (started["shoutout"] - t0).TotalMilliseconds;
    True(wait >= 750 && wait < 1100, $"begint na de animatie ({wait:0} ms)");
    // tip die 1,2 s geleden begon (polling): show begint 1,2 s ver, dus is ~0,8 s later klaar
    var t1 = DateTime.UtcNow;
    q.Enqueue(show, 3, "laat", "tip", "s", QueueSource.Event, false, null, t1.AddMilliseconds(-1200), 1500);
    await WaitFor(() => done.ContainsKey("laat"), 5000, "late tip klaar");
    var len = (done["laat"] - t1).TotalMilliseconds;
    True(len > 600 && len < 1300, $"achterstand ingehaald ({len:0} ms in plaats van 2000)");
    // meer dan 3 s te laat: gewoon vooraan beginnen
    var t2 = DateTime.UtcNow;
    q.Enqueue(show, 3, "heel laat", "tip", "s", QueueSource.Event, false, null, t2.AddSeconds(-10), 1500);
    await WaitFor(() => done.ContainsKey("heel laat"), 5000, "heel late tip klaar");
    True((done["heel laat"] - t2).TotalMilliseconds > 1800, "hele show afgespeeld");
});

// ---------------------------------------------------------------- BetterTok verbinding

Test("BetterTok Local: poortscan, events ontvangen en automatisch opnieuw verbinden", async () =>
{
    using var server = new FakeBetterTokServer(21292);
    var errors = new ErrorCenter();
    using var client = new BetterTokClient(errors) { LocalPortsOverride = new[] { 21290, 21291, 21292 } };
    var events = new List<StreamEvent>();
    client.EventReceived += e => { lock (events) events.Add(e); };
    var ok = await client.ConnectAsync(new BetterTokOptions { LocalUrl = "ws://127.0.0.1:21290", ScanPorts = true, AutoReconnect = true }, "");
    True(ok, "verbonden");
    Eq(ConnectionState.Connected, client.State);
    True(client.ConnectedUrl!.Contains("21292"), "juiste poort gevonden");
    await server.SendAsync("{\"event\":\"follow\",\"data\":{\"uniqueId\":\"x\"}}");
    await WaitFor(() => events.Count == 1, 2000, "event ontvangen");
    await server.DropConnectionAsync();
    await WaitFor(() => client.State == ConnectionState.Reconnecting, 3000, "reconnect gestart");
    await WaitFor(() => client.State == ConnectionState.Connected && server.Connections >= 2, 6000, "opnieuw verbonden");
    await server.SendAsync("{\"event\":\"share\",\"data\":{\"uniqueId\":\"y\"}}");
    await WaitFor(() => events.Count == 2, 2000, "event na reconnect");
});

Test("BetterTok Local: duidelijke fout als BetterTok niet draait", async () =>
{
    var errors = new ErrorCenter();
    using var client = new BetterTokClient(errors) { LocalPortsOverride = new[] { 21280, 21281 } };
    var ok = await client.ConnectAsync(new BetterTokOptions { LocalUrl = "ws://127.0.0.1:21280", AutoReconnect = false }, "");
    Eq(false, ok);
    Eq(ConnectionState.Failed, client.State);
    var e = errors.Snapshot().Single();
    Eq("bt.local.notfound", e.Key);
    True(e.Solution.Contains("Local Mode"), "oplossing noemt Local Mode");
    True(e.Detail.Contains("geweigerd") || e.Detail.Contains("refused") || e.Detail.Length > 0, "reden: " + e.Detail);
});

Test("BetterTok Cloud: Bearer-key, connected-frame, ping/pong en events", async () =>
{
    using var server = new FakeBetterTokServer(21295) { RequiredKey = "btk_live_goed", SendConnectedFrame = true };
    var errors = new ErrorCenter();
    using var client = new BetterTokClient(errors) { CloudUrlOverride = "ws://127.0.0.1:21295/" };
    var events = new List<StreamEvent>();
    client.EventReceived += e => { lock (events) events.Add(e); };
    True(await client.ConnectAsync(new BetterTokOptions { Mode = BetterTokMode.Cloud }, "btk_live_goed"), "verbonden");
    Eq("Bearer btk_live_goed", server.LastAuthorization);
    await WaitFor(() => client.CloudUsername == "mhstreams", 2000, "connected-frame verwerkt");
    var (pingOk, _) = await client.TestAsync();
    True(pingOk, "pong ontvangen");
    await server.SendAsync("{\"v\":1,\"id\":\"evt_1\",\"type\":\"gift\",\"data\":{\"viewer_username\":\"v\",\"gift_name\":\"Lion\",\"gift_count\":1,\"gift_value\":29999,\"gift_total_value\":29999}}");
    await WaitFor(() => events.Count == 1, 2000, "cloud-event");
    Eq(29999L, events[0].TotalCoins);
});

Test("BetterTok Cloud: UNAUTHORIZED → duidelijke fout en géén reconnect-lus", async () =>
{
    using var server = new FakeBetterTokServer(21296) { RequiredKey = "btk_live_goed" };
    var errors = new ErrorCenter();
    using var client = new BetterTokClient(errors) { CloudUrlOverride = "ws://127.0.0.1:21296/" };
    await client.ConnectAsync(new BetterTokOptions { Mode = BetterTokMode.Cloud, AutoReconnect = true }, "btk_live_fout");
    await WaitFor(() => client.State == ConnectionState.Failed, 3000, "status Failed");
    Eq("API-key geweigerd", client.StatusText);
    True(errors.Snapshot().Any(e => e.Key == "bt.unauthorized"), "fout in Error Center");
    await Task.Delay(2500);
    Eq(1, server.Connections, "niet opnieuw geprobeerd");

    var errors2 = new ErrorCenter();
    using var client2 = new BetterTokClient(errors2);
    Eq(false, await client2.ConnectAsync(new BetterTokOptions { Mode = BetterTokMode.Cloud }, "verkeerd"));
    Eq("bt.key.format", errors2.Snapshot().Single().Key);
});

// ---------------------------------------------------------------- DonateWave

Test("DonateWave: tips ophalen volgens de documentatie (Bearer-header, cursor pas na verwerken, dubbel/oud/onbekend genegeerd)", async () =>
{
    var http = new FakeDonateWave();
    var now = DateTime.UtcNow;
    http.Responses.Add((200, FakeDonateWave.Tip("1", "ev1", 500, now)));
    http.Responses.Add((200, FakeDonateWave.Tip("2", "ev1", 500, now)));                       // zelfde id → dubbel
    http.Responses.Add((200, FakeDonateWave.Tip("3", "ev3", 2500, now.AddMinutes(-30))));      // te oud
    http.Responses.Add((200, FakeDonateWave.Tip("4", "ev4", 1234, now, type: "payout.created"))); // onbekend type
    http.Responses.Add((200, FakeDonateWave.Tip("5", "ev5", 0, now, type: "integration.test", name: "Test")));
    http.Responses.Add((200, "{\"events\":[],\"next_cursor\":\"5\",\"poll_after_seconds\":2}"));
    var errors = new ErrorCenter();
    using var dw = new DonateWaveClient(errors, http) { BaseUrl = "https://dw.test", DelayScale = 0.01 };
    var got = new List<StreamEvent>(); var cursors = new List<string>();
    dw.EventReceived += e => { lock (got) got.Add(e); };
    dw.CursorChanged += c => { lock (cursors) { cursors.Add(c); True(got.Count >= (c == "1" ? 1 : 0), "cursor pas ná verwerken"); } };
    dw.Start("dw_evt_geheim123", "");
    await WaitFor(() => cursors.Contains("5") && http.Requests.Count >= 7, 5000, "alle antwoorden verwerkt");
    await dw.StopAsync();
    Eq(2, got.Count, "één echte tip + één test");
    Eq(EventTypes.Tip, got[0].Type); Eq(500L, got[0].TotalCoins); Eq("Supporter", got[0].DisplayName); Eq("€ 5,00", got[0].GiftName); Eq(false, got[0].IsTest);
    Eq(true, got[1].IsTest, "integration.test is een test"); Eq(500L, got[1].TotalCoins, "test zonder bedrag = € 5");
    Eq("https://dw.test/api/v1/events?after=0", http.Requests[0].Url, "eerste keer vanaf 0");
    Eq("https://dw.test/api/v1/events?after=1", http.Requests[1].Url, "daarna vanaf de cursor");
    True(http.Requests.All(r => r.Auth == "Bearer dw_evt_geheim123"), "sleutel in de Authorization-header");
    True(http.Requests.All(r => !r.Url.Contains("dw_evt")), "sleutel nooit in de URL");
    True(http.Requests.All(r => !r.HasOrigin), "geen Origin-header");
    Eq(ConnectionState.Disconnected, dw.State);
});

TestSync("DonateWave: echt antwoord van de API (zoals opgehaald op 12-11-2026) wordt goed gelezen", () =>
{
    const string body = "{\"events\":[{\"id\":\"99b4ec4e-e46f-4c07-af3b-0996585032cf\",\"data\":{\"test\":true,\"currency\":\"eur\",\"amount_cents\":500,\"display_name\":\"DonateWave test\"},\"type\":\"integration.test\",\"cursor\":\"2\",\"created_at\":\"2026-11-12T14:22:31.56423+00:00\",\"expires_at\":\"2026-11-19T14:22:31.56423+00:00\"},{\"id\":\"5d391cea-5904-4ed6-9d43-b0a9a022f06e\",\"data\":{\"test\":true,\"currency\":\"eur\",\"amount_cents\":500,\"display_name\":\"DonateWave test\"},\"type\":\"integration.test\",\"cursor\":\"3\",\"created_at\":\"2026-11-12T14:24:59.154589+00:00\",\"expires_at\":\"2026-11-19T14:24:59.154589+00:00\"}],\"next_cursor\":\"3\",\"poll_after_seconds\":2,\"retention_days\":7}";
    var dw = new DonateWaveClient(new ErrorCenter()) { UtcNow = () => new DateTime(2026, 11, 12, 14, 26, 0, DateTimeKind.Utc) };
    var got = new List<StreamEvent>(); var cur = "";
    dw.EventReceived += e => got.Add(e); dw.CursorChanged += c => cur = c;
    var wait = dw.Process(body);
    Eq(2.0, wait, "elke 2 s");
    Eq("3", cur, "cursor");
    Eq(1, got.Count, "eerste is ouder dan 3 minuten → overgeslagen, tweede afgespeeld");
    Eq(true, got[0].IsTest); Eq(500L, got[0].TotalCoins); Eq("Test · DonateWave test", got[0].DisplayName);
    Eq(new DateTime(2026, 11, 12, 14, 24, 59, 154, DateTimeKind.Utc).Ticks / 10000, got[0].OccurredAtUtc!.Value.Ticks / 10000, "tijd van de tip");
    True(dw.LastBatchText.Contains("2 gebeurtenis") && dw.LastBatchText.Contains("ouder dan 3 min"), "zichtbaar waarom er een is overgeslagen: " + dw.LastBatchText);
});

Test("DonateWave: 401 stopt met vragen, 429 wacht, netwerkfout probeert opnieuw met dezelfde cursor", async () =>
{
    var http = new FakeDonateWave();
    http.Responses.Add((500, "kapot"));
    http.Responses.Add((429, "{}"));
    http.Responses.Add((401, "{}"));
    var errors = new ErrorCenter();
    using var dw = new DonateWaveClient(errors, http) { BaseUrl = "https://dw.test", DelayScale = 0.005 };
    dw.Start("dw_evt_x", "42");
    await WaitFor(() => dw.State == ConnectionState.Failed, 5000, "gestopt na 401");
    await Task.Delay(100);
    Eq(3, http.Requests.Count, "na 401 geen verzoeken meer");
    True(http.Requests.All(r => r.Url.EndsWith("after=42")), "steeds dezelfde cursor");
    var keys = errors.Snapshot().Select(e => e.Key).ToList();
    True(keys.Contains("dw.unauthorized") && keys.Contains("dw.ratelimit") && keys.Contains("dw.network"), "fouten gemeld: " + string.Join(",", keys));
    using var dw2 = new DonateWaveClient(errors, http);
    dw2.Start("", "");
    Eq(ConnectionState.Failed, dw2.State, "zonder sleutel niet starten");
    True(errors.Snapshot().Any(e => e.Key == "dw.key.missing"), "melding: geen sleutel");
});

TestSync("Opslag: versie 23 → 24 zet naam/bedrag-tijd op 2 s, eigen waarde blijft", () =>
{
    var s = DefaultSettings(); s.DonateWave.AlertGapMs = 300; s.Version = 23; SettingsStore.Migrate(s);
    Eq(28, s.Version);
    Eq(3240, s.DonateWave.AlertGapMs, "via 2000 naar de gemeten 3240");
    var s2 = DefaultSettings(); s2.DonateWave.AlertGapMs = 1500; s2.Version = 23; SettingsStore.Migrate(s2);
    Eq(1500, s2.DonateWave.AlertGapMs, "zelf ingesteld blijft");
});

TestSync("Opslag: versie 25 → 26 zet DonateWave-tijden op de metingen, eigen waarden blijven", () =>
{
    var s = DefaultSettings(); s.DonateWave.OverlayDelayMs = 250; s.DonateWave.AlertGapMs = 2000; s.DonateWave.BetweenAnimationsMs = 1200;
    s.Version = 25; SettingsStore.Migrate(s);
    Eq(28, s.Version);
    Eq(0, s.DonateWave.OverlayDelayMs); Eq(3240, s.DonateWave.AlertGapMs); Eq(1735, s.DonateWave.BetweenAnimationsMs);
    var s2 = DefaultSettings(); s2.DonateWave.OverlayDelayMs = 400; s2.DonateWave.AlertGapMs = 2500; s2.DonateWave.BetweenAnimationsMs = 900;
    s2.Version = 25; SettingsStore.Migrate(s2);
    Eq(400, s2.DonateWave.OverlayDelayMs, "eigen correctie blijft"); Eq(2500, s2.DonateWave.AlertGapMs); Eq(900, s2.DonateWave.BetweenAnimationsMs);
});

TestSync("Opslag: versie 22 → 23 werkt gemeten animatietijden bij, zelf afgestelde tijden blijven", () =>
{
    var s = DefaultSettings();
    s.Triggers.Single(t => t.Id == "tw-special-desertking").StartDelayMs = 10560;   // oude meting
    s.Triggers.Single(t => t.Id == "tw-special-spikygroove").StartDelayMs = 8200;   // zelf afgesteld
    s.Version = 22; SettingsStore.Migrate(s);
    Eq(28, s.Version);
    Eq(10560, s.Triggers.Single(t => t.Id == "tw-special-desertking").StartDelayMs, "videoduur (na alle updates)");
    Eq(8200, s.Triggers.Single(t => t.Id == "tw-special-spikygroove").StartDelayMs, "eigen afstelling blijft");
});

TestSync("Opslag: versie 18 → 19 zet DonateWave-regels op de sounds die DonateWave afspeelt, eigen keuze blijft", () =>
{
    var s = DefaultSettings();
    s.Triggers.Single(t => t.Id == "tw-1").ShowId = "preset-" + Presets.BigGift;           // oude standaard
    s.Triggers.Single(t => t.Id == "tw-10").ShowId = "preset-party";                      // zelf gekozen
    s.Triggers.Single(t => t.Id == "tw-30").ShowId = "preset-song-nevercomingdown";       // oude standaard
    s.Version = 18; SettingsStore.Migrate(s);
    Eq("preset-song-savetik", s.Triggers.Single(t => t.Id == "tw-1").ShowId);
    Eq("preset-party", s.Triggers.Single(t => t.Id == "tw-10").ShowId, "eigen keuze blijft");
    Eq("preset-song-christmaslunos", s.Triggers.Single(t => t.Id == "tw-30").ShowId);
    foreach (var k in new[] { "savetik", "boa", "christmaslunos", "oceans", "twilightzone", "deadlyguns" })
        True(s.FindShow("preset-song-" + k) != null, k + " aanwezig");
});

TestSync("Opslag: versie 17 → 18 voegt de DonateWave-regels toe zonder eigen regels te wijzigen", () =>
{
    var s = DefaultSettings();
    foreach (var t in s.Triggers.Where(t => t.Id.StartsWith("tw-")).ToList()) s.Triggers.Remove(t);
    var mine = new EventTrigger { Name = "Mijn tip-regel", EventType = EventTypes.Tip, MinCoins = 2000, ShowId = "preset-party" };
    s.Triggers.Add(mine); var count = s.Triggers.Count;
    s.Version = 17; SettingsStore.Migrate(s);
    Eq(28, s.Version); Eq(count + 14, s.Triggers.Count, "14 regels erbij (3 bedragen + shout-out + 10 specials)");
    Eq(2000, mine.MinCoins, "eigen regel ongewijzigd");
    SettingsStore.Migrate(s); Eq(count + 14, s.Triggers.Count, "niet dubbel");
});

Test("DonateWave: shout-out krijgt een eigen lichtshow, Lucky Wheel volgt het bedrag", async () =>
{
    var s = DefaultSettings();
    StreamEvent Ev(long cents, string kind) => new() { Type = EventTypes.Tip, TotalCoins = cents, TipKind = kind };
    Eq("tw-special-spikygroove", RuleEngine.SelectTrigger(Ev(500, TipKinds.Shoutout), s.Triggers)?.Id, "shout-out € 5 = Spiky Groove");
    Eq("tw-special-desertking", RuleEngine.SelectTrigger(Ev(50000, TipKinds.Shoutout), s.Triggers)?.Id, "shout-out € 500 = Desert King");
    Eq("tw-special-starlightkingdom", RuleEngine.SelectTrigger(Ev(1000, TipKinds.Shoutout), s.Triggers)?.Id, "€ 10: Starlight Kingdom gaat voor");
    Eq("tw-special-acornfortune", RuleEngine.SelectTrigger(new StreamEvent { Type = EventTypes.Tip, TotalCoins = 1000, TipKind = TipKinds.Shoutout, Comment = "Acorn Fortune" }, s.Triggers)?.Id, "op naam als DonateWave die meestuurt");
    Eq("tw-shoutout", RuleEngine.SelectTrigger(Ev(1234, TipKinds.Shoutout), s.Triggers)?.Id, "onbekende shout-out = algemene regel");
    var spiky = s.Triggers.Single(t => t.Id == "tw-special-spikygroove");
    Eq(7500, spiky.StartDelayMs, "show pas na de animatievideo (7,500 s)");
    Eq("7,5", spiky.StartDelaySeconds);
    var changed = new List<string>(); spiky.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
    spiky.StartDelayMs += 100;                                   // knop + bij Shout-outs afstellen
    Eq("7,6", spiky.StartDelaySeconds); True(changed.Contains(nameof(EventTrigger.StartDelaySeconds)), "invulveld ververst");
    spiky.StartDelaySeconds = "8,25"; Eq(8250, spiky.StartDelayMs, "seconden invullen met komma");
    spiky.StartDelayMs = 7500;
    Eq("preset-song-helpless", spiky.ShowId, "extra sound Helpless");
    Eq("tw-10", RuleEngine.SelectTrigger(Ev(1500, TipKinds.Wheel), s.Triggers)?.Id, "Lucky Wheel zonder eigen regel = bedrag");
    Eq("tw-1", RuleEngine.SelectTrigger(Ev(500, ""), s.Triggers)?.Id, "gewone tip");
    foreach (var t in s.Triggers.Where(t => t.TipKind == TipKinds.Shoutout)) t.Enabled = false;
    Eq("tw-1", RuleEngine.SelectTrigger(Ev(500, TipKinds.Shoutout), s.Triggers)?.Id, "shout-out-regels uit → gewone bedrag-regel");
    True(s.FindShow(s.Triggers.Single(t => t.Id == "tw-shoutout").ShowId) != null, "shout-out heeft een show");
    Eq("Shout-out", s.Triggers.Single(t => t.Id == "tw-shoutout").ConditionSummary);
    foreach (var (id, name, cents, animMs, song, prio) in Presets.Specials)
        True(s.FindShow("preset-song-" + song) != null, name + ": show bestaat");
    // de client leest special / lucky_wheel uit de API
    var http = new FakeDonateWave();
    http.Responses.Add((200, FakeDonateWave.Tip("1", "so1", 300, DateTime.UtcNow, special: true)));
    http.Responses.Add((200, FakeDonateWave.Tip("2", "lw1", 300, DateTime.UtcNow, wheel: true)));
    using var dw = new DonateWaveClient(new ErrorCenter(), http) { BaseUrl = "https://dw.test", DelayScale = 0.01 };
    var got = new List<StreamEvent>(); dw.EventReceived += e => { lock (got) got.Add(e); };
    dw.Start("dw_evt_x", "");
    await WaitFor(() => got.Count == 2, 3000, "twee tips");
    await dw.StopAsync();
    Eq(TipKinds.Shoutout, got[0].TipKind); Eq("€ 3,00 · shout-out", got[0].GiftName);
    Eq(TipKinds.Wheel, got[1].TipKind); Eq("€ 3,00 · Lucky Wheel", got[1].GiftName);
});

Test("Verbindingsdetails: API-adres, HTTP-status en foutcode zichtbaar, sleutel nooit volledig", async () =>
{
    var dir = TempDir();
    var http = new FakeDonateWave();
    http.Responses.Add((503, "{\"error\":\"onderhoud\"}"));
    http.Responses.Add((200, "{\"events\":[],\"next_cursor\":\"\",\"poll_after_seconds\":2}"));
    using var app = new AutomationController(dir, NewGovee(), donateWaveHttp: http);
    app.Initialize();
    app.DonateWave.BaseUrl = "https://dw.test"; app.DonateWave.DelayScale = 0.01;
    app.SetDonateWaveKey("dw_evt_supergeheim1234567890");
    app.Settings.DonateWave.Enabled = true;
    app.ApplyDonateWave();
    await WaitFor(() => app.DonateWave.RequestCount >= 2, 4000, "twee verzoeken");
    var rep = app.ConnectionReport();
    await app.DonateWave.StopAsync();
    True(rep.Contains("GET https://dw.test/api/v1/events?after=0"), "API-adres");
    True(rep.Contains("HTTP 503") && rep.Contains("onderhoud"), "foutcode en antwoord van de server");
    True(rep.Contains("HTTP 200"), "laatste geslaagde verzoek");
    True(!rep.Contains("supergeheim1234567890"), "sleutel niet volledig");
    True(rep.Contains("BETTERTOK") && rep.Contains("GOVEE"), "ook BetterTok en lampen");
});

TestSync("Zones: Direct-pakket met eigen kleur per zone en segmentpakket (ptReal) byte voor byte", () =>
{
    var p = GoveeLanClient.DirectZonesPacket(new (byte, byte, byte)[] { (255, 0, 0), (0, 255, 0), (0, 0, 255) });
    Eq("BB 00 0B B0 01 03 FF 00 00 00 FF 00 00 00 FF", string.Join(" ", p.Take(15).Select(b => b.ToString("X2"))), "kop, aantal en kleuren per zone");
    Eq(p.Take(p.Length - 1).Aggregate((byte)0, (x, b) => (byte)(x ^ b)), p[^1], "controlebyte");
    // zelfde kleur overal = oude pakket voor de hele strip
    True(GoveeLanClient.DirectZonesPacket(Enumerable.Repeat(((byte)1, (byte)2, (byte)3), 20).ToList()).SequenceEqual(GoveeLanClient.DirectColorPacket(1, 2, 3, 20)), "gelijk aan hele-strip-pakket");
    var f = GoveeLanClient.SegmentColorFrame(0x12, 0x34, 0x56, (1 << 0) | (1 << 9) | (1 << 14));
    Eq(20, f.Length);
    Eq("33 05 15 01 12 34 56 00 00 00 00 00 01 42 00 00 00 00 00", string.Join(" ", f.Take(19).Select(b => b.ToString("X2"))), "segment 1, 10 en 15");
    Eq(f.Take(19).Aggregate((byte)0, (x, b) => (byte)(x ^ b)), f[19], "XOR over byte 0–18");
    Eq("1,2,3,18,19,20", string.Join(",", GoveeDeviceConfig.ParseZones("1-3, 18–20", 20)), "zones uit tekst");
    Eq("5,6", string.Join(",", GoveeDeviceConfig.ParseZones("0, 5 6, 99, 20-18x", 15)), "alleen geldige nummers");
});

Test("Zones: segmentkleuren gaan als ptReal naar de strip (zelfde kleur samen, max 12 per bericht)", async () =>
{
    using var lamp = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort);
    var govee = NewGovee();
    var zones = Enumerable.Range(0, 15).Select(i => i is 13 or 14 ? ((byte)255, (byte)200, (byte)0) : ((byte)0, (byte)0, (byte)40)).ToList();
    await govee.SegmentColorsAsync("127.0.0.1", zones);
    await WaitFor(() => lamp.Commands.Any(m => m.cmd == "ptReal"), 2000, "ptReal ontvangen");
    var cmds = lamp.Commands.Last(m => m.cmd == "ptReal").data.GetProperty("command");
    Eq(2, cmds.GetArrayLength(), "twee kleuren = twee pakketjes");
    var masks = cmds.EnumerateArray().Select(c => Convert.FromBase64String(c.GetString()!)).Select(b => b[12] | (b[13] << 8)).OrderBy(x => x).ToArray();
    Eq((1 << 13) - 1, masks[0], "segment 1–13 blauw");
    Eq((1 << 13) | (1 << 14), masks[1], "segment 14 en 15 geel");
});

TestSync("Binnenkomst: Thomas en Diego krijgen hun eigen show, andere kijkers niet", () =>
{
    var s = DefaultSettings();
    var thomas = s.Triggers.Single(t => t.Id == "te-thomas");
    var diego = s.Triggers.Single(t => t.Id == "te-diego");
    Eq("preset-song-thomas", thomas.ShowId); Eq("preset-song-diego", diego.ShowId);
    True(s.Shows.Any(x => x.Id == "preset-song-thomas") || Presets.AllKeys.Contains("song-thomas"), "show Thomas bestaat");
    Eq(28440, thomas.SoundDurationMs, "volgende show wacht tot de sound van Thomas klaar is");
    Eq(31974, diego.SoundDurationMs);
    StreamEvent Join(string user, string display = "") => new() { Type = EventTypes.Member, RawType = "member", User = user, DisplayName = display };
    Eq("te-thomas", RuleEngine.SelectTrigger(Join("thomas"), s.Triggers)?.Id, "Thomas");
    Eq("te-thomas", RuleEngine.SelectTrigger(Join("@Thomas"), s.Triggers)?.Id, "hoofdletters en @ maken niet uit");
    Eq("te-diego", RuleEngine.SelectTrigger(Join("diego_123", "Diego"), s.Triggers)?.Id, "op weergavenaam");
    True(RuleEngine.SelectTrigger(Join("thomasvdb"), s.Triggers) == null, "andere kijker met bijna dezelfde naam: geen show");
    True(RuleEngine.SelectTrigger(Join("piet"), s.Triggers) == null, "andere kijker: geen show");
    // een algemene binnenkomst-regel blijft voor iedereen werken, maar Thomas krijgt zijn eigen
    s.Triggers.Add(new EventTrigger { Id = "alle", Name = "Iedereen", EventType = EventTypes.Member, ShowId = "preset-" + Presets.Party, Priority = Priorities.Critical });
    Eq("alle", RuleEngine.SelectTrigger(Join("piet"), s.Triggers)?.Id);
    Eq("te-thomas", RuleEngine.SelectTrigger(Join("thomas"), s.Triggers)?.Id, "eigen regel gaat voor, ook bij hogere prioriteit van de algemene");
    // meerdere namen
    thomas.MatchText = "thomas, tommie";
    Eq("te-thomas", RuleEngine.SelectTrigger(Join("tommie"), s.Triggers)?.Id, "tweede naam");
    // songs: 49, de nieuwe twee precies zo lang als de sound
    var th = SongShows.All.Single(x => x.Key == "song-thomas"); var di = SongShows.All.Single(x => x.Key == "song-diego");
    var thMs = Presets.Create("song-thomas").TotalDurationMs; var diMs = Presets.Create("song-diego").TotalDurationMs;
    True(Math.Abs(thMs - 28395) <= 50, $"show Thomas {thMs} ms ≈ sound 28,4 s");
    True(Math.Abs(diMs - 31974) <= 50, $"show Diego {diMs} ms ≈ sound 32,0 s");
    Eq(49, SongShows.All.Count());
});

TestSync("Opslag: versie 26 → 28 voegt de binnenkomst-regels toe (één keer)", () =>
{
    var s = DefaultSettings();
    foreach (var t in s.Triggers.Where(t => t.Id.StartsWith("te-")).ToList()) s.Triggers.Remove(t);
    s.Version = 26; SettingsStore.Migrate(s);
    Eq(28, s.Version);
    Eq(2, s.Triggers.Count(t => t.Id.StartsWith("te-")));
    SettingsStore.Migrate(s);
    Eq(2, s.Triggers.Count(t => t.Id.StartsWith("te-")), "niet dubbel");
});

TestSync("Kerst-overlays: tips tellen voor doel, ranking, recent, vandaag en weekkampioen", () =>
{
    var dir = TempDir();
    var stats = new DonationStats(dir);
    var o = new OverlayOptions { GoalCents = 10000, GoalStartUtc = DateTime.UtcNow.AddHours(-1) };
    var now = DateTime.UtcNow;
    StreamEvent Tip(string id, string name, long cents, bool test = false, string kind = "", int q = 1, string msg = "", double minAgo = 0) => new()
    {
        Type = EventTypes.Tip, DedupeKey = "dw:" + id, User = name, TotalCoins = cents, IsTest = test, TipKind = kind, GiftCount = q,
        TipMessage = msg, OccurredAtUtc = now.AddMinutes(-minAgo)
    };
    stats.Add(Tip("1", "Anna", 500, msg: "Fijne kerst!", minAgo: 3), now.AddMinutes(-3));
    stats.Add(Tip("2", "Bram", 2000, kind: TipKinds.Shoutout, minAgo: 2), now.AddMinutes(-2));
    stats.Add(Tip("3", "anna", 1000, minAgo: 1), now.AddMinutes(-1));
    stats.Add(Tip("4", "TestViewer", 500, test: true), now);
    True(stats.Add(Tip("1", "Anna", 500), now) == null, "dubbele tip telt niet");
    var j = System.Text.Json.JsonSerializer.SerializeToElement(stats.Snapshot(o, now));
    Eq("€ 35,00", j.GetProperty("goal").GetProperty("current").GetString(), "doel zonder testtips");
    Eq(35.0, j.GetProperty("goal").GetProperty("percent").GetDouble());
    Eq("€ 65,00", j.GetProperty("goal").GetProperty("remaining").GetString());
    var rk = j.GetProperty("ranking");
    Eq("Bram", rk[0].GetProperty("name").GetString()); Eq("€ 20,00", rk[0].GetProperty("amount").GetString());
    Eq("anna", rk[1].GetProperty("name").GetString(), "Anna + anna samen"); Eq("€ 15,00", rk[1].GetProperty("amount").GetString());
    Eq(4, j.GetProperty("recent").GetArrayLength(), "testtip wel zichtbaar in recent");
    Eq("TestViewer", j.GetProperty("recent")[0].GetProperty("name").GetString(), "nieuwste eerst");
    True(j.GetProperty("recent")[0].GetProperty("test").GetBoolean(), "gemarkeerd als test");
    Eq("TestViewer", j.GetProperty("last").GetProperty("name").GetString(), "laatste supporter ook bij test");
    Eq("Fijne kerst!", j.GetProperty("recent")[3].GetProperty("message").GetString());
    Eq(3, j.GetProperty("today").GetProperty("count").GetInt32(), "vandaag zonder testtip");
    Eq("Bram", j.GetProperty("weekChampion").GetProperty("name").GetString());
    Eq(1, j.GetProperty("alerts").GetArrayLength(), "alleen de nieuwe alert (testtip ook), oude zijn al voorbij");
    Eq("TestViewer", j.GetProperty("alerts")[0].GetProperty("name").GetString());
    // 3 tips tegelijk: alerts na elkaar, nooit over elkaar
    stats.Add(Tip("5", "Cor", 100), now); stats.Add(Tip("6", "Dirk", 200), now); stats.Add(Tip("7", "Eva", 300), now);
    var al = System.Text.Json.JsonSerializer.SerializeToElement(stats.Snapshot(o, now)).GetProperty("alerts");
    Eq(4, al.GetArrayLength());
    for (var i = 1; i < al.GetArrayLength(); i++)
        True(al[i].GetProperty("showAt").GetInt64() >= al[i - 1].GetProperty("showAt").GetInt64() + o.AlertMs, "alert na de vorige");
    // Testtips meetellen
    o.CountTests = true;
    j = System.Text.Json.JsonSerializer.SerializeToElement(stats.Snapshot(o, now));
    Eq("€ 46,00", j.GetProperty("goal").GetProperty("current").GetString(), "35 + 5 test + 6");
    // Doel terugzetten
    o.GoalStartUtc = now.AddSeconds(1);
    j = System.Text.Json.JsonSerializer.SerializeToElement(stats.Snapshot(o, now));
    Eq("€ 0,00", j.GetProperty("goal").GetProperty("current").GetString());
    // Bewaard na herstart
    var again = new DonationStats(dir);
    Eq(7, again.Count, "tips bewaard in donations.json");
    again.Clear(); Eq(0, new DonationStats(dir).Count, "wissen");
});

Test("Kerst-overlays: server op localhost geeft pagina's en de stand", async () =>
{
    var dir = TempDir();
    var stats = new DonationStats(dir);
    var o = new OverlayOptions { Port = 7590 };
    using var server = new OverlayServer(stats, () => o);
    server.Apply();
    True(server.IsRunning, server.Status);
    stats.Add(new StreamEvent { Type = EventTypes.Tip, DedupeKey = "dw:x", User = "<b>Hacker</b>", TotalCoins = 750, OccurredAtUtc = DateTime.UtcNow }, DateTime.UtcNow);
    using var http = new HttpClient();
    var state = await http.GetStringAsync("http://127.0.0.1:7590/api/state");
    True(state.Contains("\\u003Cb\\u003EHacker") || state.Contains("\\u003cb\\u003eHacker"), "naam als JSON-tekst (geen HTML): " + state[..Math.Min(200, state.Length)]);
    foreach (var v in OverlayServer.Views)
    {
        var html = await http.GetStringAsync($"http://127.0.0.1:7590/kerst/{v.Path}");
        True(html.Contains($"data-view=\"{v.Path}\""), v.Path);
        True(!html.Contains("__"), "geen lege plaatshouders in " + v.Path);
        var html2 = await http.GetStringAsync(OverlayServer.ViewUrl(7590, v.Path));
        True(html2.Contains($"data-view=\"{v.Path}\""), v.Path + ".html (voor TikTok LIVE Studio)");
        True(OverlayServer.ViewUrl(7590, v.Path).EndsWith(".html"), "eindigt op .html");
    }
    True((await http.GetStringAsync("http://127.0.0.1:7590/")).Contains("/kerst/alert"), "overzicht");
    var nf = await http.GetAsync("http://127.0.0.1:7590/geheim");
    Eq(System.Net.HttpStatusCode.NotFound, nf.StatusCode);
    // Poort bezet → duidelijke melding, geen crash
    using var second = new OverlayServer(stats, () => o);
    second.Apply();
    True(!second.IsRunning && second.Status.Contains("7590"), second.Status);
    o.Enabled = false; server.Apply();
    True(!server.IsRunning, "uit");
});

Test("Kerst-overlays: https met eigen certificaat voor kerst.slc-overlay.nl (TikTok LIVE Studio)", async () =>
{
    var dir = TempDir();
    var cert = OverlayCert.LoadOrCreate(dir);
    True(cert.Leaf.HasPrivateKey, "sleutel aanwezig");
    Eq(cert.Ca.Subject, cert.Leaf.Issuer, "ondertekend door eigen basiscertificaat");
    True(cert.Leaf.NotAfter > DateTime.Now.AddDays(700), "ruim 2 jaar geldig");
    var again = OverlayCert.LoadOrCreate(dir);
    Eq(cert.Leaf.Thumbprint, again.Leaf.Thumbprint, "zelfde certificaat na herstart (niet steeds opnieuw vertrouwen)");
    var stats = new DonationStats(dir);
    var o = new OverlayOptions { Port = 7594 };
    var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
    using var server = new OverlayServer(stats, () => o, logs.Enqueue, () => cert.Leaf);
    server.Apply();
    True(server.TlsRunning, "https draait");
    Eq(7595, server.TlsPort);
    Eq("https://kerst.slc-overlay.nl:7595/kerst/alles.html", OverlayServer.TikTokUrl(7594, "alles"));
    string? chainError = null;
    var handler = new SocketsHttpHandler
    {
        UseProxy = false,   // rechtstreeks naar de app, niet via een proxy
        // de domeinnaam naar 127.0.0.1 laten wijzen, zoals het hosts-bestand doet
        ConnectCallback = async (ctx, ct) =>
        {
            var sock = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            await sock.ConnectAsync(System.Net.IPAddress.Loopback, ctx.DnsEndPoint.Port, ct);
            return new System.Net.Sockets.NetworkStream(sock, true);
        },
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            // zoals een browser die het basiscertificaat vertrouwt: keten + domeinnaam moeten kloppen
            RemoteCertificateValidationCallback = (_, c, _, errors) =>
            {
                if ((errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) != 0) { chainError = "naam klopt niet"; return false; }
                using var chain = new System.Security.Cryptography.X509Certificates.X509Chain();
                chain.ChainPolicy.TrustMode = System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(cert.Ca);
                chain.ChainPolicy.RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
                var ok = chain.Build(new System.Security.Cryptography.X509Certificates.X509Certificate2(c!));
                if (!ok) chainError = string.Join(", ", chain.ChainStatus.Select(x => x.StatusInformation));
                return ok;
            }
        }
    };
    using var http = new HttpClient(handler);
    string html;
    try { html = await http.GetStringAsync("https://kerst.slc-overlay.nl:7595/kerst/alles.html"); }
    catch (Exception ex) { await Task.Delay(300); throw new Exception($"https mislukt: {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message} | {ex.InnerException?.InnerException?.Message} | keten: {chainError} | server: {string.Join(" / ", logs)}"); }
    True(chainError == null, "certificaat geldig: " + chainError);
    True(html.Contains("data-view=\"alles\""), "pagina via https");
    var state = await http.GetStringAsync("https://kerst.slc-overlay.nl:7595/api/state");
    True(state.Contains("\"goal\""), "stand via https");
    // gewoon http blijft werken (OBS)
    using var plain = new HttpClient();
    True((await plain.GetStringAsync(OverlayServer.ViewUrl(7594, "doel"))).Contains("data-view=\"doel\""), "http voor OBS");
    server.Stop();
    True(!server.TlsRunning && !server.IsRunning, "alles uit");
});

Test("Kerst-overlays: DonateWave-tip komt in de overlays, ook zonder lichtregel", async () =>
{
    var dir = TempDir();
    using var app = new AutomationController(dir, NewGovee());
    app.Initialize();
    var t0 = DateTime.UtcNow;
    app.HandleEvent(new StreamEvent { Type = EventTypes.Tip, RawType = "tip.received", DedupeKey = "dw:a1", User = "Kees", TotalCoins = 1500, OccurredAtUtc = t0 });
    app.HandleEvent(new StreamEvent { Type = EventTypes.Tip, RawType = "tip.received", DedupeKey = "dw:a2", User = "Lies", TotalCoins = 500, TipKind = TipKinds.Shoutout, GiftCount = 1, CoinsPerItem = 500, OccurredAtUtc = t0 });
    Eq(2, app.Donations.Count);
    var j = System.Text.Json.JsonSerializer.SerializeToElement(app.Donations.Snapshot(app.Settings.Overlays, t0));
    Eq("Lies", j.GetProperty("last").GetProperty("name").GetString());
    var al = j.GetProperty("alerts");
    var tip = al.EnumerateArray().First(a => a.GetProperty("name").GetString() == "Kees");
    var so = al.EnumerateArray().First(a => a.GetProperty("name").GetString() == "Lies");
    var ms0 = new DateTimeOffset(t0).ToUnixTimeMilliseconds();
    True(Math.Abs(tip.GetProperty("showAt").GetInt64() - (ms0 - 140)) < 5, "gewone tip: tegelijk met DonateWave");
    True(so.GetProperty("showAt").GetInt64() >= ms0 + 790 + 7500 + 795 - 5, "shout-out: na de animatie");
    await Task.CompletedTask;
    app.Queue.StopAll();
});

Test("Follow: 2 follows na elkaar – licht voor de tweede pas als de sound van de eerste klaar is", async () =>
{
    var dir = TempDir();
    using var lamp = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort);
    using var app = new AutomationController(dir, NewGovee());
    app.Initialize();
    app.Settings.Lighting.CommandDelayMs = 30; app.Settings.Lighting.LatencyMs = 0; app.Settings.Queue.GapMs = 0;
    app.Settings.Devices.Add(new GoveeDeviceConfig { Name = "H619E", Ip = "127.0.0.1", Sku = "H619E" });
    var follow = app.Settings.Triggers.First(t => t.EventType == EventTypes.Follow && t.Enabled);
    follow.CooldownSeconds = 0; follow.MaxInQueue = 0;
    var showMs = app.Settings.Shows.Single(x => x.Id == follow.ShowId).TotalDurationMs;
    follow.SoundDurationSeconds = "4,5";
    Eq(4500, follow.SoundDurationMs, "4,5 s ingevuld");
    True(showMs < 4500, $"show ({showMs} ms) is korter dan de sound");
    var starts = new List<DateTime>();
    app.Queue.Started += _ => { lock (starts) starts.Add(DateTime.UtcNow); };
    StreamEvent Ev(string u) => new() { Type = EventTypes.Follow, RawType = "follow", User = u, IsTest = true, DedupeKey = "f:" + u + Guid.NewGuid().ToString("N") };
    app.HandleEvent(Ev("anna"));
    app.HandleEvent(Ev("bram"));
    await WaitFor(() => { lock (starts) return starts.Count == 2; }, 9000, "beide follows gestart");
    var gap = (starts[1] - starts[0]).TotalMilliseconds;
    True(gap >= 4480 && gap < 4900, $"tweede follow pas na 4,5 s sound ({gap:0} ms)");
    // Zonder sound-duur: direct na de show
    follow.SoundDurationMs = 0;
    starts.Clear();
    app.HandleEvent(Ev("cor")); app.HandleEvent(Ev("dirk"));
    await WaitFor(() => { lock (starts) return starts.Count == 2; }, 9000, "beide follows gestart (zonder sound-duur)");
    var gap2 = (starts[1] - starts[0]).TotalMilliseconds;
    True(gap2 < showMs + 600, $"zonder sound-duur direct na de show ({gap2:0} ms, show {showMs} ms)");
    app.Queue.StopAll();
});

Test("DonateWave shout-out: show start = tip + 790 ms + animatie + 795 ms (gemeten op de overlay)", async () =>
{
    var dir = TempDir();
    using var lamp = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort);
    using var app = new AutomationController(dir, NewGovee());
    app.Initialize();
    app.Settings.Lighting.CommandDelayMs = 30; app.Settings.Lighting.LatencyMs = 0; app.Settings.Queue.GapMs = 0;
    app.Settings.Devices.Add(new GoveeDeviceConfig { Name = "H619E", Ip = "127.0.0.1", Sku = "H619E" });
    app.Settings.DonateWave.OverlayDelayMs = 300;
    var spiky = app.Settings.Triggers.Single(t => t.Id == "tw-special-spikygroove");
    Eq(7500, spiky.StartDelayMs, "animatie Spiky Groove");
    spiky.StartDelayMs = 400;                       // korter voor de test
    DateTime? started = null;
    app.Queue.Started += _ => started ??= DateTime.UtcNow;
    var t0 = DateTime.UtcNow;
    app.HandleEvent(new StreamEvent { Type = EventTypes.Tip, RawType = "tip.received", TotalCoins = 500, TipKind = TipKinds.Shoutout,
                                      IsTest = true, OccurredAtUtc = t0, DedupeKey = "dw:t-" + Guid.NewGuid().ToString("N") });
    await WaitFor(() => started != null, 4000, "show gestart");
    var ms = (started!.Value - t0).TotalMilliseconds;
    True(ms >= 2265 && ms < 2650, $"start na 300 + 790 + 400 + 795 = 2285 ms ({ms:0} ms)");
    app.Queue.StopAll();
});

Test("DonateWave: 2 shout-outs tegelijk = animatie 1, animatie 2 + sound 2, dan sound 1 (gemeten)", async () =>
{
    var dir = TempDir();
    using var lamp = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort);
    using var app = new AutomationController(dir, NewGovee());
    app.Initialize();
    app.Settings.Lighting.CommandDelayMs = 30; app.Settings.Lighting.LatencyMs = 0; app.Settings.Queue.GapMs = 0;
    app.Settings.Devices.Add(new GoveeDeviceConfig { Name = "H619E", Ip = "127.0.0.1", Sku = "H619E" });
    app.Settings.DonateWave.OverlayDelayMs = 200;
    app.Settings.DonateWave.AlertGapMs = 300;
    app.Settings.DonateWave.BetweenAnimationsMs = 500;
    var shortShow = new LightShow { Id = "kort", Name = "Kort", Steps = new ObservableCollection<LightStep> { LightStep.Color("#FF0000", 500), LightStep.Color("#00FF00", 500) } };
    app.Settings.Shows.Add(shortShow);
    foreach (var id in new[] { "tw-special-spikygroove", "tw-special-enchantedhat" })
    {
        var t = app.Settings.Triggers.Single(x => x.Id == id);
        t.ShowId = "kort"; t.StartDelayMs = 400;
    }
    var starts = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>();
    app.Queue.Started += i => starts[i.TriggerId] = DateTime.UtcNow;
    var t0 = DateTime.UtcNow;
    StreamEvent Ev(long cents, string id) => new() { Type = EventTypes.Tip, RawType = "tip.received", TotalCoins = cents, TipKind = TipKinds.Shoutout,
                                                     IsTest = true, OccurredAtUtc = t0, DedupeKey = "dw:" + id + Guid.NewGuid().ToString("N") };
    app.HandleEvent(Ev(500, "a"));   // Spiky Groove
    app.HandleEvent(Ev(800, "b"));   // Enchanted Hat, op hetzelfde moment gestuurd
    await WaitFor(() => starts.Count == 2, 10000, "beide shows gestart");
    var spiky = (starts["tw-special-spikygroove"] - t0).TotalMilliseconds;
    var hat = (starts["tw-special-enchantedhat"] - t0).TotalMilliseconds;
    // anim 1: 200+790 → 990..1390 · anim 2: 1390+500 → 1890..2290 · sound 2: +795 = 3085 · klaar: 3085 + max(1000+300, 2200) = 5285
    // sound 1: 5285 + 1500 = 6785
    True(hat >= 3065 && hat < 3400, $"tweede shout-out speelt als eerste zijn sound ({hat:0} ms)");
    True(spiky >= 6765 && spiky < 7100, $"sound van de eerste komt als laatste ({spiky:0} ms)");
    app.Queue.StopAll();
});

TestSync("DonateWave-overlaywachtrij: volgorde en tijden zoals gemeten op de echte overlay", () =>
{
    var dir = TempDir();
    using var app = new AutomationController(dir, NewGovee());
    app.Initialize();
    Eq(0, app.Settings.DonateWave.OverlayDelayMs, "standaard geen correctie");
    Eq(3240, app.Settings.DonateWave.AlertGapMs, "voorleesstem gemeten");
    Eq(1735, app.Settings.DonateWave.BetweenAnimationsMs, "animatie → animatie gemeten");
    var t0 = DateTime.UtcNow.AddSeconds(5);
    // Meting: Spiky Groove alleen → sound ±9,1 s na created_at; Desert King → ±12,2 s
    Eq(790.0 + 7500 + 795, (app.DonateWaveSoundStart(t0, 7500, 29727) - t0).TotalMilliseconds, "Spiky Groove");
    var tDk = t0.AddSeconds(100);
    Eq(790.0 + 10560 + 795, (app.DonateWaveSoundStart(tDk, 10560, 35709) - tDk).TotalMilliseconds, "Desert King");
    // Gewone tip, overlay vrij: sound 140 ms vóór created_at
    var tN = t0.AddSeconds(300);
    Eq(-140.0, (app.DonateWaveSoundStart(tN, 0, 27115) - tN).TotalMilliseconds, "gewone tip");
    // Gewone tip terwijl de overlay bezig is: wacht tot sound + voorleesstem klaar zijn, dan +1500
    var tB = t0.AddSeconds(500);
    var s1 = app.DonateWaveSoundStart(tB, 7000, 50000);
    var s2 = app.DonateWaveSoundStart(tB.AddSeconds(10), 0, 20000);
    Eq(790.0 + 7000 + 795, (s1 - tB).TotalMilliseconds);
    Eq(790.0 + 7000 + 795 + 50000 + 3240 + 1500, (s2 - tB).TotalMilliseconds, "na sound 1 + voorleesstem + 1500");
    // 3 shout-outs vlak na elkaar (gemeten: Royal Voyage, Spiky Groove, Enchanted Hat)
    var t3 = t0.AddSeconds(800);
    app.DonateWaveSoundStart(t3, 6374, 28500);
    app.DonateWaveSoundStart(t3.AddMilliseconds(300), 7500, 29727);
    app.DonateWaveSoundStart(t3.AddMilliseconds(600), 7300, 27637);
    var p = app.DonateWavePlannedStarts().TakeLast(3).Select(x => (x - t3).TotalMilliseconds).ToArray();
    var aEnd = 790 + 6374;                            // animatie 1 klaar
    var bSound = aEnd + 1735 + 7500 + 795;            // animatie 2 + sound 2
    var bEnd = bSound + 29727 + 3240;
    var cSound = bEnd + 2437 + 7300 + 795;            // animatie 3 + sound 3
    var cEnd = cSound + 27637 + 3240;
    Eq((double)bSound, p[1], "shout-out 2 direct na zijn animatie");
    Eq((double)cSound, p[2], "shout-out 3 daarna");
    Eq((double)(cEnd + 1500), p[0], "sound van shout-out 1 als laatste");
    True(p[1] < p[2] && p[2] < p[0], "volgorde 2, 3, 1");
    // Gewone tip tijdens een animatie: duwt de shout-out niet opzij
    var t4 = t0.AddSeconds(1200);
    var q1 = app.DonateWaveSoundStart(t4, 6000, 10000, quantity: 2);
    var q2 = app.DonateWaveSoundStart(t4.AddSeconds(1), 0, 5000);
    Eq(790.0 + 6000 + 795, (q1 - t4).TotalMilliseconds, "shout-out ×2 niet uitgesteld");
    Eq(790.0 + 6000 + 795 + 20000 + 3240 + 1500, (q2 - t4).TotalMilliseconds, "gewone tip na 2× de sound");
    // Shout-out komt binnen terwijl gewone tips wachten: gaat vóór die tips
    var t5 = t0.AddSeconds(1600);
    var n1 = app.DonateWaveSoundStart(t5, 0, 30000);
    var n2 = app.DonateWaveSoundStart(t5.AddSeconds(1), 0, 30000);
    var sp = app.DonateWaveSoundStart(t5.AddSeconds(2), 5000, 20000);
    var n2b = app.DonateWavePlannedStarts()[^2];
    var n1End = -140 + 30000 + 3240;
    Eq((double)(n1End + 2437 + 5000 + 795), (sp - t5).TotalMilliseconds, "shout-out met voorrang");
    Eq((double)(n1End + 2437 + 5000 + 795 + 20000 + 3240 + 1500), (n2b - t5).TotalMilliseconds, "gewone tip daarna");
    // Correctie verschuift alles
    app.Settings.DonateWave.OverlayDelayMs = 250;
    var t6 = t0.AddSeconds(2200);
    Eq(250.0 + 790 + 6000 + 795, (app.DonateWaveSoundStart(t6, 6000, 30000) - t6).TotalMilliseconds, "correctie +250");
});

TestSync("DonateWave: overlay-testtip (special, quantity) uit de echte API wordt goed gelezen en gekozen", () =>
{
    var s = DefaultSettings();
    const string body = "{\"events\":[{\"id\":\"a1\",\"type\":\"integration.test\",\"cursor\":\"60\",\"created_at\":\"2026-11-12T17:43:17.159312+00:00\",\"data\":{\"test\":true,\"source\":\"overlay_test_tip\",\"tip_id\":\"x\",\"special\":true,\"currency\":\"eur\",\"quantity\":1,\"lucky_wheel\":false,\"amount_cents\":800,\"display_name\":\"TestViewer\"}},{\"id\":\"a2\",\"type\":\"tip.received\",\"cursor\":\"61\",\"created_at\":\"2026-11-12T17:43:18.381191+00:00\",\"data\":{\"special\":true,\"currency\":\"eur\",\"quantity\":2,\"lucky_wheel\":false,\"amount_cents\":1000,\"display_name\":\"Fan\"}}],\"next_cursor\":\"61\",\"poll_after_seconds\":2}";
    var dw = new DonateWaveClient(new ErrorCenter()) { UtcNow = () => new DateTime(2026, 11, 12, 17, 43, 20, DateTimeKind.Utc) };
    var got = new List<StreamEvent>(); dw.EventReceived += e => got.Add(e);
    dw.Process(body);
    Eq(2, got.Count);
    Eq(TipKinds.Shoutout, got[0].TipKind); Eq(true, got[0].IsTest); Eq(1, got[0].GiftCount);
    Eq("tw-special-enchantedhat", RuleEngine.SelectTrigger(got[0], s.Triggers)?.Id, "€ 8 shout-out = Enchanted Hat");
    Eq(2, got[1].GiftCount, "quantity 2"); Eq(500L, got[1].CoinsPerItem); Eq("€ 10,00 · shout-out ×2", got[1].GiftName);
    Eq("tw-special-spikygroove", RuleEngine.SelectTrigger(got[1], s.Triggers)?.Id, "2× Spiky Groove (€ 5 per stuk), niet Starlight Kingdom (€ 10)");
});

TestSync("DonateWave: bedrag kiest de juiste regel (gelijk aan DonateWave › Alert-effecten)", () =>
{
    var s = DefaultSettings();
    EventTrigger? Pick(long cents) => RuleEngine.SelectTrigger(new StreamEvent { Type = EventTypes.Tip, TotalCoins = cents }, s.Triggers);
    Eq("tw-1", Pick(100)?.Id); Eq("tw-1", Pick(999)?.Id);
    Eq("tw-10", Pick(1000)?.Id); Eq("tw-10", Pick(2999)?.Id);
    Eq("tw-30", Pick(3000)?.Id); Eq("tw-30", Pick(50000)?.Id);
    Eq("€ 10,00 – € 29,99", s.Triggers.Single(t => t.Id == "tw-10").ConditionSummary);
    var t = new EventTrigger { EventType = EventTypes.Tip };
    t.MinEuro = "4,50"; t.MaxEuro = "€ 9.99";
    Eq(450, t.MinCoins); Eq(999, t.MaxCoins); Eq("4,50", t.MinEuro);
    t.MaxEuro = ""; Eq(null, t.MaxCoins);
    foreach (var r in Presets.DonateWaveRules()) True(s.FindShow(r.ShowId) != null, r.Name + " heeft een show");
});

Test("DonateWave: test vanuit DonateWave › Koppelingen speelt de show van de app, cursor wordt bewaard", async () =>
{
    var dir = TempDir();
    var http = new FakeDonateWave();
    http.Responses.Add((200, FakeDonateWave.Tip("77", "t1", 1500, DateTime.UtcNow, type: "integration.test", name: "Gustavo")));
    http.Responses.Add((200, "{\"events\":[],\"next_cursor\":\"77\",\"poll_after_seconds\":2}"));
    using var app = new AutomationController(dir, NewGovee(), donateWaveHttp: http);
    app.Initialize();
    app.DonateWave.BaseUrl = "https://dw.test"; app.DonateWave.DelayScale = 0.01;
    app.SetDonateWaveKey("dw_evt_abc");
    True(!app.Settings.DonateWave.ApiKeyProtected.Contains("dw_evt_abc"), "sleutel niet leesbaar opgeslagen");
    app.Settings.DonateWave.Enabled = true;
    app.ApplyDonateWave();
    await WaitFor(() => app.History.Snapshot().Any(h => h.Type == EventTypes.Tip), 4000, "tip in de geschiedenis");
    var h = app.History.Snapshot().First(x => x.Type == EventTypes.Tip);
    Eq("♪ B.O.A.", h.ShowName, "€ 15 → regel € 10 – € 29,99 = B.O.A., net als in DonateWave");
    Eq(true, h.IsTest);
    await WaitFor(() => app.Settings.DonateWave.Cursor == "77", 2000, "cursor bewaard");
    await app.DonateWave.StopAsync();
});

// ---------------------------------------------------------------- Volledige workflow

Test("End-to-end: BetterTok follow → regel → wachtrij → 2× blauw op de lamp", async () =>
{
    var dir = TempDir();
    using var lamp = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort);
    using var server = new FakeBetterTokServer(21297);
    using var app = new AutomationController(dir, NewGovee());
    app.Initialize();
    app.Settings.Lighting.CommandDelayMs = 30;
    app.Settings.Lighting.ShowBrightness = 100;
    app.Settings.Lighting.RestoreMode = RestoreMode.Previous;
    app.Settings.Queue.GapMs = 0;
    app.Settings.BetterTok.LocalUrl = "ws://127.0.0.1:21297";
    app.Settings.BetterTok.ScanPorts = false;
    app.Settings.Devices.Add(new GoveeDeviceConfig { Name = "H619E", Ip = "127.0.0.1", Sku = "H619E" });
    await app.StartAutomationAsync();
    Eq(ConnectionState.Connected, app.BetterTok.State, "BetterTok verbonden");

    await server.SendAsync("{\"event\":\"follow\",\"data\":{\"uniqueId\":\"nieuwe_fan\",\"nickname\":\"Nieuwe Fan\",\"msgId\":\"501\"}}");
    await WaitFor(() => app.History.Snapshot().Any(h => h.Status == ProcessingStatus.Played), 4000, "show afgespeeld");
    var h = app.History.Snapshot().First();
    Eq("Nieuwe Fan", h.User);
    Eq("Follow", h.ShowName);
    Eq(1, app.Stats.Follows);
    await WaitFor(() => app.Stats.ShowsPlayed == 1, 1000, "teller shows");
    Eq(2, lamp.ColorHistory().Count(c => c == "#3478F6"), "2× blauw geknipperd");
    True(lamp.ColorHistory().Contains("#000000"), "knipperen via zwart (snelle modus is standaard)");
    await WaitFor(() => lamp.Color == (255, 255, 255), 2000, "lamp hersteld naar wit");

    // Gift-combo: 3 tussenberichten + eindbericht = precies één effect, coins één keer geteld
    lamp.Reset();
    for (var i = 1; i <= 3; i++)
        await server.SendAsync($"{{\"event\":\"gift\",\"data\":{{\"uniqueId\":\"g\",\"giftName\":\"Doughnut\",\"giftType\":1,\"repeatCount\":{i},\"repeatEnd\":false,\"diamondCount\":20,\"msgId\":\"700\"}}}}");
    await server.SendAsync("{\"event\":\"gift\",\"data\":{\"uniqueId\":\"g\",\"giftName\":\"Doughnut\",\"giftType\":1,\"repeatCount\":4,\"repeatEnd\":true,\"diamondCount\":20,\"msgId\":\"700\"}}");
    await WaitFor(() => app.History.Snapshot().Count(x => x.Type == EventTypes.Gift && x.Status == ProcessingStatus.Played) == 1, 5000, "gift afgespeeld");
    await Task.Delay(800);
    Eq(1, app.History.Snapshot().Count(x => x.Type == EventTypes.Gift), "één regel in geschiedenis");
    Eq("Kleine gift", app.History.Snapshot().First(x => x.Type == EventTypes.Gift).ShowName);
    Eq(80L, app.Stats.Coins, "coins");
    Eq(4, app.Stats.Gifts, "gifts");

    // Safe Mode: event wordt gelogd maar niet afgespeeld
    app.SetSafeMode(true);
    lamp.Reset();
    await server.SendAsync("{\"event\":\"follow\",\"data\":{\"uniqueId\":\"tweede_fan\",\"msgId\":\"502\"}}");
    await WaitFor(() => app.History.Snapshot().Any(x => x.User == "tweede_fan"), 2000, "gelogd");
    await Task.Delay(400);
    Eq(ProcessingStatus.Ignored, app.History.Snapshot().First(x => x.User == "tweede_fan").Status);
    Eq(0, lamp.ColorHistory().Count, "geen licht in Safe Mode");
    app.SetSafeMode(false);

    // Testknop van een koppeling speelt echt af
    var trig = app.Settings.Triggers.First(t => t.EventType == EventTypes.Share);
    app.TestTrigger(trig);
    await WaitFor(() => lamp.ColorHistory().Contains("#9B5CFF"), 4000, "test-share paars");
    await Task.Delay(1500);
    Eq(2, app.Stats.ShowsPlayed, "follow + gift geteld, test telt niet mee");

    // Geschiedenis blijft bewaard
    app.History.SaveNow();
    var reloaded = new HistoryStore(dir);
    reloaded.Load();
    True(reloaded.Snapshot().Count >= 3, "geschiedenis opgeslagen");
});

// ---------------------------------------------------------------- Spotify-modus

TestSync("Spotify: nummer uit de vensternaam halen (spelen, pauze, reclame)", () =>
{
    var p = SpotifyWindowSource.ParseTitles(new[] { "", "Default IME", "Martin Garrix - Animals" });
    Eq(PlayerState.Playing, p.State);
    Eq("Martin Garrix", p.Track!.Artist);
    Eq("Animals", p.Track.Title);
    var dash = SpotifyWindowSource.ParseTitles(new[] { "Avicii - Wake Me Up - Radio Edit" });
    Eq("Wake Me Up - Radio Edit", dash.Track!.Title, "streepje in de titel blijft heel");
    foreach (var idle in new[] { "Spotify", "Spotify Premium", "Spotify Free", "Advertisement" })
    {
        var x = SpotifyWindowSource.ParseTitles(new[] { idle });
        Eq(PlayerState.Paused, x.State, idle);
        Eq(null, x.Track, idle);
    }
    Eq(PlayerState.Paused, SpotifyWindowSource.ParseTitles(Array.Empty<string>()).State, "venster zonder titel");
});

TestSync("Spotify: elk nummer eigen kleurvolgorde met alle 8 kleuren, altijd hetzelfde, elke wissel sterk contrast", () =>
{
    Eq(16, SpotifyPalette.CycleCount);
    var a = SpotifyPalette.For("martin garrix\u0001animals");
    Eq(string.Join(",", a), string.Join(",", SpotifyPalette.For("martin garrix\u0001animals")), "zelfde nummer = zelfde kleuren");
    var distinct = new HashSet<string>();
    for (var i = 0; i < 40; i++)
    {
        var pal = SpotifyPalette.For("artiest" + i + "\u0001nummer" + i);
        Eq(8, pal.Distinct().Count(), "alle 8 kleuren");
        for (var k = 0; k < 8; k++)
            True(SpotifyPalette.HueDistance(pal[k], pal[(k + 1) % 8]) >= 100, $"{pal[k]} → {pal[(k + 1) % 8]} lijkt te veel op elkaar");
        distinct.Add(string.Join(",", pal));
    }
    True(distinct.Count >= 10, $"verschillende nummers krijgen een andere volgorde ({distinct.Count}/40)");
});

TestSync("Spotify: beatdetectie vindt de kicks, niet de ruis", () =>
{
    var d = new BeatDetector { Sensitivity = 0.5 };
    var rnd = new Random(3);
    var beats = 0;
    // 20 s op 150 BPM (400 ms per beat), meting elke 20 ms
    for (long t = 0; t < 20000; t += 20)
    {
        var phase = t % 400;
        var peak = phase < 60 ? 0.9 : 0.2 + rnd.NextDouble() * 0.08;
        if (d.Push(peak, t)) beats++;
    }
    True(beats is >= 40 and <= 52, $"{beats} beats gevonden, verwacht ±50");
    var flat = new BeatDetector { Sensitivity = 0.5 };
    var noise = 0;
    for (long t = 0; t < 20000; t += 20) if (flat.Push(0.3 + rnd.NextDouble() * 0.05, t)) noise++;
    True(noise <= 3, $"{noise} beats in constante ruis");
    var silent = new BeatDetector();
    for (long t = 0; t < 3000; t += 20) silent.Push(0, t);
    True(silent.IsSilent(3000), "stilte herkend");
});

Test("Spotify-modus: kleuren op de beat, nooit sneller dan de limiet, nieuw nummer = nieuwe kleuren, pauze, terug naar Stream", async () =>
{
    var dir = TempDir();
    using var lamp = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort);
    var player = new FakeNowPlaying();
    var audio = new FakeAudio { Bpm = 150 };
    using var app = new AutomationController(dir, NewGovee(), player, () => audio);
    app.Initialize();
    app.Settings.Lighting.CommandDelayMs = 30;
    app.Settings.Lighting.ShowBrightness = 100;
    app.Settings.Lighting.MinChangeMs = 550;
    app.Settings.Spotify.Rave = RaveMode.Never;   // dit test de gewone Spotify-stijl
    app.Settings.BetterTok.ScanPorts = false;
    app.Settings.BetterTok.LocalUrl = "ws://127.0.0.1:21299";
    app.Settings.Devices.Add(new GoveeDeviceConfig { Name = "H619E", Ip = "127.0.0.1", Sku = "H619E" });

    player.Play("Martin Garrix", "Animals");
    await app.SetModeAsync(AppMode.Spotify);
    Eq(AppMode.Spotify, app.Mode);
    True(app.Spotify.Running, "Spotify-modus draait");
    Eq(false, app.IsRunning, "BetterTok-automatisering staat uit");
    await Task.Delay(9000);

    var pal = SpotifyPalette.For(new NowPlaying("Martin Garrix", "Animals").Key);
    var colors = lamp.Commands.Where(c => c.cmd == "colorwc").ToList();
    True(colors.Count is >= 8 and <= 18, $"{colors.Count} kleurwissels in 9 s (verwacht ±1,25 per seconde)");
    for (var i = 1; i < colors.Count; i++)
    {
        var gap = (colors[i].at - colors[i - 1].at).TotalMilliseconds;
        True(gap >= 520, $"wissel {i} na {gap:0} ms, sneller dan de strip aankan");
    }
    var hist = lamp.ColorHistory();
    Eq(pal[0], hist[0], "begint met de eerste kleur van dit nummer");
    var colored = hist.Where(h => h is not ("#FFFFFF" or "#000000")).ToList();
    True(colored.Distinct().Count() >= 4, "meerdere kleuren: " + string.Join(" ", colored.Distinct()));
    for (var i = 1; i < colored.Count; i++)
        True(colored[i] == colored[i - 1] || SpotifyPalette.HueDistance(colored[i], colored[i - 1]) >= 100, $"{colored[i - 1]} → {colored[i]} te weinig contrast");
    True(hist.Contains("#FFFFFF"), "flashbang gezien: " + string.Join(" ", hist));
    True(hist.Contains("#000000"), "zwart stukje gezien");
    True(Math.Abs(app.Spotify.Bpm - 150) < 6, $"tempo herkend ({app.Spotify.Bpm:0} BPM)");
    True(app.Spotify.BeatCount >= 10, $"beats gemeten ({app.Spotify.BeatCount})");
    Eq("Animals", app.Spotify.Snapshot.Track!.Title);

    // BetterTok-event tijdens Spotify-modus doet niets
    var ev = app.Simulate(new StreamEvent { Type = EventTypes.Follow, RawType = "follow", User = "spotifytest", DisplayName = "x", DedupeKey = "sp-1" });
    Eq(ProcessingStatus.Ignored, ev!.Status);
    Eq("Spotify-modus staat aan", ev.Detail);

    // Ander nummer → andere kleuren
    var pal2 = SpotifyPalette.For(new NowPlaying("Avicii", "Levels").Key);
    lamp.Reset();
    player.Play("Avicii", "Levels");
    await WaitFor(() => lamp.ColorHistory().Count >= 3, 4000, "kleuren voor het nieuwe nummer");
    Eq(pal2[0], lamp.ColorHistory()[0], "nieuw nummer begint met zijn eigen eerste kleur");

    // Pauze → strip brandt zacht in de eerste kleur
    player.Pause();
    await WaitFor(() => app.Spotify.Snapshot.State == PlayerState.Paused, 2000, "pauze gezien");
    await Task.Delay(1200);
    lamp.Reset();
    await Task.Delay(1500);
    Eq(0, lamp.ColorHistory().Count, "geen wissels tijdens pauze");
    var c = lamp.Color;
    True(c.r + c.g + c.b < 200, $"zacht licht bij pauze ({c})");

    // Safe Mode: niets naar de lamp
    player.Play("Avicii", "Levels");
    app.SetSafeMode(true);
    await Task.Delay(300);
    lamp.Reset();
    await Task.Delay(1500);
    Eq(0, lamp.ColorHistory().Count, "Safe Mode stopt ook Spotify-licht");
    app.SetSafeMode(false);

    // Terug naar Stream-modus: Spotify-licht stopt
    await app.SetModeAsync(AppMode.Stream);
    Eq(false, app.Spotify.Running);
    Eq(AppMode.Stream, app.Settings.General.Mode);
    await Task.Delay(300);
    lamp.Reset();
    await Task.Delay(1500);
    Eq(0, lamp.ColorHistory().Count, "geen Spotify-licht meer in Stream-modus");
});

Test("Spotify-modus: zonder geluidsmeting wisselt het op een rustig vast tempo, stijl Rustig en Eén kleur", async () =>
{
    using var lamp = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort);
    var player = new FakeNowPlaying();
    var audio = new FakeAudio { Broken = true };
    var s = DefaultSettings();
    s.Lighting.CommandDelayMs = 30;
    s.Lighting.MinChangeMs = 550;
    var engine = new LightShowEngine(NewGovee(), () => s.Lighting);
    var errors = new ErrorCenter();
    var sp = new SpotifyLightController(engine, () => new[] { "127.0.0.1" }, () => s, null, errors, player, () => audio);
    player.Play("Noah", "Shivers");
    sp.Start();
    await Task.Delay(5500);
    var n = lamp.ColorHistory().Count;
    True(n is >= 3 and <= 7, $"{n} wissels in 5,5 s zonder geluid (±1 per seconde)");
    True(errors.Snapshot().Any(e => e.Key == "sp.audio"), "melding dat geluid niet gemeten kan worden");
    True(!sp.AudioAvailable, "geen geluid beschikbaar");

    // Eén kleur per nummer: alleen de hoofdkleur, helderheid pulseert
    audio.Broken = false;
    s.Spotify.Style = SpotifyStyle.PerSong;
    player.Play("Noah", "Andere");
    await Task.Delay(1500);
    lamp.Reset();
    await Task.Delay(3000);
    var main = SpotifyPalette.For(new NowPlaying("Noah", "Andere").Key)[0];
    True(lamp.ColorHistory().Count >= 2, "pulseert op de beat");
    True(lamp.ColorHistory().All(h => SpotifyPalette.HueDistance(h, main) < 3), "alleen de kleur van het nummer");

    // Rustig: één kleur per ±2 s met zachte overgang
    s.Spotify.Style = SpotifyStyle.Calm;
    s.Spotify.CalmSeconds = 2;
    lamp.Reset();
    await Task.Delay(4600);
    var calm = lamp.Commands.Where(x => x.cmd == "colorwc").ToList();
    for (var i = 1; i < calm.Count; i++)
        True((calm[i].at - calm[i - 1].at).TotalMilliseconds >= 520, "overgang niet sneller dan de limiet");
    await sp.StopAsync();
    Eq(false, sp.Running);
    sp.Dispose();
});

TestSync("Spotify: tempo volgen, drop herkennen, vertraging uitrekenen", () =>
{
    var t = new TempoTracker();
    var rnd = new Random(5);
    for (long b = 0; b < 4000; b += 400)
    {
        t.AddBeat(b + rnd.Next(-8, 9));
        if (b % 1600 == 800) t.AddBeat(b + 200);        // tussenklap (hi-hat)
    }
    True(t.Locked, "tempo vast");
    True(Math.Abs(t.Bpm - 150) < 4, $"150 BPM herkend ({t.Bpm:0})");
    var next = t.NextBeatAtOrAfter(4010);
    True(Math.Abs(next - 4000) < 25 || Math.Abs(next - 4400) < 25, $"volgende beat voorspeld op {next}");
    t.Update(9000);
    Eq(false, t.Locked, "tempo losgelaten na lange stilte");

    var d = new BeatDetector();
    var drops = 0;
    // 6 s hard, 6 s breakdown, dan de drop
    for (long ms = 0; ms < 18000; ms += 20)
    {
        var loud = ms < 6000 || ms >= 12000;
        var peak = loud ? (ms % 400 < 60 ? 0.95 : 0.7) : 0.12;
        d.Push(peak, ms);
        if (d.CheckDrop(ms)) { drops++; True(ms >= 12000 && ms < 13500, $"drop op {ms} ms"); }
    }
    Eq(1, drops, "precies één drop");

    var flashes = Enumerable.Range(0, 10).Select(i => (long)(i * 1200)).ToList();
    var taps = flashes.Select((f, i) => f + 180 + (i % 3 - 1) * 25).ToList();
    taps.Add(5000);                                         // verkeerde tik
    Eq(180, LatencyCalibration.Compute(flashes, taps), "180 ms gemeten");
    Eq(null, LatencyCalibration.Compute(flashes, taps.Take(3).ToList()), "te weinig tikken");
});

Test("Spotify: met ‘licht eerder sturen’ gaat de wissel vóór de beat de deur uit (vertraging weg)", async () =>
{
    using var lamp = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort);
    var player = new FakeNowPlaying();
    var audio = new FakeAudio { Bpm = 150 };   // 400 ms per beat
    var s = DefaultSettings();
    s.Lighting.CommandDelayMs = 30;
    s.Lighting.MinChangeMs = 550;
    s.Lighting.LatencyMs = 150;
    var engine = new LightShowEngine(NewGovee(), () => s.Lighting);
    using var sp = new SpotifyLightController(engine, () => new[] { "127.0.0.1" }, () => s, null, null, player, () => audio);
    player.Play("Test", "Tempo");
    sp.Start();
    await WaitFor(() => sp.Bpm > 0, 6000, "tempo vast");
    await Task.Delay(800);
    lamp.Reset();
    await Task.Delay(5000);
    var sends = lamp.Commands.Where(c => c.cmd == "colorwc").Select(c => c.at).ToList();
    True(sends.Count >= 4, $"{sends.Count} wissels");
    // fase t.o.v. de kick: 150 ms vóór de beat = fase ±250 van 400 (plus een paar ms meet- en verzendtijd)
    var phases = sends.Select(a => audio.PhaseOf(a)).ToList();
    var early = phases.Count(p => p is >= 190 and <= 320);
    True(early >= sends.Count * 0.75, "wissels ±150 ms vóór de beat verstuurd, fasen: " + string.Join(", ", phases.Select(p => p.ToString("0"))));
    for (var i = 1; i < sends.Count; i++)
        True((sends[i] - sends[i - 1]).TotalMilliseconds >= 520, "niet sneller dan de limiet");
    await sp.StopAsync();
});

// ---------------------------------------------------------------- Direct springen (realtime-protocol)

TestSync("Govee direct: razer-pakketten hebben de juiste opbouw en controlecode", () =>
{
    Eq("uwABsQEK", Convert.ToBase64String(GoveeLanClient.DirectEnablePacket(true)), "aanzetten = BB 00 01 B1 01 0A (zoals OpenRGB)");
    Eq("uwABsQAL", Convert.ToBase64String(GoveeLanClient.DirectEnablePacket(false)), "uitzetten = BB 00 01 B1 00 0B");
    var p = GoveeLanClient.DirectColorPacket(0xFF, 0x00, 0x80, 20);
    Eq(0xBB, (int)p[0]); Eq(0xB0, (int)p[3]); Eq(1, (int)p[4], "geen verloop"); Eq(20, (int)p[5], "20 vakken");
    Eq(62, (p[1] << 8) | p[2], "lengte 2 + 3×20");
    Eq(5 + 62, p.Length);
    byte x = 0; for (var i = 0; i < p.Length - 1; i++) x ^= p[i];
    Eq(x, p[^1], "XOR-controlecode");
});

Test("Govee direct: lichtshow springt via realtime-protocol, vaste kleur en herstel weer via standaardcommando", async () =>
{
    using var lamp = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort);
    var opts = new LightingOptions { ShowBrightness = 100, CommandDelayMs = 30, MinChangeMs = 300, DirectColor = true, RestoreMode = RestoreMode.Previous };
    var engine = new LightShowEngine(NewGovee(), () => opts);
    var ips = new[] { "127.0.0.1" };
    var baseline = await engine.CaptureAsync(ips);
    var show = new LightShow { Name = "t", Steps = new ObservableCollection<LightStep>
        { LightStep.Color("#FF0000", 400, 100, 1, 0), LightStep.Color("#0047FF", 400, 100, 1, 0), LightStep.Color("#00FF3C", 400, 100, 1, 0) } };
    await engine.PlayAsync(show, ips, CancellationToken.None);
    Eq(0, lamp.BadPackets, "geen kapotte pakketten");
    Eq("#FF0000,#0047FF,#00FF3C", string.Join(",", lamp.DirectColors), "kleuren direct ontvangen");
    Eq(0, lamp.ColorHistory().Count, "geen gewone colorwc tijdens de show");
    Eq(1, lamp.Commands.Count(c => c.cmd == "razer" && Convert.FromBase64String(c.data.GetProperty("pt").GetString()!)[3] == 0xB1), "directe modus één keer aangezet");
    True(lamp.DirectOn, "strip in realtime-modus");
    await engine.RestoreAsync(baseline, ips);
    Eq(false, lamp.DirectOn, "na de show uit de realtime-modus");
    Eq((255, 255, 255), lamp.Color, "vorige kleur hersteld via het standaardcommando");

    // Uitgezet: weer gewoon colorwc
    lamp.Reset();
    opts.DirectColor = false;
    engine.ForgetState();
    await engine.PlayAsync(show, ips, CancellationToken.None);
    Eq(3, lamp.ColorHistory().Count, "gewone modus gebruikt colorwc");
    Eq(0, lamp.DirectColors.Count);
});

// ---------------------------------------------------------------- Club-versies en lichtshow-afbeeldingen

static string FixturePath(string name)
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Fixtures", name))) dir = dir.Parent;
    return dir == null ? throw new FileNotFoundException(name) : Path.Combine(dir.FullName, "Fixtures", name);
}

TestSync("Festival-versies: drops uit de audio, overal felle kleur (nergens zwart), flash op de kicks, strobe wit ↔ kleur ≤ 2 maten, strip A/B", () =>
{
    foreach (var song in SongShows.All.Where(x => !Presets.CozySongs.Contains(x.Key)))   // gezellige shows hebben bewust geen flashes
    {
        var show = Presets.Create(song.Key, fast: true);
        var safe = Presets.Create(song.Key, fast: false);
        True(Math.Abs(show.TotalDurationMs - safe.TotalDurationMs) < 100, $"{song.Key}: festival en veilig even lang");
        True(show.Steps.All(st => st.Kind == StepKind.Color && st.ColorHex != "#000000"), song.Key + ": nergens zwart");
        True(show.Steps.All(st => st.Brightness >= 50), song.Key + ": overal fel (≥ 50 %)");
        True(show.Steps.All(st => st.DurationMs >= 60), song.Key + ": kortste stap ≥ 60 ms");
        var beatMs = 60000 / song.Bpm;
        bool Quarter(LightStep st) => Math.Abs(st.DurationMs - beatMs / 4) < 3;
        True(show.Steps.Count(st => st.ColorHex == "#FFFFFF" && Quarter(st)) >= 12, song.Key + ": witte flashes op de kicks");
        True(show.Steps.Any(st => st.IsSplit), song.Key + ": strip A/B om-en-om");
        // strobe = aaneengesloten ¼-beat-stappen waarvan elke tweede wit: minstens één maat, nooit langer dan 2 maten
        double run = 0, longest = 0;
        for (var i = 0; i < show.Steps.Count; i++)
        {
            var st = show.Steps[i];
            var inStrobe = Quarter(st) && i + 1 < show.Steps.Count && Quarter(show.Steps[i + 1])
                           && (st.ColorHex == "#FFFFFF") != (show.Steps[i + 1].ColorHex == "#FFFFFF") && !st.IsSplit && !show.Steps[i + 1].IsSplit;
            run = inStrobe ? run + st.DurationMs : 0;
            longest = Math.Max(longest, run);
        }
        True(longest >= 4 * beatMs * 0.85, $"{song.Key}: strobe-maat aanwezig ({longest:0} ms)");
        True(longest <= 2 * 4 * beatMs + 5, $"{song.Key}: strobe {longest:0} ms > 2 maten");
        var colored = show.Steps.Where(st => st.ColorHex is not "#FFFFFF").Select(st => st.ColorHex).Distinct().ToList();
        True(colored.Count >= 3, song.Key + ": minstens 3 felle kleuren");
    }
    var expected = new Dictionary<string, int> { ["song-nevercomingdown"] = 43807, ["song-shivers"] = 81737, ["song-numb"] = 38348, ["song-microwave"] = 16666 };
    foreach (var (key, ms) in expected) Eq(ms, Presets.Create(key, true).TotalDurationMs, key + ": exact de lengte van de sound");
    // elk nummer zijn eigen herkenbare kleurenpaar (Numb en Numb epic verwant maar niet gelijk)
    string Pair(string k) => string.Join("+", Presets.Create(k, true).Steps.Where(st => st.ColorHex != "#FFFFFF").Select(st => st.ColorHex).Distinct().OrderBy(x => x));
    True(SongShows.All.Select(x => Pair(x.Key)).Distinct().Count() >= 26, "(bijna) elke song een eigen kleurenset");
    True(Pair("song-numb") != Pair("song-numbepic") && Pair("song-numb").Split('+').Intersect(Pair("song-numbepic").Split('+')).Any(), "Numb en Numb (epic) delen één kleur");
    foreach (var key in Presets.FastKeys)
        foreach (var st in Presets.Create(key, fast: true).Steps)
        {
            var phase = st.Flashes > 1 ? st.DurationMs / (double)st.Flashes * 0.45 : st.DurationMs;
            True(phase >= 300 * 0.8, $"{key} club: fase {phase:0} ms te snel voor 300 ms-limiet");
        }
});

Test("Engine: strip A/B om-en-om met 2 lampen (geen segmenten), 1 lamp = hele strip kleur A; ¼-beat-strobe in directe modus", async () =>
{
    using var lamp = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort);
    using var lamp2 = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort, "127.0.0.2");
    var opts = new LightingOptions { ShowBrightness = 100, CommandDelayMs = 80, MinChangeMs = 40, DirectColor = true };
    var engine = new LightShowEngine(NewGovee(), () => opts);
    var show = new LightShow { Steps = new ObservableCollection<LightStep> { LightStep.Split("#FF0000", "#00E5FF", 188), LightStep.Split("#00E5FF", "#FF0000", 188) } };
    await engine.PlayAsync(show, new[] { "127.0.0.1", "127.0.0.2" }, CancellationToken.None);
    await Task.Delay(100);
    Eq("#FF0000,#00E5FF", string.Join(",", lamp.DirectColors), "strip A: rood → cyaan");
    Eq("#00E5FF,#FF0000", string.Join(",", lamp2.DirectColors), "strip B: cyaan → rood");
    Eq(0, lamp.Commands.Count(c => c.cmd == "ptReal"), "geen segmenten");
    lamp.Reset(); lamp2.Reset(); engine.ForgetState();
    await engine.PlayAsync(show, new[] { "127.0.0.1" }, CancellationToken.None);
    await Task.Delay(100);
    Eq("#FF0000,#00E5FF", string.Join(",", lamp.DirectColors), "1 lamp: hele strip kleur A");
    Eq(0, lamp.Commands.Count(c => c.cmd == "ptReal"), "1 lamp: geen helft/helft");

    // 1 maat witte strobe op ¼ beats (160 BPM): 16 flitsen in 1500 ms, allemaal aangekomen
    lamp.Reset(); engine.ForgetState();
    var strobe = new LightShow { Steps = new ObservableCollection<LightStep> { LightStep.Color("#FFFFFF", 1500, 100, 16), LightStep.Color("#FF0000", 200) } };
    var sw = Stopwatch.StartNew();
    await engine.PlayAsync(strobe, new[] { "127.0.0.1" }, CancellationToken.None);
    var whites = lamp.DirectColors.Count(c => c == "#FFFFFF");
    True(whites >= 15, $"{whites} van de 16 flitsen verstuurd");
    True(sw.ElapsedMilliseconds < 1850, $"op tijd ({sw.ElapsedMilliseconds} ms)");

    // Strobe-veiligheid aan: hooguit 3 flitsen per seconde
    lamp.Reset(); engine.ForgetState();
    opts.LimitStrobes = true;
    await engine.PlayAsync(strobe, new[] { "127.0.0.1" }, CancellationToken.None);
    True(lamp.DirectColors.Count(c => c == "#FFFFFF") <= 5, "strobe beperkt tot ±3 per seconde");
    lamp.Reset(); engine.ForgetState();
    var colorStrobe = new LightShow();
    for (var q = 0; q < 16; q++) colorStrobe.Steps.Add(LightStep.Color(q % 2 == 0 ? "#FFFFFF" : "#FF00CC", 94));
    await engine.PlayAsync(colorStrobe, new[] { "127.0.0.1" }, CancellationToken.None);
    True(lamp.DirectColors.Count(c => c == "#FFFFFF") <= 5, "ook wit ↔ kleur-strobe beperkt");
    opts.LimitStrobes = false;
});

TestSync("Opslag: versie 13 → 14: rave-shows en geen snelheidslimiet bij direct springen", () =>
{
    var s = new AppSettings { Version = 13 };
    s.Lighting.DirectColor = true; s.Lighting.MinChangeMs = 300;
    var old = Presets.Create("song-numb"); old.Steps.RemoveAt(0); s.Shows.Add(old);
    SettingsStore.Migrate(s);
    Eq(28, s.Version);
    Eq(40, s.Lighting.MinChangeMs);
    ShowTiers.Apply(s);
    Eq(Presets.ToData(Presets.Create("song-numb", true).Steps), Presets.ToData(s.Shows[0].Steps), "rave-versie actief");
});

TestSync("Club/veilig: app kiest vanzelf de versie die past bij de strip, eigen aanpassingen blijven", () =>
{
    var s = DefaultSettings();
    ShowTiers.Apply(s);
    var numb = s.Shows.Single(x => x.PresetKey == "song-numb");
    var police = s.Shows.Single(x => x.PresetKey == Presets.Police);
    var mine = s.Shows.Single(x => x.PresetKey == "song-kickpong");
    Eq(Presets.ToData(Presets.Create("song-numb", false).Steps), Presets.ToData(numb.Steps), "550 ms → veilige versie");
    mine.Steps[3].ColorHex = "#123456";                                   // zelf aangepast
    s.Lighting.MinChangeMs = 300;
    True(ShowTiers.Apply(s) >= 20, "songs en presets gewisseld");
    Eq(Presets.ToData(Presets.Create("song-numb", true).Steps), Presets.ToData(numb.Steps), "300 ms → club-versie");
    Eq(250, police.Steps[0].DurationMs, "Police club-versie");
    Eq("#123456", mine.Steps[3].ColorHex, "zelf aangepaste show niet overschreven");
    s.Lighting.MinChangeMs = 550;
    ShowTiers.Apply(s);
    Eq(600, police.Steps[0].DurationMs, "terug naar veilig");
    Eq("#123456", mine.Steps[3].ColorHex);
});

TestSync("Opslag: versie 12 → 13: songs krijgen twee versies, direct springen → club-tempo", () =>
{
    var s = new AppSettings { Version = 12 };
    s.Lighting.DirectColor = true;
    s.Lighting.MinChangeMs = 550;
    var old = Presets.Create("song-numb"); old.FastData = ""; old.SafeData = ""; old.Steps.RemoveAt(0); s.Shows.Add(old);
    SettingsStore.Migrate(s);
    Eq(28, s.Version);
    Eq(40, s.Lighting.MinChangeMs, "versie 13 zet 300, versie 14 daarna 40");
    True(s.Shows[0].HasTiers, "song heeft club + veilig");
    ShowTiers.Apply(s);
    Eq(Presets.ToData(Presets.Create("song-numb", true).Steps), Presets.ToData(s.Shows[0].Steps), "club-versie actief");
});

TestSync("Lichtshow-afbeelding: maken, inlezen, importeren, vervangen en koppelen aan coins", () =>
{
    // 1. Afbeelding gemaakt door Claude (Python) wordt door de app gelezen
    var bytes = File.ReadAllBytes(FixturePath("numb-lichtshow.png"));
    True(ShowFile.IsPng(bytes), "is een PNG");
    var f = ShowFile.Load(bytes);
    Eq("song-numb", f.Key);
    Eq("♪ Numb", f.Name);
    var song = SongShows.All.Single(x => x.Key == "song-numb");
    Eq(song.Data, f.Fast, "club-versie uit de afbeelding = ingebouwde versie");
    Eq(song.SafeData, f.Safe);

    // 2. Importeren vervangt de bestaande Numb-show; koppeling blijft werken
    var s = DefaultSettings();
    var numb = s.Shows.Single(x => x.PresetKey == "song-numb");
    var id = numb.Id;
    var trig = s.Triggers.Single(t => t.ShowId == id);
    numb.Steps.Clear();
    var count = s.Shows.Count;
    var imported = f.AddTo(s, replaceExisting: true);
    Eq(id, imported.Id, "zelfde show (Id) → gift-koppeling blijft");
    Eq(count, s.Shows.Count, "geen dubbele show");
    True(imported.Steps.Count > 10, "stappen terug");
    Eq(id, trig.ShowId);

    // 3. Eigen show exporteren als afbeelding en weer importeren als nieuwe show
    var mine = new LightShow { Name = "Mijn drop", Steps = new ObservableCollection<LightStep>
        { LightStep.Color("#FF0000", 300), LightStep.Off(250), LightStep.Color("#00E5FF", 900, 80, 3), LightStep.Color("#FFFFFF", 400, 100, 1, 200) } };
    var tinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    var png = ShowFile.EmbedInPng(tinyPng, ShowFile.FromShow(mine).ToJson());
    True(ShowFile.IsPng(png), "nog steeds een geldige PNG");
    var png2 = ShowFile.EmbedInPng(png, ShowFile.FromShow(mine).ToJson());
    Eq(png.Length, png2.Length, "opnieuw insluiten vervangt het oude blok");
    var back = ShowFile.Load(png).AddTo(s, replaceExisting: true);
    Eq("Mijn drop", back.Name);
    Eq(Presets.ToData(mine.Steps), Presets.ToData(back.Steps), "stappen exact gelijk (ook uit-stap, flitsen, overgang)");
    Eq(StepKind.Off, back.Steps[1].Kind);
    Eq(3, back.Steps[2].Flashes);

    // 4. Koppelen aan 1234 coins: gaat vóór het bredere bereik 999–1499
    ShowFile.LinkToCoins(s, back, 1234);
    var d = new RuleEngine().Evaluate(new StreamEvent { Type = EventTypes.Gift, RawType = "gift", User = "u", DedupeKey = "g1", GiftName = "x", GiftCount = 1, CoinsPerItem = 1234, TotalCoins = 1234 },
        s, _ => 0, checkDuplicate: false);
    Eq(DecisionKind.Play, d.Kind);
    Eq("Mijn drop", d.Show!.Name, "1234 coins speelt de geïmporteerde show");

    // 5. Kapotte bestanden geven een duidelijke fout
    var bad = 0;
    try { ShowFile.Load(tinyPng); } catch (FormatException) { bad++; }
    try { ShowFile.Load(System.Text.Encoding.UTF8.GetBytes("{\"format\":\"iets\"}")); } catch (FormatException) { bad++; }
    try { ShowFile.Load(System.Text.Encoding.UTF8.GetBytes("{\"format\":\"slc-show\",\"fast\":\"ZZ\"}")); } catch (FormatException) { bad++; }
    Eq(3, bad, "3× duidelijke fout");
});

Test("Helderheid lichtshows: alles naar verhouding lager (camera), standaard 60 %", async () =>
{
    Eq(60, new LightingOptions().ShowBrightness);
    var old = new AppSettings { Version = 14 }; old.Lighting.ShowBrightness = 100;
    SettingsStore.Migrate(old);
    Eq(60, old.Lighting.ShowBrightness, "update zet 60 %");
    using var lamp = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort);
    var opts = new LightingOptions { CommandDelayMs = 30, MinChangeMs = 40, ShowBrightness = 50 };
    var engine = new LightShowEngine(NewGovee(), () => opts);
    var show = new LightShow { Steps = new ObservableCollection<LightStep> { LightStep.Color("#FFFFFF", 200), LightStep.Color("#FF0000", 200, 50) } };
    await engine.PlayAsync(show, new[] { "127.0.0.1" }, CancellationToken.None);
    Eq("#808080,#400000", string.Join(",", lamp.ColorHistory()), "wit op 50 %, rood 50 % van 50 %");
    lamp.Reset(); engine.ForgetState();
    await engine.LiveColorAsync(new[] { "127.0.0.1" }, "#00E5FF", 100, "spotify");
    Eq("#007280", lamp.ColorHistory().Last(), "Spotify ook op 50 %");
});

TestSync("Opslag: versie 15 → 16 voegt alle dono-sounds toe, koppelt ze zoals in BetterTok en haalt sounds weg die er niet meer zijn", () =>
{
    var s = new AppSettings(); SettingsStore.EnsureDefaults(s, true);
    s.Version = 15;
    foreach (var key in new[] { "song-partyrock", "song-titanium", "song-cancan" })
        s.Shows.Remove(s.Shows.Single(x => x.PresetKey == key));                         // stond nog niet in versie 15
    var weg = new LightShow { Id = "preset-song-oud", Name = "♪ Oude sound", PresetKey = "song-oud" };
    s.Shows.Add(weg);
    s.Triggers.Add(new EventTrigger { Id = "tf-oud", Name = "♪ Oude sound", EventType = EventTypes.Gift, GiftNames = "Rose", ShowId = weg.Id });
    s.Triggers.Add(new EventTrigger { Name = "Mijn eigen regel", EventType = EventTypes.Gift, GiftNames = "Leeuw", ShowId = weg.Id });
    var t30000 = s.Triggers.Single(t => t.Id == "tf-30000"); t30000.ShowId = "preset-megagift"; t30000.Name = "Gift 30000+ coins";
    SettingsStore.Migrate(s); SettingsStore.EnsureDefaults(s, false);
    Eq(28, s.Version);
    Eq(49, s.Shows.Count(x => x.PresetKey.StartsWith("song-")), "49 song-shows");
    True(s.Shows.All(x => x.PresetKey != "song-oud"), "sound die niet meer bestaat is weg");
    True(s.Triggers.All(t => t.Id != "tf-oud"), "standaardkoppeling naar die sound is weg");
    Eq("", s.Triggers.Single(t => t.Name == "Mijn eigen regel").ShowId, "eigen regel blijft, zonder show");
    Eq("preset-song-titanium", s.Triggers.Single(t => t.Id == "tf-30000").ShowId, "30000 coins = Titanium");
    foreach (var song in SongShows.All)
        True(s.FindShow("preset-" + song.Key) != null, song.Key + " aanwezig");
    var warn = RuleEngine.ValidateGiftRules(s);
    True(warn.All(w => !w.Contains("overlappen") && !w.Contains("Gat")), "geen overlap of gaten: " + string.Join(" | ", warn));
    True(warn.Any(w => w.Contains("Mijn eigen regel")), "eigen regel zonder show wordt gemeld");
});

Test("Opstarten met instellingen van elke oudere versie (ook met ontbrekende onderdelen) geeft geen fout", async () =>
{
    foreach (var v in new[] { 4, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24 })
    foreach (var direct in new[] { false, true })
    foreach (var strip in new[] { "", "Spotify", "Updates", "Lighting", "Shows" })
    {
        var dir = TempDir();
        var s = new AppSettings(); SettingsStore.EnsureDefaults(s, true);
        s.Version = v; s.Lighting.DirectColor = direct;
        foreach (var sh in s.Shows.Where(x => x.PresetKey.StartsWith("song-"))) { sh.FastData = ""; sh.SafeData = ""; }
        var node = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(s, SettingsStore.JsonOptions))!.AsObject();
        if (strip != "") node[strip] = null;                       // onderdeel ontbreekt of is null
        File.WriteAllText(Path.Combine(dir, "settings.json"), node.ToJsonString());
        using var app = new AutomationController(dir, NewGovee(), new FakeNowPlaying(), () => new FakeAudio());
        try
        {
            app.Initialize();
            app.Settings.Lighting.MinChangeMs = 40; app.ApplyShowTier();
            app.Settings.Lighting.MinChangeMs = 550; app.ApplyShowTier();
            foreach (var show in app.Settings.Shows) { _ = Presets.ToData(show.Steps); _ = show.TotalDurationMs; _ = ShowFile.FromShow(show).ToJson(); }
            await app.SetModeAsync(AppMode.Spotify);
            await app.SetModeAsync(AppMode.Stream);
        }
        catch (Exception ex) { throw new Exception($"versie {v}, direct={direct}, zonder '{strip}': {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
    }
});

TestSync("Rave-herkenning: 160 BPM met vaste kick = rave, 100 BPM of stilte niet", () =>
{
    RaveDetector Run(double bpm, bool silent, int ms)
    {
        var d = new BeatDetector(); var t = new TempoTracker(); var r = new RaveDetector(); var per = 60000 / bpm;
        for (long now = 0; now < ms; now += 20)
        {
            var ph = now % per;
            var peak = silent ? 0 : (ph < 60 ? 0.95 : 0.22);
            var beat = d.Push(peak, now);
            if (beat) t.AddBeat(now);
            t.Update(now);
            r.Update(now, beat, t, d);
        }
        return r;
    }
    True(Run(160, false, 9000).IsRave, "uptempo 160 BPM herkend");
    True(Run(150, false, 9000).IsRave, "hardstyle 150 BPM herkend");
    Eq(false, Run(100, false, 9000).IsRave, "100 BPM is geen rave");
    Eq(false, Run(160, true, 9000).IsRave, "stilte is geen rave");
    Eq(false, Run(160, false, 3000).IsRave, "pas na een paar seconden zeker");
});

Test("Spotify + rave/zaag: festival-licht zoals de dono-sounds (flash op de kick, kleur op ¼ beat, strobe), ander nummer niet", async () =>
{
    using var lamp = new FakeGoveeDevice(ScanPort, ControlPort, ListenPort);
    var player = new FakeNowPlaying();
    var audio = new FakeAudio { Bpm = 160 };
    var s = DefaultSettings();
    s.Lighting.CommandDelayMs = 30; s.Lighting.MinChangeMs = 40; s.Lighting.DirectColor = true;
    s.Spotify.Rave = RaveMode.Auto;
    var engine = new LightShowEngine(NewGovee(), () => s.Lighting);
    using var sp = new SpotifyLightController(engine, () => new[] { "127.0.0.1" }, () => s, null, null, player, () => audio);
    player.Play("Rave", "Zaag");
    sp.Start();
    await WaitFor(() => sp.RaveActive, 9000, "rave herkend");
    await Task.Delay(1000);
    lamp.Reset();
    await Task.Delay(4500);
    var cmds = lamp.Commands.Where(c => c.cmd == "razer" && Convert.FromBase64String(c.data.GetProperty("pt").GetString()!)[3] == 0xB0).ToList();
    var cols = lamp.DirectColors.ToList();
    True(cols.Count(c => c == "#FFFFFF") >= 8, $"witte flashes op de kicks ({cols.Count(c => c == "#FFFFFF")})");
    True(cols.Distinct().Count() >= 3, "meerdere kleuren");
    // na elke witte flash komt ±¼ beat (94 ms) later een kleur
    var gaps = new List<double>();
    for (var i = 1; i < cmds.Count; i++)
    {
        var prev = Convert.FromBase64String(cmds[i - 1].data.GetProperty("pt").GetString()!);
        if (prev[6] == 0xFF && prev[7] == 0xFF && prev[8] == 0xFF) gaps.Add((cmds[i].at - cmds[i - 1].at).TotalMilliseconds);
    }
    var med = gaps.OrderBy(x => x).ElementAt(gaps.Count / 2);
    True(med is > 60 and < 140, $"kleur ±¼ beat na de flash (mediaan {med:0} ms)");
    // ander nummer, 100 BPM: geen rave
    audio.Bpm = 100;
    player.Play("Pop", "Rustig");
    await Task.Delay(7000);
    Eq(false, sp.RaveActive, "100 BPM: gewone Spotify-stijl");
    // instelling Nooit
    s.Spotify.Rave = RaveMode.Never; audio.Bpm = 160; player.Play("Rave", "Twee");
    await Task.Delay(7000);
    Eq(false, sp.RaveActive, "Rave-herkenning uit");
    await sp.StopAsync();
});

// ---------------------------------------------------------------- uitvoeren

var failed = 0;
var total = Stopwatch.StartNew();
foreach (var (name, run) in tests)
{
    var sw = Stopwatch.StartNew();
    try
    {
        var task = run();
        if (await Task.WhenAny(task, Task.Delay(30000)) != task) throw new Exception("Test duurde langer dan 30 s");
        await task;
        Console.WriteLine($"  ✔ {name}  ({sw.ElapsedMilliseconds} ms)");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"  ✘ {name}\n      {ex.Message}");
    }
}
Console.WriteLine($"\n{tests.Count - failed}/{tests.Count} tests geslaagd in {total.Elapsed.TotalSeconds:0.0} s");
return failed == 0 ? 0 : 1;
