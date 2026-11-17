using System.IO;
using System.Globalization;
using System.Text.Json;
using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Core.Services;

/// <summary>Eén TikTok-event (gift, volger of sub) zoals de kerst-TikTok-overlays hem tonen.</summary>
public sealed class TikTokRecord
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";      // gift, follow, subscribe
    public string User { get; set; } = "";      // TikTok-naam (voor groeperen)
    public string Name { get; set; } = "";      // weergavenaam
    public bool HasDisplay { get; set; }
    public string Gift { get; set; } = "";
    public int Count { get; set; } = 1;
    public long Coins { get; set; }
    public bool Test { get; set; }
    public DateTime AtUtc { get; set; }
}

/// <summary>
/// Houdt gifts, volgers en subs van BetterTok bij voor de kerst-TikTok-overlays: giftalert, Gift Gallery,
/// leaderboard, grootste gift en laatste volger. Telt vanaf "Nieuwe stream" (TikTokStartUtc). Bewaard in tiktok.json.
/// </summary>
public sealed class TikTokStats
{
    public const int AlertGapMs = 400;
    private readonly object _gate = new();
    private readonly List<TikTokRecord> _items = new();
    private readonly string _path;
    private int _version;

    public TikTokStats(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "tiktok.json");
        try
        {
            if (File.Exists(_path))
                _items.AddRange(JsonSerializer.Deserialize<List<TikTokRecord>>(File.ReadAllText(_path)) ?? new());
        }
        catch { /* kapot bestand: opnieuw beginnen */ }
    }

    public int Version { get { lock (_gate) return _version; } }
    public int Count { get { lock (_gate) return _items.Count; } }

    /// <summary>Gift (pas als de combo klaar is), volger of sub toevoegen. Andere events en lopende combo's: null.</summary>
    public TikTokRecord? Add(StreamEvent e)
    {
        if (e.Type is not (EventTypes.Gift or EventTypes.Follow or EventTypes.Subscribe)) return null;
        if (e.Type == EventTypes.Gift && e.StreakInProgress) return null;    // combo telt pas als hij klaar is
        var id = e.DedupeKey.Length > 0 ? e.DedupeKey : Guid.NewGuid().ToString("N");
        var user = (e.User.Length > 0 ? e.User : e.DisplayName).Trim().TrimStart('@');
        var rec = new TikTokRecord
        {
            Id = id,
            Type = e.Type,
            User = user.Length > 0 ? user : "kijker",
            Name = !string.IsNullOrWhiteSpace(e.DisplayName) ? e.DisplayName.Trim() : user.Length > 0 ? user : "Kijker",
            HasDisplay = !string.IsNullOrWhiteSpace(e.DisplayName),
            Gift = e.Type == EventTypes.Gift ? (e.GiftName.Length > 0 ? e.GiftName : "Gift") : "",
            Count = Math.Max(1, e.GiftCount),
            Coins = e.Type == EventTypes.Gift ? Math.Max(0, e.TotalCoins > 0 ? e.TotalCoins : e.CoinsPerItem * Math.Max(1, e.GiftCount)) : 0,
            Test = e.IsTest,
            AtUtc = DateTime.UtcNow
        };
        lock (_gate)
        {
            if (_items.Any(t => t.Id == id)) return null;
            _items.Add(rec);
            var cutoff = DateTime.UtcNow.AddDays(-60);
            _items.RemoveAll(t => t.AtUtc < cutoff);
            if (_items.Count > 100000) _items.RemoveRange(0, _items.Count - 100000);
            _version++;
            Save();
        }
        return rec;
    }

    /// <summary>Alles wissen.</summary>
    public void Clear() { lock (_gate) { _items.Clear(); _version++; Save(); } }

    /// <summary>Overlays opnieuw laten tekenen (bijv. na "Nieuwe stream").</summary>
    public void Touch() { lock (_gate) _version++; }

    private void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items));
            File.Move(tmp, _path, true);
        }
        catch { /* niet erg: volgende event probeert opnieuw */ }
    }

    /// <summary>Weergavenaam als die ooit is meegestuurd, anders de TikTok-naam.</summary>
    private static string BestName(IEnumerable<TikTokRecord> g) => (g.LastOrDefault(x => x.HasDisplay) ?? g.Last()).Name;

    public static string Coins(long c) => c.ToString("#,0", CultureInfo.GetCultureInfo("nl-NL")) + (c == 1 ? " coin" : " coins");

    /// <summary>Kerst-icoon per waarde: 1 = zuurstok, 2 = cadeautje, 3 = kerstbal, 4 = kerstboom, 5 = arrenslee.</summary>
    public static int Tier(long coinsPerItem) => coinsPerItem switch { < 10 => 1, < 100 => 2, < 1000 => 3, < 10000 => 4, _ => 5 };

    /// <summary>Alles wat de TikTok-overlays nodig hebben.</summary>
    public object Snapshot(OverlayOptions o, DateTime nowUtc)
    {
        List<TikTokRecord> all; int version;
        lock (_gate) { all = _items.Where(t => t.AtUtc >= o.TikTokStartUtc).ToList(); version = _version; }
        // Tellingen zonder testevents (tenzij "Testtips meetellen"); alerts en "laatste" tonen tests wel, zodat je ziet dat het werkt
        var gifts = all.Where(t => t.Type == EventTypes.Gift).OrderBy(t => t.AtUtc).ToList();
        var counted = gifts.Where(t => o.CountTests || !t.Test).ToList();

        object Gift(TikTokRecord t) => new
        {
            id = t.Id, name = t.Name, gift = t.Gift, count = t.Count, coins = t.Coins, coinsText = Coins(t.Coins),
            tier = Tier(t.Coins / Math.Max(1, t.Count)), test = t.Test, at = new DateTimeOffset(t.AtUtc).ToUnixTimeMilliseconds()
        };
        object? Person(TikTokRecord? t) => t == null ? null : new { id = t.Id, name = t.Name, test = t.Test, at = new DateTimeOffset(t.AtUtc).ToUnixTimeMilliseconds() };

        var ranking = counted.GroupBy(t => t.User, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { name = BestName(g), coins = g.Sum(x => x.Coins), gifts = g.Sum(x => x.Count) })
            .OrderByDescending(x => x.coins).ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase).Take(5)
            .Select((x, i) => (object)new { rank = i + 1, x.name, x.coins, coinsText = Coins(x.coins), x.gifts }).ToArray();

        var gallery = counted.GroupBy(t => t.Gift, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var top = g.GroupBy(x => x.User, StringComparer.OrdinalIgnoreCase).OrderByDescending(u => u.Sum(x => x.Count)).ThenBy(u => u.Min(x => x.AtUtc)).First();
                var count = g.Sum(x => x.Count); var coins = g.Sum(x => x.Coins);
                return new { gift = g.First().Gift, count, coins, per = coins / Math.Max(1, count), top = BestName(top), last = g.Max(x => x.AtUtc) };
            })
            .OrderByDescending(x => x.per).ThenByDescending(x => x.count).ThenBy(x => x.gift).Take(12)
            .Select(x => (object)new { x.gift, x.count, x.coins, coinsText = Coins(x.coins), tier = Tier(x.per), x.top }).ToArray();

        var top1 = counted.OrderByDescending(t => t.Coins).ThenBy(t => t.AtUtc).FirstOrDefault();

        // Giftalerts: op het moment dat de gift binnenkomt, nooit over elkaar heen
        var alerts = new List<object>();
        var free = DateTime.MinValue;
        foreach (var t in gifts.Where(t => t.AtUtc > nowUtc.AddMinutes(-3)))
        {
            var show = t.AtUtc > free ? t.AtUtc : free.AddMilliseconds(AlertGapMs);
            free = show.AddMilliseconds(o.TikTokAlertMs);
            if (free < nowUtc.AddSeconds(-5)) continue;
            alerts.Add(new
            {
                id = t.Id, name = t.Name, gift = t.Gift, count = t.Count, coinsText = Coins(t.Coins), tier = Tier(t.Coins / Math.Max(1, t.Count)),
                test = t.Test, showAt = new DateTimeOffset(show).ToUnixTimeMilliseconds(), durationMs = o.TikTokAlertMs
            });
        }

        return new
        {
            version,
            now = new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds(),
            totals = new { coins = counted.Sum(t => t.Coins), coinsText = Coins(counted.Sum(t => t.Coins)), gifts = counted.Sum(t => t.Count) },
            ranking,
            gallery,
            topGift = top1 == null ? null : Gift(top1),
            lastFollower = Person(all.LastOrDefault(t => t.Type == EventTypes.Follow)),
            lastSub = Person(all.LastOrDefault(t => t.Type == EventTypes.Subscribe)),
            lastGift = gifts.Count == 0 ? null : Gift(gifts[^1]),
            alerts
        };
    }
}
