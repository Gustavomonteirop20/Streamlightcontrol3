using System.Text.Json.Serialization;

namespace StreamLightControl2.Core.Models;

/// <summary>Genormaliseerde eventtypes (Local- en Cloud-namen van BetterTok worden hierop gemapt).</summary>
public static class EventTypes
{
    public const string Follow = "follow";
    public const string Gift = "gift";
    public const string Like = "like";
    public const string Share = "share";
    public const string Comment = "comment";
    public const string Member = "member";
    public const string Subscribe = "subscribe";
    public const string Tip = "tip";
    public const string Other = "other";

    public static readonly string[] Triggerable = { Follow, Gift, Like, Share, Comment, Member, Subscribe, Tip };

    public static string Label(string type) => type switch
    {
        Follow => "Follow",
        Gift => "Gift",
        Like => "Like",
        Share => "Share",
        Comment => "Comment",
        Member => "Kijker komt binnen",
        Subscribe => "Abonnement",
        Tip => "DonateWave-tip",
        _ => "Overig"
    };
}

/// <summary>Soorten DonateWave-tips (veld data.special / data.lucky_wheel).</summary>
public static class TipKinds
{
    public const string Any = "", Shoutout = "shoutout", Wheel = "wheel";
    public static string Label(string k) => k switch { Shoutout => "Alleen shout-outs", Wheel => "Alleen Lucky Wheel", _ => "Elke tip" };
}

public static class Priorities
{
    public const int Low = 1, Normal = 2, High = 3, VeryHigh = 4, Critical = 5, Manual = 9;

    public static string Label(int p) => p switch
    {
        <= 1 => "Laag",
        2 => "Normaal",
        3 => "Hoog",
        4 => "Zeer hoog",
        5 => "Kritiek",
        _ => "Handmatig"
    };
}

/// <summary>
/// Koppelt één BetterTok-eventtype (+ optionele voorwaarden) aan één lichtshow.
/// Event Manager, Gift Manager en Follow Manager bewerken allemaal deze ene lijst.
/// </summary>
public sealed class EventTrigger : ObservableObject
{
    private string _name = "Nieuwe koppeling";
    private string _eventType = EventTypes.Follow;
    private string _showId = "";
    private int _priority = Priorities.Normal;
    private bool _enabled = true;
    private string _giftNames = "";
    private int? _minCoins;
    private int? _maxCoins;
    private string _matchText = "";
    private int _likeThreshold;
    private int _cooldownSeconds;
    private int _maxInQueue;
    private bool _interruptCurrent;
    private string _tipKind = "";
    private int _startDelayMs;
    private int _soundDurationMs;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, string.IsNullOrWhiteSpace(value) ? "Koppeling" : value.Trim()); }
    public string EventType { get => _eventType; set { if (Set(ref _eventType, value ?? EventTypes.Follow)) { OnPropertyChanged(nameof(EventLabel)); OnPropertyChanged(nameof(ConditionSummary)); } } }
    public string ShowId { get => _showId; set => Set(ref _showId, value ?? ""); }
    public int Priority { get => _priority; set { if (Set(ref _priority, Math.Clamp(value, 1, 5))) OnPropertyChanged(nameof(PriorityLabel)); } }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    /// <summary>Komma-gescheiden giftnamen. Leeg = elke gift (dan gelden de coin-grenzen).</summary>
    public string GiftNames { get => _giftNames; set { if (Set(ref _giftNames, value?.Trim() ?? "")) OnPropertyChanged(nameof(ConditionSummary)); } }

    /// <summary>Minimaal aantal coins (inclusief). Null = geen ondergrens.</summary>
    public int? MinCoins { get => _minCoins; set { if (Set(ref _minCoins, value is < 0 ? 0 : value)) OnPropertyChanged(nameof(ConditionSummary)); } }

    /// <summary>Maximaal aantal coins (inclusief). Null = geen bovengrens.</summary>
    public int? MaxCoins { get => _maxCoins; set { if (Set(ref _maxCoins, value is < 0 ? 0 : value)) OnPropertyChanged(nameof(ConditionSummary)); } }

    /// <summary>Voor comments: alleen als de tekst dit woord bevat (bijv. !rainbow).</summary>
    public string MatchText { get => _matchText; set { if (Set(ref _matchText, value?.Trim() ?? "")) OnPropertyChanged(nameof(ConditionSummary)); } }

    /// <summary>Voor likes: pas afspelen na zoveel likes (0 = bij elk like-bericht).</summary>
    public int LikeThreshold { get => _likeThreshold; set { if (Set(ref _likeThreshold, Math.Max(0, value))) OnPropertyChanged(nameof(ConditionSummary)); } }

    public int CooldownSeconds { get => _cooldownSeconds; set => Set(ref _cooldownSeconds, Math.Clamp(value, 0, 3600)); }

    /// <summary>Maximaal zoveel effecten van deze koppeling tegelijk in de wachtrij (0 = onbeperkt).</summary>
    public int MaxInQueue { get => _maxInQueue; set => Set(ref _maxInQueue, Math.Clamp(value, 0, 100)); }

    /// <summary>DonateWave: "" = elke tip, "shoutout" = alleen shout-outs, "wheel" = alleen Lucky Wheel.</summary>
    public string TipKind { get => _tipKind; set { if (Set(ref _tipKind, value ?? "")) OnPropertyChanged(nameof(ConditionSummary)); } }

    /// <summary>Show pas zoveel ms na het event laten beginnen (bijv. na de animatie van een DonateWave shout-out,
    /// waarna de extra sound speelt). Bij DonateWave telt de tijd vanaf het moment van de tip zelf.</summary>
    public int StartDelayMs { get => _startDelayMs; set { if (Set(ref _startDelayMs, Math.Clamp(value, 0, 120000))) OnPropertyChanged(nameof(StartDelaySeconds)); } }
    [JsonIgnore] public string StartDelaySeconds
    {
        get => (StartDelayMs / 1000.0).ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("nl-NL"));
        set { if (double.TryParse((value ?? "").Replace(",", "."), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) StartDelayMs = (int)Math.Round(v * 1000); OnPropertyChanged(); }
    }

    /// <summary>Hoe lang de sound bij dit event duurt (bijv. de follow-sound in BetterTok). De volgende show in de
    /// wachtrij begint pas als die sound klaar is, ook als de lichtshow zelf korter is. 0 = alleen de show.</summary>
    public int SoundDurationMs { get => _soundDurationMs; set { if (Set(ref _soundDurationMs, Math.Clamp(value, 0, 600000))) OnPropertyChanged(nameof(SoundDurationSeconds)); } }
    [JsonIgnore] public string SoundDurationSeconds
    {
        get => (SoundDurationMs / 1000.0).ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("nl-NL"));
        set { if (double.TryParse((value ?? "").Replace(",", "."), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) SoundDurationMs = (int)Math.Round(v * 1000); OnPropertyChanged(); }
    }

    /// <summary>Breekt een lopende show met lagere prioriteit af.</summary>
    public bool InterruptCurrent { get => _interruptCurrent; set => Set(ref _interruptCurrent, value); }

    private bool _oncePerLive;
    /// <summary>Binnenkomst: deze koppeling speelt maar één keer per live (ook als de kijker opnieuw binnenkomt).</summary>
    public bool OncePerLive { get => _oncePerLive; set => Set(ref _oncePerLive, value); }

    [JsonIgnore] public string EventLabel => EventTypes.Label(EventType);
    [JsonIgnore] public string PriorityLabel => Priorities.Label(Priority);

    [JsonIgnore]
    public IReadOnlyList<string> GiftNameList => GiftNames
        .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    [JsonIgnore]
    public string ConditionSummary => EventType switch
    {
        EventTypes.Gift when GiftNameList.Count > 0 => "Gift: " + string.Join(", ", GiftNameList),
        EventTypes.Gift => CoinRangeLabel,
        EventTypes.Tip when TipKind == TipKinds.Shoutout => "Shout-out" + (MinCoins != null || MaxCoins != null ? " · " + EuroRangeLabel : ""),
        EventTypes.Tip when TipKind == TipKinds.Wheel => "Lucky Wheel" + (MinCoins != null || MaxCoins != null ? " · " + EuroRangeLabel : ""),
        EventTypes.Tip => EuroRangeLabel,
        EventTypes.Comment when MatchText.Length > 0 => $"Bevat \"{MatchText}\"",
        EventTypes.Member when MatchText.Length > 0 => $"Alleen {MatchText}",
        EventTypes.Like when LikeThreshold > 0 => $"Per {LikeThreshold} likes",
        _ => "Altijd"
    };

    [JsonIgnore]
    public string CoinRangeLabel => (MinCoins, MaxCoins) switch
    {
        (null, null) => "Elke gift",
        ({ } a, null) => $"{a:N0}+ coins",
        (null, { } b) => $"t/m {b:N0} coins",
        ({ } a, { } b) => $"{a:N0} – {b:N0} coins"
    };

    /// <summary>DonateWave-tips: MinCoins/MaxCoins bevatten dan eurocenten.</summary>
    [JsonIgnore]
    public string EuroRangeLabel => (MinCoins, MaxCoins) switch
    {
        (null, null) => "Elke tip",
        ({ } a, null) => $"{Euro(a)} en hoger",
        (null, { } b) => $"t/m {Euro(b)}",
        ({ } a, { } b) => $"{Euro(a)} – {Euro(b)}"
    };

    public static string Euro(long cents) => "€ " + (cents / 100m).ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("nl-NL"));

    /// <summary>Bedrag in euro's voor het invulveld (tips), bijv. "9,99". Leeg = geen grens.</summary>
    [JsonIgnore] public string MinEuro { get => ToEuroText(MinCoins); set { MinCoins = ParseEuro(value); OnPropertyChanged(); } }
    [JsonIgnore] public string MaxEuro { get => ToEuroText(MaxCoins); set { MaxCoins = ParseEuro(value); OnPropertyChanged(); } }
    private static string ToEuroText(int? cents) => cents is { } c ? (c / 100m).ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("nl-NL")) : "";
    public static int? ParseEuro(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Replace("€", "").Trim().Replace(",", ".");
        return decimal.TryParse(t, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0
            ? (int)Math.Round(v * 100m) : null;
    }

    public bool CoinsMatch(long coins) =>
        (MinCoins is null || coins >= MinCoins) && (MaxCoins is null || coins <= MaxCoins);

    public EventTrigger Clone() => new()
    {
        Id = Id, Name = Name, EventType = EventType, ShowId = ShowId, Priority = Priority, Enabled = Enabled,
        GiftNames = GiftNames, MinCoins = MinCoins, MaxCoins = MaxCoins, MatchText = MatchText,
        LikeThreshold = LikeThreshold, CooldownSeconds = CooldownSeconds, MaxInQueue = MaxInQueue,
        InterruptCurrent = InterruptCurrent, TipKind = TipKind, StartDelayMs = StartDelayMs, SoundDurationMs = SoundDurationMs,
        OncePerLive = OncePerLive
    };
}
