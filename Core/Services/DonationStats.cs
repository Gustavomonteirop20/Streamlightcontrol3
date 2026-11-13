using System.Globalization;
using System.Text.Json;
using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Core.Services;

/// <summary>Eén ontvangen DonateWave-tip, zoals de kerst-overlays hem tonen.</summary>
public sealed class TipRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long Cents { get; set; }
    public int Quantity { get; set; } = 1;
    public bool Special { get; set; }
    public bool Wheel { get; set; }
    public bool Test { get; set; }
    public string Message { get; set; } = "";
    public DateTime AtUtc { get; set; }
    /// <summary>Moment waarop DonateWave de sound/naam laat zien: dan verschijnt ook de kerst-alert.</summary>
    public DateTime AlertAtUtc { get; set; }
}

/// <summary>
/// Houdt de DonateWave-tips bij voor de kerst-overlays: tipdoel, ranking, recente tips, laatste supporter,
/// weekkampioen, vandaag en de alerts. Bewaard in donations.json (laatste 120 dagen).
/// </summary>
public sealed class DonationStats
{
    private readonly object _gate = new();
    private readonly List<TipRecord> _tips = new();
    private readonly string _path;
    private int _version;

    public DonationStats(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "donations.json");
        try
        {
            if (File.Exists(_path))
                _tips.AddRange(JsonSerializer.Deserialize<List<TipRecord>>(File.ReadAllText(_path)) ?? new());
        }
        catch { /* kapot bestand: opnieuw beginnen */ }
    }

    /// <summary>Verandert bij elke nieuwe tip (de overlays tonen dan meteen de nieuwe stand).</summary>
    public int Version { get { lock (_gate) return _version; } }

    public TipRecord? Add(StreamEvent e, DateTime alertAtUtc)
    {
        if (e.Type != EventTypes.Tip) return null;
        var id = e.DedupeKey.Length > 0 ? e.DedupeKey : Guid.NewGuid().ToString("N");
        var rec = new TipRecord
        {
            Id = id,
            Name = e.User.Length > 0 ? e.User : "Anoniem",
            Cents = Math.Max(0, e.TotalCoins),
            Quantity = Math.Max(1, e.GiftCount),
            Special = e.TipKind == TipKinds.Shoutout,
            Wheel = e.TipKind == TipKinds.Wheel,
            Test = e.IsTest,
            Message = e.TipMessage,
            AtUtc = (e.OccurredAtUtc ?? DateTime.UtcNow).ToUniversalTime(),
            AlertAtUtc = alertAtUtc.ToUniversalTime()
        };
        lock (_gate)
        {
            if (_tips.Any(t => t.Id == id)) return null;
            _tips.Add(rec);
            var cutoff = DateTime.UtcNow.AddDays(-120);
            _tips.RemoveAll(t => t.AtUtc < cutoff);
            if (_tips.Count > 50000) _tips.RemoveRange(0, _tips.Count - 50000);
            _version++;
            Save();
        }
        return rec;
    }

    /// <summary>Alle bijgehouden tips wissen (ranking, doel, recente tips beginnen opnieuw).</summary>
    public void Clear()
    {
        lock (_gate) { _tips.Clear(); _version++; Save(); }
    }

    public int Count { get { lock (_gate) return _tips.Count; } }

    private void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_tips));
            File.Move(tmp, _path, true);
        }
        catch { /* niet erg: volgende tip probeert opnieuw */ }
    }

    private static string Euro(long cents) => "€ " + (cents / 100m).ToString("#,0.00", CultureInfo.GetCultureInfo("nl-NL"));

    /// <summary>Alles wat de overlays nodig hebben, als JSON-object.</summary>
    public object Snapshot(OverlayOptions o, DateTime nowUtc)
    {
        List<TipRecord> all;
        int version;
        lock (_gate) { all = _tips.ToList(); version = _version; }
        var counted = all.Where(t => o.CountTests || !t.Test).OrderBy(t => t.AtUtc).ToList();
        var nowLocal = nowUtc.ToLocalTime();
        var todayStart = nowLocal.Date.ToUniversalTime();
        var monday = nowLocal.Date.AddDays(-(((int)nowLocal.DayOfWeek + 6) % 7)).ToUniversalTime();
        var monthStart = new DateTime(nowLocal.Year, nowLocal.Month, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();

        object Tip(TipRecord t) => new
        {
            id = t.Id, name = t.Name, amount = Euro(t.Cents), cents = t.Cents, quantity = t.Quantity,
            special = t.Special, wheel = t.Wheel, test = t.Test, message = t.Message,
            at = new DateTimeOffset(t.AtUtc).ToUnixTimeMilliseconds()
        };
        object[] Top(DateTime fromUtc, int n) => counted.Where(t => t.AtUtc >= fromUtc)
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { name = g.Last().Name, cents = g.Sum(x => x.Cents), count = g.Count() })
            .OrderByDescending(x => x.cents).ThenBy(x => x.name).Take(n)
            .Select((x, i) => (object)new { rank = i + 1, x.name, amount = Euro(x.cents), x.cents, x.count }).ToArray();

        var goalCents = counted.Where(t => t.AtUtc >= o.GoalStartUtc).Sum(t => t.Cents);
        var today = counted.Where(t => t.AtUtc >= todayStart).ToList();
        var week = Top(monday, 1);
        var last = counted.LastOrDefault();

        // Alerts: op het moment dat DonateWave de tip laat zien, nooit over elkaar heen
        var alerts = new List<object>();
        var free = DateTime.MinValue;
        foreach (var t in all.Where(t => t.AlertAtUtc > nowUtc.AddMinutes(-3)).OrderBy(t => t.AlertAtUtc))
        {
            var show = t.AlertAtUtc > free ? t.AlertAtUtc : free.AddMilliseconds(400);
            free = show.AddMilliseconds(o.AlertMs);
            if (free < nowUtc.AddSeconds(-5)) continue;
            alerts.Add(new
            {
                id = t.Id, name = t.Name, amount = Euro(t.Cents), quantity = t.Quantity, special = t.Special, wheel = t.Wheel,
                test = t.Test, message = t.Message,
                showAt = new DateTimeOffset(show).ToUnixTimeMilliseconds(), durationMs = o.AlertMs
            });
        }

        return new
        {
            version,
            now = new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds(),
            goal = new
            {
                title = o.GoalTitle.Length > 0 ? o.GoalTitle : "Kerstdoel",
                current = Euro(goalCents), target = Euro(o.GoalCents),
                remaining = Euro(Math.Max(0, o.GoalCents - goalCents)),
                percent = o.GoalCents > 0 ? Math.Min(100, Math.Round(goalCents * 100.0 / o.GoalCents, 1)) : 0,
                reached = goalCents >= o.GoalCents
            },
            last = last == null ? null : Tip(last),
            recent = counted.AsEnumerable().Reverse().Take(5).Select(Tip).ToArray(),
            today = new { amount = Euro(today.Sum(t => t.Cents)), cents = today.Sum(t => t.Cents), count = today.Count },
            weekChampion = week.FirstOrDefault(),
            ranking = Top(monthStart, 5),
            alerts
        };
    }
}
