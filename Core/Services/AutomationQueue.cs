using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Core.Services;

public enum FinishReason { Completed, Skipped, Stopped, Interrupted, Failed, Removed, Cleared, Dropped }

/// <summary>
/// Echte wachtrij met prioriteiten. Eén worker speelt effecten na elkaar af via de LightShowEngine.
/// Hoogste prioriteit eerst, bij gelijke prioriteit wie het eerst kwam.
/// </summary>
public sealed class AutomationQueue : IDisposable
{
    private readonly object _gate = new();
    private readonly List<QueueItem> _pending = new();
    private readonly LightShowEngine _engine;
    private readonly Func<IReadOnlyList<string>> _targets;
    private readonly Func<QueueOptions> _options;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _currentCts;
    private FinishReason _cancelReason = FinishReason.Skipped;
    private long _sequence;
    private bool _paused;
    private Task? _worker;

    public AutomationQueue(LightShowEngine engine, Func<IReadOnlyList<string>> targets, Func<QueueOptions> options)
    {
        _engine = engine;
        _targets = targets;
        _options = options;
    }

    public QueueItem? Current { get; private set; }
    public bool Paused { get { lock (_gate) return _paused; } }

    public event Action? Changed;
    public event Action<QueueItem>? Started;
    public event Action<QueueItem, FinishReason, string?>? Finished;
    /// <summary>Wordt aangeroepen als er een show moet spelen maar er geen apparaat is.</summary>
    public event Action? NoTargets;

    public void Start() => _worker ??= Task.Run(WorkerAsync);

    public IReadOnlyList<QueueItem> PendingSnapshot() { lock (_gate) return _pending.ToList(); }

    public int CountForTrigger(string triggerId)
    {
        lock (_gate)
            return _pending.Count(p => p.TriggerId == triggerId) + (Current?.TriggerId == triggerId ? 1 : 0);
    }

    /// <summary>Maximaal zoveel te laat: daarna begint de show gewoon vooraan (de sound is dan al lang bezig of klaar).</summary>
    public const int MaxCatchUpMs = 3000;

    public QueueItem Enqueue(LightShow show, int priority, string user, string eventType, string triggerId,
        QueueSource source, bool interrupt, HistoryEntry? history = null, DateTime? startAtUtc = null, int maxWaitMs = 0, int holdMs = 0)
    {
        // Op volgorde: events onderbreken elkaar nooit (tests en handmatig afspelen mogen dat wel)
        if (_options().InOrder && source == QueueSource.Event) interrupt = false;
        var item = new QueueItem
        {
            Show = show.Clone(), Priority = priority, User = user, EventType = eventType, TriggerId = triggerId,
            Source = source, Interrupt = interrupt, History = history, Sequence = Interlocked.Increment(ref _sequence),
            StartAtUtc = startAtUtc, MaxWaitMs = maxWaitMs, HoldMs = holdMs
        };
        QueueItem? dropped = null;
        lock (_gate)
        {
            var max = _options().MaxLength;
            if (_pending.Count >= max)
            {
                // Laagste prioriteit (en nieuwste) valt af – eventueel het nieuwe item zelf
                var lowest = _pending.Append(item).OrderBy(p => p.Priority).ThenByDescending(p => p.Sequence).First();
                dropped = lowest;
                if (!ReferenceEquals(lowest, item)) { _pending.Remove(lowest); _pending.Add(item); }
            }
            else _pending.Add(item);
            Sort();

            if (!ReferenceEquals(dropped, item) && interrupt && Current != null && priority > Current.Priority)
            {
                _cancelReason = FinishReason.Interrupted;
                _currentCts?.Cancel();
            }
        }
        if (dropped != null) Finished?.Invoke(dropped, FinishReason.Dropped, "Wachtrij vol");
        if (!ReferenceEquals(dropped, item) && item.History != null) item.History.Status = ProcessingStatus.Queued;
        _signal.Release();
        Changed?.Invoke();
        return item;
    }

    private void Sort()
    {
        if (_options().InOrder) _pending.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
        else _pending.Sort((a, b) => b.Priority != a.Priority ? b.Priority.CompareTo(a.Priority) : a.Sequence.CompareTo(b.Sequence));
    }

    public bool Remove(string id)
    {
        QueueItem? item;
        lock (_gate)
        {
            item = _pending.FirstOrDefault(p => p.Id == id);
            if (item != null) _pending.Remove(item);
        }
        if (item == null) return false;
        Finished?.Invoke(item, FinishReason.Removed, null);
        Changed?.Invoke();
        return true;
    }

    public void SetPriority(string id, int priority)
    {
        lock (_gate)
        {
            var item = _pending.FirstOrDefault(p => p.Id == id);
            if (item == null) return;
            item.Priority = Math.Clamp(priority, 1, Priorities.Manual);
            Sort();
        }
        Changed?.Invoke();
    }

    /// <summary>Verplaatst een item één plek omhoog/omlaag (past zo nodig de prioriteit aan).</summary>
    public void Move(string id, int direction)
    {
        lock (_gate)
        {
            var idx = _pending.FindIndex(p => p.Id == id);
            var other = idx + (direction < 0 ? -1 : 1);
            if (idx < 0 || other < 0 || other >= _pending.Count) return;
            var a = _pending[idx];
            var b = _pending[other];
            if (direction < 0) a.Priority = Math.Max(a.Priority, b.Priority);
            else a.Priority = Math.Min(a.Priority, b.Priority);
            (a.Sequence, b.Sequence) = (b.Sequence, a.Sequence);
            Sort();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        List<QueueItem> removed;
        lock (_gate) { removed = _pending.ToList(); _pending.Clear(); }
        foreach (var item in removed) Finished?.Invoke(item, FinishReason.Cleared, null);
        Changed?.Invoke();
    }

    /// <summary>Stopt het huidige effect en gaat door met het volgende.</summary>
    public void SkipCurrent()
    {
        lock (_gate) { _cancelReason = FinishReason.Skipped; _currentCts?.Cancel(); }
    }

    /// <summary>Stopt het huidige effect en pauzeert de wachtrij (Hervatten om door te gaan).</summary>
    public void StopCurrent()
    {
        lock (_gate) { _paused = true; _cancelReason = FinishReason.Stopped; _currentCts?.Cancel(); }
        Changed?.Invoke();
    }

    /// <summary>Stop alles: huidig effect stoppen en de wachtrij leegmaken.</summary>
    public void StopAll()
    {
        Clear();
        lock (_gate) { _cancelReason = FinishReason.Stopped; _currentCts?.Cancel(); }
    }

    public void Resume()
    {
        lock (_gate) _paused = false;
        _signal.Release();
        Changed?.Invoke();
    }

    private async Task WorkerAsync()
    {
        Baseline? baseline = null;
        IReadOnlyList<string> baselineTargets = Array.Empty<string>();
        var token = _lifetime.Token;
        while (!token.IsCancellationRequested)
        {
            QueueItem? item = null;
            lock (_gate)
            {
                if (!_paused && _pending.Count > 0)
                {
                    item = _pending[0];
                    _pending.RemoveAt(0);
                    Current = item;
                    _currentCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    _cancelReason = FinishReason.Skipped;
                }
            }
            if (item == null)
            {
                if (baseline != null || baselineTargets.Count > 0)
                {
                    // Wachtrij leeg: lampen terug naar hoe ze waren
                    await _engine.RestoreAsync(baseline, baselineTargets);
                    baseline = null;
                    baselineTargets = Array.Empty<string>();
                }
                try { await _signal.WaitAsync(token); } catch (OperationCanceledException) { return; }
                continue;
            }

            Changed?.Invoke();
            var targets = _targets();
            FinishReason reason;
            string? error = null;
            if (targets.Count == 0)
            {
                NoTargets?.Invoke();
                reason = FinishReason.Failed;
                error = "Geen actief Govee-apparaat";
            }
            else
            {
                try
                {
                    if (baselineTargets.Count == 0)
                    {
                        baselineTargets = targets;
                        baseline = await _engine.CaptureAsync(targets);
                    }
                    // Synchroon met de sound: wachten tot hij begint (bijv. na de animatie van een shout-out),
                    // of als we te laat zijn (polling) midden in de show beginnen.
                    var offset = 0;
                    if (item.StartAtUtc is not null)
                    {
                        // Het startmoment kan tijdens het wachten nog opschuiven (er komt bijv. een tweede
                        // animatie bij in de overlay): steeds opnieuw kijken, in stapjes van max. 50 ms.
                        item.WaitStartedUtc = DateTime.UtcNow;
                        while (true)
                        {
                            // Moet een ander ingepland item eerder beginnen (DonateWave: de sound van de 2e shout-out
                            // komt vóór die van de 1e), dan dat eerst; dit item gaat terug vooraan in de wachtrij.
                            QueueItem? earlier = null;
                            lock (_gate)
                            {
                                foreach (var p in _pending)
                                    if (p.StartAtUtc is { } ps && ps < item.StartAtUtc!.Value && (earlier == null || ps < earlier.StartAtUtc!.Value))
                                        earlier = p;
                                if (earlier != null)
                                {
                                    _pending.Remove(earlier);
                                    _pending.Insert(0, item);
                                    item.WaitStartedUtc = null;
                                    item = earlier;
                                    Current = item;
                                    item.WaitStartedUtc = DateTime.UtcNow;
                                }
                            }
                            if (earlier != null) Changed?.Invoke();
                            var at = item.StartAtUtc!.Value;
                            var wait = (at - DateTime.UtcNow).TotalMilliseconds;
                            var waited = (DateTime.UtcNow - item.WaitStartedUtc.Value).TotalMilliseconds;
                            if (wait <= 0 || waited >= item.MaxWaitMs)
                            {
                                if (wait < 0 && -wait <= MaxCatchUpMs) offset = (int)-wait;
                                break;
                            }
                            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(50, Math.Max(1, wait))), _currentCts!.Token);
                        }
                    }
                    if (item.History != null) item.History.Status = ProcessingStatus.Playing;
                    Started?.Invoke(item);
                    var playStart = DateTime.UtcNow.AddMilliseconds(-offset);
                    await _engine.PlayAsync(item.Show, targets, _currentCts!.Token, offset);
                    // Speelt de sound van dit event nog (bijv. follow-sound in BetterTok)? Dan pas daarna de volgende.
                    if (item.HoldMs > 0)
                    {
                        var left = (playStart.AddMilliseconds(item.HoldMs) - DateTime.UtcNow).TotalMilliseconds;
                        if (left > 0) await Task.Delay(TimeSpan.FromMilliseconds(left), _currentCts!.Token);
                    }
                    reason = FinishReason.Completed;
                }
                catch (OperationCanceledException)
                {
                    lock (_gate) reason = _cancelReason;
                    if (token.IsCancellationRequested) return;
                }
                catch (Exception ex)
                {
                    reason = FinishReason.Failed;
                    error = ex.Message;
                }
            }

            lock (_gate)
            {
                Current = null;
                _currentCts?.Dispose();
                _currentCts = null;
            }
            Finished?.Invoke(item, reason, error);
            Changed?.Invoke();

            var gap = _options().GapMs;
            if (gap > 0 && reason == FinishReason.Completed)
            {
                try { await Task.Delay(gap, token); } catch (OperationCanceledException) { return; }
            }
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _currentCts?.Cancel();
    }
}
