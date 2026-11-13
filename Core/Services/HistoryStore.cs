using System.IO;
using System.Text.Json;
using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Core.Services;

/// <summary>Bewaart de Event History (laatste 3000 events) in history.json en het ruwe eventlog in events.log.</summary>
public sealed class HistoryStore : IDisposable
{
    public const int MaxEntries = 3000;
    private readonly object _gate = new();
    private readonly List<HistoryEntry> _entries = new();
    private readonly string? _dir;
    private Timer? _saveTimer;

    public HistoryStore(string? directory) => _dir = directory;

    public event Action<HistoryEntry>? Added;
    public event Action? Cleared;

    public IReadOnlyList<HistoryEntry> Snapshot() { lock (_gate) return _entries.ToList(); }

    public void Load()
    {
        if (_dir == null) return;
        var path = Path.Combine(_dir, "history.json");
        try
        {
            if (!File.Exists(path)) return;
            var list = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(path), SettingsStore.JsonOptions) ?? new();
            foreach (var e in list)
            {
                // Status van een vorige sessie die nooit is afgerond
                if (e.Status is ProcessingStatus.Queued or ProcessingStatus.Playing or ProcessingStatus.Received)
                    e.Status = ProcessingStatus.Skipped;
                e.PropertyChanged += (_, _) => RequestSave();
            }
            lock (_gate) { _entries.Clear(); _entries.AddRange(list.Take(MaxEntries)); }
        }
        catch { /* beschadigde geschiedenis is niet kritiek */ }
    }

    public HistoryEntry Add(HistoryEntry entry)
    {
        lock (_gate)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        }
        entry.PropertyChanged += (_, _) => RequestSave();
        Added?.Invoke(entry);
        RequestSave();
        return entry;
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
        Cleared?.Invoke();
        RequestSave();
    }

    public void AppendRaw(string line)
    {
        if (_dir == null) return;
        try
        {
            var path = Path.Combine(_dir, "events.log");
            lock (_gate)
            {
                // events.log niet eindeloos laten groeien
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 5_000_000) File.Move(path, path + ".old", true);
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
            }
        }
        catch { }
    }

    private void RequestSave()
    {
        if (_dir == null) return;
        lock (_gate)
        {
            _saveTimer ??= new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(2000, Timeout.Infinite);
        }
    }

    public void SaveNow()
    {
        if (_dir == null) return;
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_entries, SettingsStore.JsonOptions);
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, "history.json");
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, true);
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_gate) { _saveTimer?.Dispose(); _saveTimer = null; }
        SaveNow();
    }
}
