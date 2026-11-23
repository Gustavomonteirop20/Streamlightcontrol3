using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace StreamLightControl2.Core.Models;

public enum BetterTokMode { Local, Cloud }
public enum FlashMode { PowerToggle, Black }
public enum RestoreMode { Previous, FixedColor, None }
public enum GiftStreakMode { AtEnd, EveryUpdate }
/// <summary>Stream = BetterTok-events → lampen. Spotify = het nummer dat speelt → lampen.</summary>
public enum AppMode { Stream, Spotify }
/// <summary>Beat = kleurwissel op de beat, Calm = rustige overgangen, PerSong = één kleur per nummer die meepulseert.</summary>
public enum SpotifyStyle { Beat, Calm, PerSong }
/// <summary>Rave/zaag herkennen in Spotify-modus: automatisch (op tempo + kick), altijd, of nooit.</summary>
public enum RaveMode { Auto, Always, Never }

public sealed class BetterTokOptions : ObservableObject
{
    private BetterTokMode _mode = BetterTokMode.Local;
    private string _localUrl = "ws://127.0.0.1:21213";
    private bool _scanPorts = true;
    private bool _autoConnect = true;
    private bool _autoReconnect = true;

    public BetterTokMode Mode { get => _mode; set => Set(ref _mode, value); }
    public string LocalUrl { get => _localUrl; set => Set(ref _localUrl, string.IsNullOrWhiteSpace(value) ? "ws://127.0.0.1:21213" : value.Trim()); }
    /// <summary>Probeer ook poort 21213–21222 (BetterTok schuift op als de poort bezet is).</summary>
    public bool ScanPorts { get => _scanPorts; set => Set(ref _scanPorts, value); }
    public bool AutoConnect { get => _autoConnect; set => Set(ref _autoConnect, value); }
    public bool AutoReconnect { get => _autoReconnect; set => Set(ref _autoReconnect, value); }
    /// <summary>API-key, versleuteld met Windows DPAPI (alleen leesbaar voor jouw Windows-account).</summary>
    public string ApiKeyProtected { get; set; } = "";
}

public sealed class GoveeDeviceConfig : ObservableObject
{
    private string _name = "Govee LED-strip";
    private string _ip = "";
    private string _sku = "";
    private string _mac = "";
    private bool _enabled = true;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, string.IsNullOrWhiteSpace(value) ? "Govee-apparaat" : value.Trim()); }
    public string Ip { get => _ip; set => Set(ref _ip, value?.Trim() ?? ""); }
    public string Sku { get => _sku; set => Set(ref _sku, value?.Trim() ?? ""); }
    public string Mac { get => _mac; set => Set(ref _mac, value?.Trim() ?? ""); }
    /// <summary>Meedoen met lichtshows en snelle acties.</summary>
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    // Zones / segmenten (uit de zones-test)
    private int _zoneCount = 20;
    /// <summary>Aantal zones dat de app stuurt (Direct-modus) of aantal segmenten (segment-methode).</summary>
    public int ZoneCount { get => _zoneCount; set => Set(ref _zoneCount, Math.Clamp(value, 1, 60)); }
    private string _zoneMethod = "direct";
    /// <summary>"direct" (realtime-protocol) of "segment" (onofficieel ptReal). Welke werkt, blijkt uit de test.</summary>
    public string ZoneMethod { get => _zoneMethod; set => Set(ref _zoneMethod, value == "segment" ? "segment" : "direct"); }
    private string _visibleZones = "";
    /// <summary>Zones die op stream te zien zijn, bijv. "1-3, 18-20" (leeg = nog niet ingesteld).</summary>
    public string VisibleZones { get => _visibleZones; set => Set(ref _visibleZones, value?.Trim() ?? ""); }

    /// <summary>"1-3, 18-20" → {1,2,3,18,19,20} (alleen geldige nummers tot ZoneCount).</summary>
    public static SortedSet<int> ParseZones(string text, int max)
    {
        var set = new SortedSet<int>();
        foreach (var part in (text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Split('-', '–');
            if (p.Length == 1 && int.TryParse(p[0], out var n)) { if (n >= 1 && n <= max) set.Add(n); }
            else if (p.Length == 2 && int.TryParse(p[0], out var a) && int.TryParse(p[1], out var b))
                for (var i = Math.Max(1, Math.Min(a, b)); i <= Math.Min(max, Math.Max(a, b)); i++) set.Add(i);
        }
        return set;
    }

    // Live status (niet opgeslagen)
    private bool? _online;
    private DeviceStatus? _status;
    private DateTime? _lastSeen;
    [JsonIgnore] public bool? Online { get => _online; set { if (Set(ref _online, value)) OnPropertyChanged(nameof(OnlineLabel)); } }
    [JsonIgnore] public DeviceStatus? Status { get => _status; set => Set(ref _status, value); }
    [JsonIgnore] public DateTime? LastSeen { get => _lastSeen; set => Set(ref _lastSeen, value); }
    [JsonIgnore] public string OnlineLabel => Online switch
    {
        true => "LAN Control actief",
        false => "Geen antwoord",
        null => "Nog niet getest"
    };
}

public sealed record DeviceStatus(bool On, int Brightness, byte R, byte G, byte B)
{
    public string ColorHex => ColorUtil.ToHex(R, G, B);
}

public sealed class LightingOptions : ObservableObject
{
    private FlashMode _flashMode = FlashMode.Black;
    private int _commandDelayMs = 80;
    private RestoreMode _restoreMode = RestoreMode.Previous;
    private string _restoreColor = "#FFFFFF";
    private int _restoreBrightness = 60;
    private bool _statusPolling = true;
    private bool _segmentEffects = false;
    private int _minChangeMs = 550;
    private int _latencyMs = 0;
    private int _segmentCount = 15;
    private bool _directColor;

    /// <summary>
    /// Kleuren direct laten springen via het realtime-protocol (Razer/DreamView), zonder de fade die de strip
    /// zelf bij het standaardcommando doet. Werkt op strips die Govee-schermsync/Razer Chroma ondersteunen.
    /// </summary>
    public bool DirectColor { get => _directColor; set { if (Set(ref _directColor, value)) OnPropertyChanged(nameof(EffectiveCommandDelayMs)); } }

    /// <summary>Pauze na een commando zoals de engine hem gebruikt: het realtime-protocol is gemaakt voor ±30 beelden per seconde.</summary>
    [JsonIgnore] public int EffectiveCommandDelayMs => DirectColor ? Math.Min(CommandDelayMs, 25) : CommandDelayMs;

    private bool _limitStrobes;
    private int _showBrightness = 60;
    /// <summary>Helderheid van alle lichtshows en de Spotify-modus (%). Lager = rustiger voor de streamcamera.</summary>
    public int ShowBrightness { get => _showBrightness; set => Set(ref _showBrightness, Math.Clamp(value, 10, 100)); }
    /// <summary>Strobes beperken tot 3 flitsen per seconde (snelle strobes kunnen bij gevoelige kijkers klachten geven).</summary>
    public bool LimitStrobes { get => _limitStrobes; set => Set(ref _limitStrobes, value); }

    /// <summary>Hoe het 'uit'-deel van een flits wordt gemaakt.</summary>
    public FlashMode FlashMode { get => _flashMode; set => Set(ref _flashMode, value); }
    /// <summary>Pauze na elk UDP-commando. Te laag = lamp mist commando's.</summary>
    public int CommandDelayMs { get => _commandDelayMs; set => Set(ref _commandDelayMs, Math.Clamp(value, 30, 300)); }
    public RestoreMode RestoreMode { get => _restoreMode; set => Set(ref _restoreMode, value); }
    public string RestoreColor { get => _restoreColor; set => Set(ref _restoreColor, ColorUtil.Normalize(value, "#FFFFFF")); }
    public int RestoreBrightness { get => _restoreBrightness; set => Set(ref _restoreBrightness, Math.Clamp(value, 1, 100)); }
    /// <summary>Elke 30 s de status van je lampen opvragen (devStatus).</summary>
    public bool StatusPolling { get => _statusPolling; set => Set(ref _statusPolling, value); }
    /// <summary>Snelste kleurwissel die de strip aankan. Snellere wissels in een show worden overgeslagen.</summary>
    public int MinChangeMs { get => _minChangeMs; set => Set(ref _minChangeMs, Math.Clamp(value, 40, 1500)); }
    /// <summary>Licht zoveel ms eerder sturen dan het geluid, om vertraging van wifi en strip op te vangen.</summary>
    public int LatencyMs { get => _latencyMs; set => Set(ref _latencyMs, Math.Clamp(value, 0, 600)); }
    /// <summary>Helft/helft op één strip via segmentbesturing (ptReal). Bij 2+ lampen krijgt elke lamp een helft.</summary>
    public bool SegmentEffects { get => _segmentEffects; set => Set(ref _segmentEffects, value); }
    /// <summary>Aantal segmenten van de strip (H619E: meestal 15).</summary>
    public int SegmentCount { get => _segmentCount; set => Set(ref _segmentCount, Math.Clamp(value, 2, 30)); }
    private bool _zoneEffects = true;
    /// <summary>Festival-effecten op de stukjes strip die op stream te zien zijn (Device Manager › Zones op stream).</summary>
    public bool ZoneEffects { get => _zoneEffects; set => Set(ref _zoneEffects, value); }
    private bool _calmShows;   // nieuwe installaties en versie 30 zetten dit aan
    /// <summary>Rustige shows: per sectie één kleur, geen witte flitsen, hooguit ±1,5 wissels per seconde;
    /// de zichtbare zones bewegen op de beat (zoals in het voorbeeldfilmpje).</summary>
    public bool CalmShows { get => _calmShows; set => Set(ref _calmShows, value); }
    private bool _calmFlashes = true;
    /// <summary>Festival-flitsen in de rustige shows: korte witte flits op de drop en op kicks in de drop,
    /// altijd minder dan 3 flitsen per seconde.</summary>
    public bool CalmFlashes { get => _calmFlashes; set => Set(ref _calmFlashes, value); }
}

public sealed class FollowOptions : ObservableObject
{
    private bool _oncePerUser = true;
    private int _groupWindowSeconds = 3;

    /// <summary>Iemand die opnieuw volgt (unfollow/follow) triggert in deze sessie geen tweede effect.</summary>
    public bool OncePerUser { get => _oncePerUser; set => Set(ref _oncePerUser, value); }
    /// <summary>Follows die binnen zoveel seconden na elkaar komen worden gebundeld tot één effect (0 = uit).</summary>
    public int GroupWindowSeconds { get => _groupWindowSeconds; set => Set(ref _groupWindowSeconds, Math.Clamp(value, 0, 120)); }
}

public sealed class GiftOptions : ObservableObject
{
    private GiftStreakMode _streakMode = GiftStreakMode.AtEnd;
    private int _cloudComboMergeSeconds = 20;

    public GiftStreakMode StreakMode { get => _streakMode; set => Set(ref _streakMode, value); }
    /// <summary>Cloud Mode kan na een combo nog een 'restant'-melding sturen. Die wordt binnen dit venster genegeerd.</summary>
    public int CloudComboMergeSeconds { get => _cloudComboMergeSeconds; set => Set(ref _cloudComboMergeSeconds, Math.Clamp(value, 0, 120)); }
}

public sealed class QueueOptions : ObservableObject
{
    private int _maxLength = 50;
    private int _gapMs = 250;
    private bool _inOrder = true;
    /// <summary>Shows spelen precies in de volgorde waarin ze binnenkomen en onderbreken elkaar nooit
    /// (net als de sounds in BetterTok/DonateWave). Uit = hogere prioriteit eerst.</summary>
    public bool InOrder { get => _inOrder; set => Set(ref _inOrder, value); }
    public int MaxLength { get => _maxLength; set => Set(ref _maxLength, Math.Clamp(value, 1, 200)); }
    /// <summary>Pauze tussen twee shows.</summary>
    public int GapMs { get => _gapMs; set => Set(ref _gapMs, Math.Clamp(value, 0, 5000)); }
}

public sealed class GeneralOptions : ObservableObject
{
    private bool _startAutomationOnLaunch = true;
    private bool _safeMode;
    private bool _streamModeTopmost = true;
    private AppMode _mode = AppMode.Stream;

    /// <summary>Welke bron de lampen aanstuurt. Wordt onthouden na herstart.</summary>
    public AppMode Mode { get => _mode; set => Set(ref _mode, value); }

    public bool StartAutomationOnLaunch { get => _startAutomationOnLaunch; set => Set(ref _startAutomationOnLaunch, value); }
    /// <summary>Blijft bewaard na herstart, zodat de app nooit onverwacht effecten afspeelt.</summary>
    public bool SafeMode { get => _safeMode; set => Set(ref _safeMode, value); }
    public bool StreamModeTopmost { get => _streamModeTopmost; set => Set(ref _streamModeTopmost, value); }
    private string _theme = "standaard";
    /// <summary>Thema van de app: standaard, kerst, herfst, zomer of auto (per seizoen).</summary>
    public string Theme { get => _theme; set => Set(ref _theme, string.IsNullOrWhiteSpace(value) ? "standaard" : value.Trim().ToLowerInvariant()); }
}

public sealed class SpotifyOptions : ObservableObject
{
    private SpotifyStyle _style = SpotifyStyle.Beat;
    private int _maxBrightness = 100;
    private int _sensitivity = 50;
    private bool _dimWhenPaused = true;
    private int _calmSeconds = 4;
    private bool _flashbangs = true;
    private RaveMode _rave = RaveMode.Auto;

    /// <summary>Rave/uptempo/zaag herkend → festival-licht zoals bij de dono-sounds.</summary>
    public RaveMode Rave { get => _rave; set => Set(ref _rave, value); }
    private bool _blackouts = true;

    /// <summary>Witte flitsen: elke 8e wissel in harde stukken, en wit-zwart-wit op de drop.</summary>
    public bool Flashbangs { get => _flashbangs; set => Set(ref _flashbangs, value); }
    /// <summary>Korte zwarte stukjes tussen de kleuren in harde stukken.</summary>
    public bool Blackouts { get => _blackouts; set => Set(ref _blackouts, value); }

    public SpotifyStyle Style { get => _style; set => Set(ref _style, value); }
    /// <summary>Hoogste helderheid in Spotify-modus (%).</summary>
    public int MaxBrightness { get => _maxBrightness; set => Set(ref _maxBrightness, Math.Clamp(value, 10, 100)); }
    /// <summary>Hoe snel een klap als beat telt (1 = alleen harde kicks, 100 = bijna alles).</summary>
    public int Sensitivity { get => _sensitivity; set => Set(ref _sensitivity, Math.Clamp(value, 1, 100)); }
    /// <summary>Bij pauze de strip zacht laten branden in plaats van de laatste kleur.</summary>
    public bool DimWhenPaused { get => _dimWhenPaused; set => Set(ref _dimWhenPaused, value); }
    /// <summary>Stijl ‘Rustig’: seconden per kleur.</summary>
    public int CalmSeconds { get => _calmSeconds; set => Set(ref _calmSeconds, Math.Clamp(value, 2, 20)); }
}

public sealed class UpdateOptions : ObservableObject
{
    private string _repo = StreamLightControl2.Core.Services.UpdateService.DefaultRepo;
    private bool _autoCheck = true;
    private bool _autoInstallOnStart = true;

    /// <summary>GitHub-repository met de releases, bijv. "naam/StreamLightControl".</summary>
    public string Repo { get => _repo; set => Set(ref _repo, (value ?? "").Trim().Trim('/')); }
    /// <summary>Bij opstarten en elke 6 uur kijken of er een nieuwe versie is.</summary>
    public bool AutoCheck { get => _autoCheck; set => Set(ref _autoCheck, value); }
    /// <summary>Gevonden update bij het opstarten meteen installeren (tijdens het streamen nooit automatisch).</summary>
    public bool AutoInstallOnStart { get => _autoInstallOnStart; set => Set(ref _autoInstallOnStart, value); }
}

public sealed class DashboardOptions
{
    public static readonly string[] AllStats = { "follows", "gifts", "coins", "shows", "likes", "shares", "comments", "members" };
    public List<string> VisibleStats { get; set; } = new() { "follows", "gifts", "coins", "shows" };
}

/// <summary>DonateWave-koppeling (Gebeurtenissen-API, https://www.donatewave.nl/developers).</summary>
public sealed class DonateWaveOptions : ObservableObject
{
    private bool _enabled;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    /// <summary>Sleutel (dw_evt_…), versleuteld met Windows DPAPI.</summary>
    public string ApiKeyProtected { get; set; } = "";
    /// <summary>next_cursor van de laatst volledig verwerkte gebeurtenissen.</summary>
    public string Cursor { get; set; } = "";
    private int _overlayDelayMs = 0;
    /// <summary>
    /// Eigen correctie op de gemeten overlaytijden (ms). 0 = precies zoals gemeten op 12-11-2026.
    /// Hoger = licht later, lager (min −2000) = licht eerder. Geldt voor alle DonateWave-tips.
    /// </summary>
    public int OverlayDelayMs { get => _overlayDelayMs; set => Set(ref _overlayDelayMs, Math.Clamp(value, -2000, 5000)); }
    private int _alertGapMs = 3240;
    /// <summary>Van het einde van de sound tot de alert klaar is: voorleesstem (naam + bedrag) inbegrepen.
    /// Gemeten: 3240 ms bij "TestViewer". Pas daarna begint de volgende alert.</summary>
    public int AlertGapMs { get => _alertGapMs; set => Set(ref _alertGapMs, Math.Clamp(value, 0, 10000)); }
    private int _betweenAnimationsMs = 1735;
    /// <summary>Van het einde van een animatie tot de volgende animatie in beeld is (2+ shout-outs tegelijk). Gemeten: 1735 ms.</summary>
    public int BetweenAnimationsMs { get => _betweenAnimationsMs; set => Set(ref _betweenAnimationsMs, Math.Clamp(value, 0, 10000)); }
}

/// <summary>Eigen kerst-overlays (lokale bron voor OBS / TikTok LIVE Studio) met live DonateWave-gegevens.</summary>
public sealed class OverlayOptions : ObservableObject
{
    private bool _enabled = true;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    private int _port = 7531;
    public int Port { get => _port; set => Set(ref _port, Math.Clamp(value, 1024, 65535)); }
    private string _goalTitle = "Kerstdoel";
    public string GoalTitle { get => _goalTitle; set => Set(ref _goalTitle, value ?? ""); }
    private long _goalCents = 10000;
    public long GoalCents { get => _goalCents; set { if (Set(ref _goalCents, Math.Clamp(value, 100, 100_000_000))) OnPropertyChanged(nameof(GoalEuro)); } }
    [System.Text.Json.Serialization.JsonIgnore] public string GoalEuro
    {
        get => (GoalCents / 100m).ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("nl-NL"));
        set { if (decimal.TryParse((value ?? "").Replace("€", "").Trim().Replace(".", "").Replace(",", "."), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v)) GoalCents = (long)Math.Round(v * 100); OnPropertyChanged(); }
    }
    /// <summary>Vanaf dit moment telt het tipdoel (Doel terugzetten).</summary>
    public DateTime GoalStartUtc { get; set; } = DateTime.MinValue;
    private bool _countTests;
    /// <summary>Testtips meetellen in doel, ranking en recente tips (handig om te testen).</summary>
    public bool CountTests { get => _countTests; set => Set(ref _countTests, value); }
    private int _alertMs = 7000;
    /// <summary>Hoe lang de kerst-tipalert in beeld blijft.</summary>
    public int AlertMs { get => _alertMs; set => Set(ref _alertMs, Math.Clamp(value, 2000, 60000)); }
    /// <summary>Vanaf dit moment tellen de TikTok-overlays (knop "Nieuwe stream").</summary>
    public DateTime TikTokStartUtc { get; set; } = DateTime.MinValue;
    private int _tikTokAlertMs = 5000;
    /// <summary>Hoe lang de kerst-giftalert (TikTok) in beeld blijft.</summary>
    public int TikTokAlertMs { get => _tikTokAlertMs; set => Set(ref _tikTokAlertMs, Math.Clamp(value, 2000, 30000)); }
}

public sealed class AppSettings
{
    public int Version { get; set; } = 33;
    public BetterTokOptions BetterTok { get; set; } = new();
    public ObservableCollection<GoveeDeviceConfig> Devices { get; set; } = new();
    public ObservableCollection<LightShow> Shows { get; set; } = new();
    public ObservableCollection<EventTrigger> Triggers { get; set; } = new();
    public FollowOptions Follow { get; set; } = new();
    public GiftOptions Gift { get; set; } = new();
    public QueueOptions Queue { get; set; } = new();
    public LightingOptions Lighting { get; set; } = new();
    public GeneralOptions General { get; set; } = new();
    public DashboardOptions Dashboard { get; set; } = new();
    public UpdateOptions Updates { get; set; } = new();
    public SpotifyOptions Spotify { get; set; } = new();
    public DonateWaveOptions DonateWave { get; set; } = new();
    public OverlayOptions Overlays { get; set; } = new();

    // ---- Velden uit versie 2.0 (alleen gelezen voor migratie, daarna leeg) ----
    [JsonPropertyName("BetterTokUrl")] public string? LegacyBetterTokUrl { get; set; }
    [JsonPropertyName("GoveeIp")] public string? LegacyGoveeIp { get; set; }
    [JsonPropertyName("GoveeSku")] public string? LegacyGoveeSku { get; set; }
    [JsonPropertyName("GoveeName")] public string? LegacyGoveeName { get; set; }
    [JsonPropertyName("Rules")] public List<LegacyEffectRule>? LegacyRules { get; set; }

    public LightShow? FindShow(string? id) => string.IsNullOrEmpty(id) ? null : Shows.FirstOrDefault(s => s.Id == id);
}

/// <summary>Het regelformaat van Stream Light Control 2.0.</summary>
public sealed class LegacyEffectRule
{
    public string EventName { get; set; } = "gift";
    public string MatchText { get; set; } = "";
    public string DisplayName { get; set; } = "Regel";
    public string ColorHex { get; set; } = "#9B6BFF";
    public int Brightness { get; set; } = 80;
    public int Flashes { get; set; } = 1;
    public int FlashDurationMs { get; set; } = 500;
    public bool Enabled { get; set; } = true;
}
