using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Core.Services;

public enum DecisionKind { Play, NoRule, Duplicate, StreakInProgress, Cooldown, FollowRepeat, FollowGrouped, BelowThreshold, QueueLimit, ShowMissing, ComboRemainder }

public sealed record RuleDecision(DecisionKind Kind, EventTrigger? Trigger, LightShow? Show, string Reason)
{
    public bool ShouldPlay => Kind == DecisionKind.Play;
}

/// <summary>
/// Bepaalt per event welke ÉÉN koppeling (en dus welke ene lichtshow) wordt afgespeeld.
/// Volgorde: specifieke giftnaam &gt; coin-bereik &gt; algemeen; daarbinnen hoogste prioriteit,
/// daarna het smalste coin-bereik. Zo speelt één gift nooit meerdere effecten af.
/// </summary>
public sealed class RuleEngine
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _seen = new();
    private readonly Dictionary<string, DateTime> _lastFired = new();
    private readonly Dictionary<string, long> _likeAccumulator = new();
    private readonly HashSet<string> _followedUsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _cloudCombos = new();
    private DateTime _lastFollowEffect = DateTime.MinValue;

    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    /// <summary>Kiest de koppeling zonder iets te onthouden (gebruikt door de testknoppen en de regelbouwer).</summary>
    public static EventTrigger? SelectTrigger(StreamEvent e, IEnumerable<EventTrigger> triggers)
    {
        var candidates = triggers.Where(t => t.Enabled && t.EventType == e.Type).ToList();
        if (candidates.Count == 0) return null;

        if (e.Type == EventTypes.Gift)
        {
            var byName = candidates
                .Where(t => t.GiftNameList.Count > 0 && t.GiftNameList.Any(n => n.Equals(e.GiftName, StringComparison.OrdinalIgnoreCase) || (n.Length > 0 && n == e.GiftId)))
                .Where(t => t.CoinsMatch(e.TotalCoins) || (t.MinCoins == null && t.MaxCoins == null))
                .OrderByDescending(t => t.Priority)
                .FirstOrDefault();
            if (byName != null) return byName;

            return candidates
                .Where(t => t.GiftNameList.Count == 0 && t.CoinsMatch(e.TotalCoins))
                .OrderByDescending(t => t.Priority)
                .ThenBy(t => RangeWidth(t))
                .ThenByDescending(t => t.MinCoins ?? 0)
                .FirstOrDefault();
        }

        if (e.Type == EventTypes.Tip)
        {
            // DonateWave: een regel voor deze soort (shout-out / Lucky Wheel) gaat voor; daarna de gewone bedrag-regels.
            // Binnen een groep: hoogste prioriteit, dan het smalste bedrag-bereik (TotalCoins = eurocenten).
            // Stuurt DonateWave de naam van de shout-out mee, dan gaat een regel met precies die naam voor
            var byName = candidates.Where(t => t.MatchText.Length > 0 && e.Comment.Contains(t.MatchText, StringComparison.OrdinalIgnoreCase)
                                               && (t.TipKind.Length == 0 || t.TipKind == e.TipKind)).OrderByDescending(t => t.Priority).FirstOrDefault();
            if (byName != null) return byName;
            var pool = candidates.Where(t => t.CoinsMatch(e.TotalCoins)).ToList();
            var kind = e.TipKind.Length > 0 ? pool.Where(t => t.TipKind == e.TipKind).ToList() : new List<EventTrigger>();
            // Meerdere keer dezelfde shout-out (quantity > 1): eerst op de prijs per stuk zoeken
            if (e.GiftCount > 1 && e.TipKind.Length > 0 && e.CoinsPerItem > 0)
            {
                var perItem = candidates.Where(t => t.TipKind == e.TipKind && t.CoinsMatch(e.CoinsPerItem)).ToList();
                if (perItem.Count > 0) kind = perItem;
            }
            return (kind.Count > 0 ? kind : pool.Where(t => t.TipKind.Length == 0))
                .OrderByDescending(t => t.Priority)
                .ThenBy(t => RangeWidth(t))
                .ThenByDescending(t => t.MinCoins ?? 0)
                .FirstOrDefault();
        }

        if (e.Type == EventTypes.Member)
        {
            // Binnenkomst: een regel met een gebruikersnaam geldt alleen voor die kijker; regels zonder naam voor iedereen
            var who = candidates.Where(t => t.MatchText.Length > 0 && UserMatches(t.MatchText, e))
                .OrderByDescending(t => t.Priority).FirstOrDefault();
            if (who != null) return who;
            return candidates.Where(t => t.MatchText.Length == 0).OrderByDescending(t => t.Priority).FirstOrDefault();
        }

        if (e.Type == EventTypes.Comment)
        {
            return candidates
                .Where(t => t.MatchText.Length == 0 || e.Comment.Contains(t.MatchText, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.MatchText.Length > 0) // specifiek woord gaat voor
                .ThenByDescending(t => t.Priority)
                .FirstOrDefault();
        }

        return candidates.OrderByDescending(t => t.Priority).FirstOrDefault();
    }

    /// <summary>Komt de kijker overeen met een van de namen (gescheiden door komma's)? Vergelijkt de TikTok-naam (@…)
    /// en de weergavenaam, zonder hoofdletters en zonder @.</summary>
    public static bool UserMatches(string names, StreamEvent e)
    {
        static string N(string? s) => (s ?? "").Trim().TrimStart('@').Trim().ToLowerInvariant();
        var user = N(e.User); var display = N(e.DisplayName);
        foreach (var raw in names.Split(',', ';'))
        {
            var n = N(raw);
            if (n.Length == 0) continue;
            if (n == user || n == display) return true;
        }
        return false;
    }

    private static long RangeWidth(EventTrigger t) => (t.MinCoins, t.MaxCoins) switch
    {
        ({ } a, { } b) => b - a,
        _ => long.MaxValue
    };

    /// <param name="queuedForTrigger">Aantal effecten van een koppeling dat nu al in de wachtrij staat.</param>
    public RuleDecision Evaluate(StreamEvent e, AppSettings settings, Func<string, int> queuedForTrigger, bool checkDuplicate = true)
    {
        var now = Clock();
        lock (_gate)
        {
            // 1. Dubbel bericht? (meestal al gecontroleerd via CheckDuplicate)
            if (checkDuplicate && IsDuplicateLocked(e, now))
                return new(DecisionKind.Duplicate, null, null, "Dubbel bericht – al verwerkt");

            // 2. Gift-combo nog bezig?
            if (e.Type == EventTypes.Gift && e.StreakInProgress && settings.Gift.StreakMode == GiftStreakMode.AtEnd)
                return new(DecisionKind.StreakInProgress, null, null, $"Combo loopt nog (x{e.GiftCount}) – effect volgt aan het eind");

            // 3. Cloud: late 'restant'-melding van dezelfde combo
            if (e.Type == EventTypes.Gift && e.FromCloud && settings.Gift.CloudComboMergeSeconds > 0)
            {
                var comboKey = $"{e.User}|{e.GiftId}|{e.GiftName}";
                if (_cloudCombos.TryGetValue(comboKey, out var last) && (now - last).TotalSeconds < settings.Gift.CloudComboMergeSeconds)
                {
                    _cloudCombos[comboKey] = now;
                    return new(DecisionKind.ComboRemainder, null, null, "Naschot van dezelfde combo – samengevoegd");
                }
                _cloudCombos[comboKey] = now;
            }

            var trigger = SelectTrigger(e, settings.Triggers);
            if (trigger == null)
                return new(DecisionKind.NoRule, null, null, "Geen actieve koppeling voor dit event");

            var show = settings.FindShow(trigger.ShowId);
            if (show == null)
                return new(DecisionKind.ShowMissing, trigger, null, $"Koppeling ‘{trigger.Name}’ heeft geen (bestaande) lichtshow");

            // 4. Follow-regels
            if (e.Type == EventTypes.Follow && !e.IsTest)
            {
                if (settings.Follow.OncePerUser && !string.IsNullOrEmpty(e.User) && !_followedUsers.Add(e.User))
                    return new(DecisionKind.FollowRepeat, trigger, show, "Deze kijker volgde deze sessie al eerder");
                if (settings.Follow.GroupWindowSeconds > 0 && (now - _lastFollowEffect).TotalSeconds < settings.Follow.GroupWindowSeconds)
                    return new(DecisionKind.FollowGrouped, trigger, show, "Gebundeld met een follow van zojuist");
            }

            // 5. Likes optellen tot de drempel
            if (e.Type == EventTypes.Like && trigger.LikeThreshold > 0)
            {
                var sum = (_likeAccumulator.TryGetValue(trigger.Id, out var acc) ? acc : 0) + Math.Max(1, e.LikeCount);
                if (sum < trigger.LikeThreshold)
                {
                    _likeAccumulator[trigger.Id] = sum;
                    return new(DecisionKind.BelowThreshold, trigger, show, $"{sum}/{trigger.LikeThreshold} likes");
                }
                _likeAccumulator[trigger.Id] = Math.Min(sum - trigger.LikeThreshold, trigger.LikeThreshold - 1);
            }

            // 6. Cooldown
            if (trigger.CooldownSeconds > 0 && _lastFired.TryGetValue(trigger.Id, out var fired) &&
                (now - fired).TotalSeconds < trigger.CooldownSeconds)
                return new(DecisionKind.Cooldown, trigger, show, $"Cooldown ({trigger.CooldownSeconds - (int)(now - fired).TotalSeconds} s)");

            // 7. Niet te veel van dezelfde in de wachtrij
            if (trigger.MaxInQueue > 0 && queuedForTrigger(trigger.Id) >= trigger.MaxInQueue)
                return new(DecisionKind.QueueLimit, trigger, show, $"Al {trigger.MaxInQueue} in de wachtrij");

            _lastFired[trigger.Id] = now;
            if (e.Type == EventTypes.Follow) _lastFollowEffect = now;
            return new(DecisionKind.Play, trigger, show, $"Koppeling ‘{trigger.Name}’");
        }
    }

    /// <summary>Registreert het event en geeft true als het al eerder is verwerkt.</summary>
    public bool CheckDuplicate(StreamEvent e)
    {
        lock (_gate) return IsDuplicateLocked(e, Clock());
    }

    private bool IsDuplicateLocked(StreamEvent e, DateTime now)
    {
        Prune(now);
        if (string.IsNullOrEmpty(e.DedupeKey)) return false;
        // Echte id's (Cloud evt_…, Local msgId) 10 minuten onthouden; berichten zonder id alleen 3 seconden
        // (zo worden identieke maar losse likes niet weggegooid).
        var window = e.DedupeKey.StartsWith("raw:") ? TimeSpan.FromSeconds(3) : TimeSpan.FromMinutes(10);
        if (_seen.TryGetValue(e.DedupeKey, out var seenAt) && now - seenAt < window) return true;
        _seen[e.DedupeKey] = now;
        return false;
    }

    private void Prune(DateTime now)
    {
        if (_seen.Count < 2000) return;
        foreach (var key in _seen.Where(kv => (now - kv.Value).TotalMinutes > (kv.Key.StartsWith("raw:") ? 0.05 : 10)).Select(kv => kv.Key).ToList())
            _seen.Remove(key);
        foreach (var key in _cloudCombos.Where(kv => (now - kv.Value).TotalMinutes > 5).Select(kv => kv.Key).ToList())
            _cloudCombos.Remove(key);
    }

    /// <summary>Nieuwe stream: follows en tellers opnieuw beginnen.</summary>
    public void ResetSession()
    {
        lock (_gate)
        {
            _followedUsers.Clear();
            _likeAccumulator.Clear();
            _lastFired.Clear();
            _cloudCombos.Clear();
            _lastFollowEffect = DateTime.MinValue;
        }
    }

    /// <summary>Waarschuwingen voor de regelbouwer: overlappende bereiken, gaten, ontbrekende shows.</summary>
    public static List<string> ValidateGiftRules(AppSettings settings)
    {
        var warnings = new List<string>();
        var ranges = settings.Triggers.Where(t => t.Enabled && t.EventType == EventTypes.Gift && t.GiftNameList.Count == 0)
            .OrderBy(t => t.MinCoins ?? 0).ToList();
        for (var i = 0; i < ranges.Count; i++)
        {
            var a = ranges[i];
            if (a.MinCoins is { } mn && a.MaxCoins is { } mx && mn > mx)
                warnings.Add($"‘{a.Name}’: de ondergrens ({mn}) is hoger dan de bovengrens ({mx}).");
            for (var j = i + 1; j < ranges.Count; j++)
            {
                var b = ranges[j];
                var aMax = a.MaxCoins ?? long.MaxValue;
                var bMin = b.MinCoins ?? 0;
                if (bMin <= aMax && (b.MaxCoins ?? long.MaxValue) >= (a.MinCoins ?? 0))
                    warnings.Add($"‘{a.Name}’ en ‘{b.Name}’ overlappen ({a.CoinRangeLabel} / {b.CoinRangeLabel}). Bij een gift in de overlap wint de hoogste prioriteit – er speelt maar één effect.");
            }
        }
        for (var i = 0; i + 1 < ranges.Count; i++)
        {
            if (ranges[i].MaxCoins is { } max && ranges[i + 1].MinCoins is { } nextMin && nextMin > max + 1)
                warnings.Add($"Gat tussen {max} en {nextMin} coins: gifts van {max + 1}–{nextMin - 1} coins krijgen geen effect.");
        }
        foreach (var t in settings.Triggers.Where(t => t.Enabled && settings.FindShow(t.ShowId) == null))
            warnings.Add($"‘{t.Name}’ heeft geen lichtshow gekozen.");
        return warnings;
    }
}
