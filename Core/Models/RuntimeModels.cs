using System.Text.Json.Serialization;

namespace StreamLightControl2.Core.Models;

public enum ProcessingStatus
{
    Received,       // Ontvangen, nog niet verwerkt
    Queued,         // In de wachtrij
    Playing,        // Wordt afgespeeld
    Played,         // Afgespeeld
    Skipped,        // Overgeslagen / verwijderd uit wachtrij
    Interrupted,    // Afgebroken door een belangrijker effect
    NoRule,         // Geen koppeling voor dit event
    Ignored,        // Bewust genegeerd (cooldown, dubbel, combo, safe mode …)
    Failed          // Fout bij afspelen
}

/// <summary>Eén regel in de Event History.</summary>
public sealed class HistoryEntry : ObservableObject
{
    private ProcessingStatus _status;
    private string _showName = "";
    private string _detail = "";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Time { get; set; } = DateTime.Now;
    public string User { get; set; } = "";
    public string Type { get; set; } = EventTypes.Other;
    public string RawType { get; set; } = "";
    public string GiftName { get; set; } = "";
    public int GiftCount { get; set; }
    public long? Coins { get; set; }
    public bool IsTest { get; set; }
    public string ShowName { get => _showName; set { if (Set(ref _showName, value ?? "")) OnPropertyChanged(nameof(Description)); } }
    public ProcessingStatus Status { get => _status; set { if (Set(ref _status, value)) { OnPropertyChanged(nameof(StatusLabel)); OnPropertyChanged(nameof(IsError)); } } }
    public string Detail { get => _detail; set => Set(ref _detail, value ?? ""); }

    [JsonIgnore] public string TypeLabel => EventTypes.Label(Type);
    /// <summary>Korte omschrijving, bijv. "Gift · Rose ×5 → Kleine gift".</summary>
    [JsonIgnore] public string Description =>
        (IsTest ? "Test · " : "") + TypeLabel +
        (string.IsNullOrEmpty(GiftName) ? "" : $" · {GiftName}{(GiftCount > 1 ? $" ×{GiftCount}" : "")}") +
        (Coins is > 0 ? $" · {Coins:N0} coins" : "") +
        (string.IsNullOrEmpty(ShowName) ? "" : $"  →  {ShowName}");
    [JsonIgnore] public bool IsError => Status == ProcessingStatus.Failed;
    [JsonIgnore] public string StatusLabel => Status switch
    {
        ProcessingStatus.Received => "Ontvangen",
        ProcessingStatus.Queued => "In wachtrij",
        ProcessingStatus.Playing => "Speelt af",
        ProcessingStatus.Played => "Afgespeeld",
        ProcessingStatus.Skipped => "Overgeslagen",
        ProcessingStatus.Interrupted => "Onderbroken",
        ProcessingStatus.NoRule => "Geen koppeling",
        ProcessingStatus.Ignored => "Genegeerd",
        ProcessingStatus.Failed => "Fout",
        _ => Status.ToString()
    };
}

public enum ErrorCategory { BetterTok, Govee, Event, Queue, App, Spotify, DonateWave }

/// <summary>Een melding in het Error Center, met uitleg en oplossing.</summary>
public sealed class ErrorEntry : ObservableObject
{
    private int _count = 1;
    private DateTime _lastSeen = DateTime.Now;
    private string _detail = "";

    public string Key { get; init; } = "";
    public ErrorCategory Category { get; init; }
    public string Title { get; init; } = "";
    public string Explanation { get; init; } = "";
    public string Solution { get; init; } = "";
    public bool IsWarning { get; init; }
    public DateTime FirstSeen { get; init; } = DateTime.Now;
    public DateTime LastSeen { get => _lastSeen; set => Set(ref _lastSeen, value); }
    public int Count { get => _count; set => Set(ref _count, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value ?? ""); }

    public string CategoryLabel => Category switch
    {
        ErrorCategory.BetterTok => "BetterTok",
        ErrorCategory.Govee => "Govee",
        ErrorCategory.Event => "Event",
        ErrorCategory.Queue => "Wachtrij",
        ErrorCategory.Spotify => "Spotify",
        ErrorCategory.DonateWave => "DonateWave",
        _ => "App"
    };
}

public enum QueueSource { Event, Test, Manual }

/// <summary>Een effect in de wachtrij. Bevat een kopie van de show zodat bewerken tijdens afspelen veilig is.</summary>
public sealed class QueueItem : ObservableObject
{
    private int _priority;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public LightShow Show { get; init; } = new();
    public string ShowName => Show.Name;
    public string User { get; init; } = "";
    public string EventType { get; init; } = "";
    public string TriggerId { get; init; } = "";
    public QueueSource Source { get; init; }
    public bool Interrupt { get; init; }
    public DateTime EnqueuedAt { get; } = DateTime.Now;
    public HistoryEntry? History { get; init; }
    public long Sequence { get; set; }
    /// <summary>Moment (UTC) waarop de sound begint; de show wacht hierop of begint er midden in als hij te laat is.</summary>
    public DateTime? StartAtUtc { get; set; }
    /// <summary>Hoe lang er maximaal op StartAtUtc gewacht mag worden (beschermt tegen een verkeerd ingestelde pc-klok).</summary>
    public int MaxWaitMs { get; set; }
    /// <summary>Na de show de wachtrij nog vasthouden tot zoveel ms na de start (de sound van het event speelt nog).</summary>
    public int HoldMs { get; set; }
    /// <summary>Wanneer het wachten op StartAtUtc begon (voor MaxWaitMs).</summary>
    public DateTime? WaitStartedUtc { get; set; }
    public int Priority { get => _priority; set { if (Set(ref _priority, value)) OnPropertyChanged(nameof(PriorityLabel)); } }
    public string PriorityLabel => Priorities.Label(Priority);
    public string EventLabel => Source == QueueSource.Manual ? "Handmatig" : Source == QueueSource.Test ? "Test · " + EventTypes.Label(EventType) : EventTypes.Label(EventType);
}

/// <summary>Teller per sessie. Begint altijd op 0 – geen voorbeeldcijfers.</summary>
public sealed class SessionStats : ObservableObject
{
    private int _follows, _gifts, _shows, _likes, _shares, _comments, _members, _events;
    private long _coins;
    public int Follows { get => _follows; set => Set(ref _follows, value); }
    public int Gifts { get => _gifts; set => Set(ref _gifts, value); }
    public long Coins { get => _coins; set => Set(ref _coins, value); }
    public int ShowsPlayed { get => _shows; set => Set(ref _shows, value); }
    public int Likes { get => _likes; set => Set(ref _likes, value); }
    public int Shares { get => _shares; set => Set(ref _shares, value); }
    public int Comments { get => _comments; set => Set(ref _comments, value); }
    public int Members { get => _members; set => Set(ref _members, value); }
    public int Events { get => _events; set => Set(ref _events, value); }
    public DateTime Since { get; private set; } = DateTime.Now;

    public void Reset()
    {
        Follows = Gifts = ShowsPlayed = Likes = Shares = Comments = Members = Events = 0;
        Coins = 0;
        Since = DateTime.Now;
        OnPropertyChanged(nameof(Since));
    }
}
