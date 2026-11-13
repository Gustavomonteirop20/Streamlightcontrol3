namespace StreamLightControl2.Core.Models;

/// <summary>Een BetterTok-event, omgezet naar één vast formaat (Local- en Cloud-mode).</summary>
public sealed class StreamEvent
{
    /// <summary>Uniek id voor ontdubbelen (Cloud: evt_…; Local: afgeleid van msgId). Kan leeg zijn.</summary>
    public string DedupeKey { get; init; } = "";
    public string Type { get; init; } = EventTypes.Other;
    public string RawType { get; init; } = "";
    public string User { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string GiftName { get; init; } = "";
    public string GiftId { get; init; } = "";
    public int GiftCount { get; init; } = 1;
    public long CoinsPerItem { get; init; }
    public long TotalCoins { get; init; }
    /// <summary>True zolang een gift-combo nog loopt (Local Mode: giftType 1 zonder repeatEnd).</summary>
    public bool StreakInProgress { get; init; }
    public int LikeCount { get; init; }
    public string Comment { get; init; } = "";
    /// <summary>Bericht van de supporter bij een DonateWave-tip (als de API het meestuurt).</summary>
    public string TipMessage { get; init; } = "";
    /// <summary>DonateWave: "" = gewone tip, "shoutout" = shout-out (special), "wheel" = Lucky Wheel.</summary>
    public string TipKind { get; init; } = "";
    /// <summary>Moment van het event bij de bron (DonateWave created_at), in UTC. Null = onbekend (nu).</summary>
    public DateTime? OccurredAtUtc { get; init; }
    public string Raw { get; init; } = "";
    public bool FromCloud { get; init; }
    public bool IsTest { get; init; }
    public DateTime ReceivedAt { get; init; } = DateTime.Now;

    public string UserLabel => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName
        : !string.IsNullOrWhiteSpace(User) ? User : "—";

    public static StreamEvent Test(string type, string giftName = "", long coins = 0, int likes = 0, string comment = "", string user = "testkijker") => new()
    {
        Type = type, RawType = type, User = user, DisplayName = "Test · " + user,
        GiftName = giftName, GiftCount = 1, CoinsPerItem = coins, TotalCoins = coins,
        LikeCount = likes, Comment = comment, IsTest = true, DedupeKey = "test:" + Guid.NewGuid().ToString("N")
    };
}
