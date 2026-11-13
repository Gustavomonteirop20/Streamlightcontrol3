using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace StreamLightControl2.Core.Services;

public enum PlayerState { NotRunning, Paused, Playing }

/// <summary>Een nummer zoals Spotify het toont: "Artiest - Titel".</summary>
public sealed record NowPlaying(string Artist, string Title)
{
    public string Key => (Artist + "\u0001" + Title).ToLowerInvariant();
    public string Label => string.IsNullOrEmpty(Artist) ? Title : $"{Artist} – {Title}";
}

public sealed record PlayerSnapshot(PlayerState State, NowPlaying? Track)
{
    public static readonly PlayerSnapshot NotRunning = new(PlayerState.NotRunning, null);
}

public interface INowPlayingSource
{
    PlayerSnapshot Read();
}

/// <summary>
/// Leest het nummer uit de vensternaam van de Spotify-desktopapp. Tijdens het afspelen heet het venster
/// "Artiest - Titel", bij pauze "Spotify", "Spotify Free" of "Spotify Premium".
/// Werkt zonder inloggen of Spotify-account koppeling, en ook als Spotify geminimaliseerd of in het systeemvak staat.
/// </summary>
public sealed class SpotifyWindowSource : INowPlayingSource
{
    public PlayerSnapshot Read()
    {
        if (!OperatingSystem.IsWindows()) return PlayerSnapshot.NotRunning;
        var procs = Process.GetProcessesByName("Spotify");
        if (procs.Length == 0) return PlayerSnapshot.NotRunning;
        var pids = new HashSet<uint>();
        foreach (var p in procs) { try { pids.Add((uint)p.Id); } catch { } finally { p.Dispose(); } }
        return ParseTitles(WindowTitles(pids));
    }

    /// <summary>Bepaalt de toestand uit de vensternamen van alle Spotify-processen.</summary>
    public static PlayerSnapshot ParseTitles(IEnumerable<string> titles)
    {
        var list = titles.Select(t => t?.Trim() ?? "").Where(t => t.Length > 0).ToList();
        foreach (var t in list)
        {
            var sep = t.IndexOf(" - ", StringComparison.Ordinal);
            if (sep <= 0 || sep + 3 >= t.Length) continue;
            if (IsIdleTitle(t)) continue;
            return new PlayerSnapshot(PlayerState.Playing, new NowPlaying(t[..sep].Trim(), t[(sep + 3)..].Trim()));
        }
        // Venster gevonden zonder "Artiest - Titel": gepauzeerd, reclame of nog aan het opstarten.
        return new PlayerSnapshot(PlayerState.Paused, null);
    }

    private static bool IsIdleTitle(string t) =>
        t.StartsWith("Spotify", StringComparison.OrdinalIgnoreCase) && !t.Contains(" - ", StringComparison.Ordinal)
        || t.Equals("Advertisement", StringComparison.OrdinalIgnoreCase);

    // ---- Win32: alle topniveau-vensters van de Spotify-processen (ook verborgen, bijv. in het systeemvak) ----

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);

    private static List<string> WindowTitles(HashSet<uint> pids)
    {
        var titles = new List<string>();
        try
        {
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (!pids.Contains(pid)) return true;
                var len = GetWindowTextLength(hwnd);
                if (len <= 0) return true;
                var sb = new StringBuilder(len + 1);
                GetWindowText(hwnd, sb, sb.Capacity);
                titles.Add(sb.ToString());
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return titles;
    }
}

// =====================================================================================================
// Geluid meten: de piekmeter van het standaard-afspeelapparaat (zelfde meter als in de Windows-volumemixer)
// =====================================================================================================

public interface IAudioLevelSource : IDisposable
{
    /// <summary>Piekniveau 0..1, of null als meten niet lukt.</summary>
    float? ReadPeak();
}

public sealed class WindowsAudioMeter : IAudioLevelSource
{
    private IAudioMeterInformation? _meter;
    private long _nextRefresh;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public string? LastError { get; private set; }

    public float? ReadPeak()
    {
        if (!OperatingSystem.IsWindows()) { LastError = "Alleen beschikbaar op Windows"; return null; }
        try
        {
            // Elke 10 s opnieuw het standaardapparaat ophalen (headset in/uit, ander apparaat gekozen)
            if (_meter == null || _clock.ElapsedMilliseconds >= _nextRefresh)
            {
                Release();
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                try
                {
                    Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out var device));
                    try
                    {
                        var iid = typeof(IAudioMeterInformation).GUID;
                        Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out var obj));
                        _meter = (IAudioMeterInformation)obj;
                    }
                    finally { Marshal.ReleaseComObject(device); }
                }
                finally { Marshal.ReleaseComObject(enumerator); }
                _nextRefresh = _clock.ElapsedMilliseconds + 10_000;
            }
            Marshal.ThrowExceptionForHR(_meter!.GetPeakValue(out var peak));
            LastError = null;
            return Math.Clamp(peak, 0f, 1f);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Release();
            _nextRefresh = _clock.ElapsedMilliseconds + 3_000;
            return null;
        }
    }

    private void Release()
    {
        if (_meter != null) { try { Marshal.ReleaseComObject(_meter); } catch { } _meter = null; }
    }

    public void Dispose() => Release();

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}

// =====================================================================================================
// Beatdetectie op het piekniveau
// =====================================================================================================

/// <summary>
/// Herkent klappen (kicks) in het geluidsniveau: een snelle stijging ten opzichte van het gemiddelde van de
/// laatste ±0,7 s. Past zich aan het volume aan, dus werkt even goed als Spotify zacht of hard staat.
/// </summary>
public sealed class BeatDetector
{
    private double _norm = 0.05, _fast, _slow, _prevFast, _level, _short, _long = -1;
    private long _lastBeat = long.MinValue / 2, _lastSound = long.MinValue / 2, _lastDrop = long.MinValue / 2, _start = -1;

    /// <summary>0..1: hoe gevoelig (hoger = meer beats).</summary>
    public double Sensitivity { get; set; } = 0.5;
    /// <summary>Kortste tijd tussen twee beats (ms).</summary>
    public int MinIntervalMs { get; set; } = 250;
    /// <summary>Huidige luidheid 0..1 (genormaliseerd op het volume van de laatste tijd).</summary>
    public double Level => _level;
    public long LastBeatMs => _lastBeat;
    /// <summary>Gemiddelde luidheid van de laatste ±0,5 s.</summary>
    public double ShortEnergy => _short;
    /// <summary>Gemiddelde luidheid van de laatste ±6 s (het ‘normale’ niveau van dit stuk).</summary>
    public double LongEnergy => Math.Max(0, _long);
    /// <summary>Het nummer is nu minstens zo hard als normaal: tijd voor flashbangs en zwarte stukjes.</summary>
    public bool Energetic => _short > 0.15 && _short >= LongEnergy * 0.85;

    /// <summary>Plotseling veel harder dan de seconden ervoor = de drop. Hooguit eens per 8 s.</summary>
    public bool CheckDrop(long nowMs)
    {
        if (_start < 0 || nowMs - _start < 4000 || nowMs - _lastDrop < 8000) return false;
        if (_short > 0.5 && _short > LongEnergy * 1.4) { _lastDrop = nowMs; return true; }
        return false;
    }
    /// <summary>Geen geluid van betekenis in de laatste 1,5 s.</summary>
    public bool IsSilent(long nowMs) => nowMs - _lastSound > 1500;

    public bool Push(double peak, long nowMs)
    {
        peak = Math.Clamp(peak, 0, 1);
        if (peak > 0.004) _lastSound = nowMs;
        // Langzaam dalend maximum = referentie voor het volume
        _norm = Math.Max(Math.Max(peak, 0.02), _norm * 0.9993);
        var x = peak / _norm;
        _prevFast = _fast;
        _fast = _fast * 0.45 + x * 0.55;
        _slow = _slow * 0.965 + x * 0.035;
        _level = _level * 0.8 + x * 0.2;
        if (_start < 0) _start = nowMs;
        _short = _short * 0.96 + x * 0.04;                       // ±0,5 s bij een meting per 20 ms
        // ±6 s; de eerste seconde volgt hij het korte gemiddelde (anders telt de allereerste meting te zwaar)
        _long = nowMs - _start < 1000 ? _short : _long * 0.9967 + x * 0.0033;
        var threshold = 1.65 - Math.Clamp(Sensitivity, 0, 1) * 0.5;   // 1,15 (gevoelig) … 1,65 (alleen harde klappen)
        var beat = _fast > _slow * threshold && _fast > 0.3 && _fast > _prevFast && nowMs - _lastBeat >= MinIntervalMs && !IsSilent(nowMs);
        if (beat) _lastBeat = nowMs;
        return beat;
    }
}

// =====================================================================================================
// Tempo volgen: voorspelt de volgende beat, zodat het licht óp de beat komt in plaats van erna
// =====================================================================================================

public sealed class TempoTracker
{
    private readonly List<long> _beats = new();
    private long _lastBeat = long.MinValue / 2;

    public double IntervalMs { get; private set; }
    public bool Locked { get; private set; }
    public long Anchor { get; private set; }
    public double Bpm => Locked && IntervalMs > 0 ? 60000 / IntervalMs : 0;

    public void AddBeat(long t)
    {
        _lastBeat = t;
        _beats.Add(t);
        _beats.RemoveAll(b => t - b > 8000);
        var intervals = new List<double>();
        for (var i = 1; i < _beats.Count; i++)
        {
            double x = _beats[i] - _beats[i - 1];
            if (x < 280) continue;                                  // tussenklap (hi-hat): geen beat-afstand
            while (x > 750 && x / 2 >= 280) x /= 2;                // overgeslagen beat: terugrekenen
            intervals.Add(x);
        }
        if (intervals.Count < 4) { Locked = false; Anchor = t; return; }
        var sorted = intervals.OrderBy(x => x).ToList();
        var median = sorted[sorted.Count / 2];
        var inliers = intervals.Where(x => Math.Abs(x - median) < median * 0.1).ToList();
        var wasLocked = Locked;
        Locked = inliers.Count >= Math.Max(4, intervals.Count * 0.6);
        if (!Locked) { Anchor = t; return; }
        IntervalMs = inliers.Average();
        if (!wasLocked) { Anchor = t; return; }
        // Fase bijsturen: alleen als deze klap dicht bij het verwachte rooster ligt (anders is het een tussenklap)
        var n = Math.Round((t - Anchor) / IntervalMs);
        var grid = Anchor + n * IntervalMs;
        var err = t - grid;
        if (Math.Abs(err) < IntervalMs * 0.2) Anchor = (long)(grid + err * 0.5);
    }

    /// <summary>Lang geen beat (rustig stuk, pauze): tempo loslaten.</summary>
    public void Update(long now)
    {
        if (Locked && now - _lastBeat > Math.Max(2500, IntervalMs * 5)) { Locked = false; _beats.Clear(); }
    }

    /// <summary>Eerste voorspelde beat op of na tijdstip t (-1 als het tempo nog niet bekend is).</summary>
    public long NextBeatAtOrAfter(long t)
    {
        if (!Locked || IntervalMs <= 0) return -1;
        var n = Math.Ceiling((t - Anchor) / IntervalMs);
        return (long)Math.Round(Anchor + n * IntervalMs);
    }
}

/// <summary>
/// Herkent rave / uptempo / hardstyle ("zaag") aan het geluid: hoog tempo (≥ 128 BPM), een vaste kick op
/// (bijna) elke beat en genoeg energie. Na 4 s achter elkaar = rave. Blijft rave tot het nummer wisselt of er
/// 30 s lang geen rave-kenmerken meer zijn (een break in een raveplaat zet hem dus niet uit).
/// </summary>
public sealed class RaveDetector
{
    private readonly List<long> _beats = new();
    private long _okSince = -1, _badSince = -1;

    public bool IsRave { get; private set; }
    /// <summary>Hoeveel van de verwachte beats er de laatste 6 s echt een kick hadden (0..1).</summary>
    public double KickRatio { get; private set; }

    public void Reset() { _beats.Clear(); _okSince = _badSince = -1; IsRave = false; KickRatio = 0; }

    public void Update(long now, bool beat, TempoTracker tempo, BeatDetector detector)
    {
        if (beat) _beats.Add(now);
        _beats.RemoveAll(b => now - b > 6000);
        KickRatio = tempo.Locked && tempo.IntervalMs > 0 ? Math.Min(1, _beats.Count / (6000.0 / tempo.IntervalMs)) : 0;
        var ok = tempo.Locked && tempo.Bpm >= 128 && KickRatio >= 0.6 && detector.LongEnergy > 0.15;
        if (ok)
        {
            _badSince = -1;
            if (_okSince < 0) _okSince = now;
            if (now - _okSince >= 4000) IsRave = true;
        }
        else
        {
            _okSince = -1;
            if (_badSince < 0) _badSince = now;
            if (IsRave && now - _badSince >= 30000) IsRave = false;
        }
    }

    /// <summary>Kicks in de laatste <paramref name="beats"/> beats (voor: zitten we in een drop?).</summary>
    public int RecentKicks(long now, double intervalMs, int beats) => _beats.Count(b => now - b <= beats * intervalMs + 60);
}

/// <summary>
/// Vertraging meten: de strip flitst in een vast ritme en jij tikt mee op het moment dat je hem ZIET.
/// Het verschil tussen versturen en jouw tik = hoe ver de strip achterloopt.
/// </summary>
public static class LatencyCalibration
{
    public static int? Compute(IReadOnlyList<long> flashes, IReadOnlyList<long> taps)
    {
        var offsets = new List<long>();
        foreach (var tap in taps)
        {
            var near = flashes.Select(f => tap - f).Where(d => d is >= -250 and <= 900).OrderBy(d => Math.Abs(d)).ToList();
            if (near.Count > 0) offsets.Add(near[0]);
        }
        if (offsets.Count < 5) return null;
        offsets.Sort();
        var median = offsets[offsets.Count / 2];
        return (int)Math.Clamp(Math.Round(median / 10.0) * 10, 0, 600);
    }
}

// =====================================================================================================
// Kleuren per nummer
// =====================================================================================================

/// <summary>
/// Elk nummer krijgt zijn eigen kleurvolgorde met alle 8 felle kleuren (zelfde nummer = altijd hetzelfde).
/// De volgorde is een kring waarin elke stap sterk verschilt (≥ 100° op de kleurcirkel), ook van de
/// laatste terug naar de eerste. Paren (1+2, 3+4, 5+6, 7+8) vormen de scènes.
/// </summary>
public static class SpotifyPalette
{
    /// <summary>Gesorteerd op kleurtoon: rood, oranje, geel, groen, cyaan, blauw, paars, magenta.</summary>
    public static readonly string[] Colors =
        { "#FF0000", "#FF7A00", "#FFE600", "#00FF3C", "#00E5FF", "#0047FF", "#8A00FF", "#FF00C8" };

    /// <summary>Aantal verschillende volgordes (8 startkleuren × 2 richtingen).</summary>
    public static int CycleCount => Colors.Length * 2;

    public static string[] For(string key)
    {
        // FNV-1a: stabiel tussen app-starts (string.GetHashCode is dat niet)
        uint h = 2166136261;
        foreach (var ch in key ?? "") { h ^= ch; h *= 16777619; }
        h ^= h >> 15; h *= 0x2C1B3C6D; h ^= h >> 12;   // goed mengen: lijkende titels krijgen toch een andere volgorde
        var n = Colors.Length;
        var start = (int)(h % (uint)n);
        var step = ((h >> 20) & 1) == 0 ? 3 : n - 3;   // 3 stappen verder op de kleurcirkel = altijd groot contrast
        return Enumerable.Range(0, n).Select(i => Colors[(start + i * step) % n]).ToArray();
    }

    public static double HueDistance(string a, string b)
    {
        var d = Math.Abs(Hue(a) - Hue(b)) % 360;
        return Math.Min(d, 360 - d);
    }

    public static double Hue(string hex)
    {
        ColorUtil.TryParseHex(hex, out var r, out var g, out var b);
        double R = r / 255.0, G = g / 255.0, B = b / 255.0, max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B)), d = max - min;
        if (d == 0) return 0;
        var h = max == R ? 60 * ((G - B) / d % 6) : max == G ? 60 * ((B - R) / d + 2) : 60 * ((R - G) / d + 4);
        return h < 0 ? h + 360 : h;
    }
}
