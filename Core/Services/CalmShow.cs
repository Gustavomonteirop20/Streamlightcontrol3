using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Core.Services;

/// <summary>Eén beeld van een show zonder geknipper.</summary>
/// <param name="Energy">0 = rustig stuk, 1 = normaal, 2 = drop.</param>
/// <param name="Impact">Eerste beat van een drop: de hele strip één beat vol aan.</param>
/// <param name="Build">Opbouw naar een drop: de zones vullen en worden feller tot de drop.</param>
public sealed record CalmFrame(long AtMs, int DurationMs, (byte r, byte g, byte b) Color, int Pattern, int Tick, bool SectionStart,
    int TicksInSection = 1, (byte r, byte g, byte b) Accent = default, int Energy = 1, bool Impact = false, bool Build = false, bool OnKick = false);

/// <summary>
/// Lichtshow zonder geknipper, opgebouwd als een echte show: rustige stukken ademen en vullen langzaam (om de 2 beats),
/// vóór een drop bouwen de zones op tot ze precies op de drop vol en fel staan, op de drop gaat de hele strip één beat
/// vol aan en daarna wordt het snel en tweekleurig (pingpong-scanner met staart, randen naar het midden, stapelen,
/// sparkles). De tikken liggen op de echte kicks uit de audio (de witte flitsen van de festival-versie zijn op de kicks
/// gemeten) met de beat-grid daartussen. Geen wit, nooit 3 of meer wissels per seconde. Per sectie (±3,5 s) één
/// hoofdkleur uit de show zelf plus een accentkleur; elke show een eigen volgorde.
/// </summary>
public static class CalmShow
{
    public const int Patterns = 11;
    public const int MinTickMs = 360;        // elke beat tot ±165 BPM; nooit 3 of meer wissels per seconde
    public const int CozyMinTickMs = 850;
    public const int SectionMs = 3500;

    private static readonly int[] CalmSet = { 4, 1 };
    private static readonly int[] NormalSet = { 0, 2, 3, 1, 5 };
    private static readonly int[] DropSet = { 6, 7, 8, 9, 5, 2 };
    public const int BuildPattern = 10;

    public static List<CalmFrame> Build(LightShow show, bool cozy)
    {
        var minTick = cozy ? CozyMinTickMs : MinTickMs;
        var total = show.Steps.Sum(s => (long)s.DurationMs);
        var song = SongShows.All.FirstOrDefault(x => x.Key == show.PresetKey);

        // 1. Kicks: de witte flitsen van de festival-versie liggen op de gemeten kicks
        var kicks = new List<long>();
        var festival = song != null ? Presets.Create(song.Key, fast: true) : show;
        if (Math.Abs(festival.Steps.Sum(s => (long)s.DurationMs) - total) > 100) festival = show;
        {
            long t = 0;
            foreach (var st in festival.Steps)
            {
                if (st.Kind == StepKind.Color && st.ColorHex == "#FFFFFF") kicks.Add(t);
                t += st.DurationMs;
            }
        }

        // 2. Tik-kandidaten: kicks, begin van elke stap, en de beat-grid binnen lange stappen
        var bpm = song?.Bpm ?? 0;
        var beat = bpm > 0 ? 60000.0 / bpm : 0;
        while (beat > 0 && beat < minTick) beat *= 2;      // te snel: om de 2 beats
        var candidates = new SortedSet<long>(kicks);
        {
            long t = 0;
            foreach (var st in show.Steps)
            {
                candidates.Add(t);
                if (beat > 0)
                    for (var b = t + beat; b < t + st.DurationMs - minTick * 0.5; b += beat) candidates.Add((long)Math.Round(b));
                t += st.DurationMs;
            }
        }
        // Eerst de kicks (die zijn exact), dan de rest waar het past
        var kickSet = new HashSet<long>(kicks);
        var chosen = new SortedSet<long>();
        foreach (var k in kicks.Where(k => k < total))
            if (Fits(chosen, k, minTick)) chosen.Add(k);
        foreach (var c in candidates.Where(c => c < total && !kickSet.Contains(c)))
            if (Fits(chosen, c, minTick)) chosen.Add(c);
        if (chosen.Count == 0 || chosen.Min > 0 && chosen.Min >= minTick) chosen.Add(0);
        var ticks = chosen.ToList();

        // 3. Secties van ±3,5 s, op een tik beginnend
        var sections = new List<(long start, long end)>();
        var secStart = ticks[0];
        for (var i = 1; i < ticks.Count; i++)
            if (ticks[i] - secStart >= SectionMs && total - ticks[i] >= 1500) { sections.Add((secStart, ticks[i])); secStart = ticks[i]; }
        sections.Add((secStart, total));

        // 4. Energie per sectie: kicks per seconde in de festival-versie
        var energy = sections.Select(s =>
        {
            if (song == null || kicks.Count == 0) return 1;
            var rate = kicks.Count(k => k >= s.start && k < s.end) / Math.Max(0.5, (s.end - s.start) / 1000.0);
            return rate >= 1.4 ? 2 : rate >= 0.4 ? 1 : 0;
        }).ToArray();
        if (cozy) energy = energy.Select(_ => 0).ToArray();

        // 5. Beelden
        var seed = StableHash(show.PresetKey.Length > 0 ? show.PresetKey : show.Id);
        int[] Order(int[] set, int salt) => set.OrderBy(p => StableHash($"{seed}:{salt}:{p}")).ToArray();
        var orders = new[] { Order(CalmSet, 0), Order(NormalSet, 1), Order(DropSet, 2) };
        var used = new int[3];
        var frames = new List<CalmFrame>();
        (byte, byte, byte)? prev = null;
        for (var s = 0; s < sections.Count; s++)
        {
            var (a, b) = sections[s];
            var e = energy[s];
            var color = Dominant(show, a, b, prev) ?? prev ?? (180, 60, 255);
            prev = color;
            var accent = Accent(show, a, b, color);
            var isBuild = !cozy && e < 2 && s + 1 < sections.Count && energy[s + 1] == 2;
            var impact = !cozy && e == 2 && s > 0 && energy[s - 1] < 2;
            var pattern = cozy ? new[] { 1, 4 }[s % 2] : isBuild ? BuildPattern : orders[e][used[e]++ % orders[e].Length];
            var secTicks = ticks.Where(x => x >= a && x < b).ToList();
            if (secTicks.Count == 0 || secTicks[0] != a) secTicks.Insert(0, a);
            // Rustige stukken: om de 2 beats (contrast met de drop)
            if (e == 0 && !cozy && !isBuild) secTicks = secTicks.Where((_, i) => i % 2 == 0).ToList();
            for (var k = 0; k < secTicks.Count; k++)
            {
                var end = k + 1 < secTicks.Count ? secTicks[k + 1] : b;
                frames.Add(new CalmFrame(secTicks[k], (int)Math.Max(1, end - secTicks[k]), color, pattern, k, k == 0, secTicks.Count,
                    accent, e, impact && k == 0, isBuild, kickSet.Contains(secTicks[k])));
            }
        }
        return frames;
    }

    private static bool Fits(SortedSet<long> set, long t, int min)
    {
        var lower = set.GetViewBetween(long.MinValue, t);
        var upper = set.GetViewBetween(t, long.MaxValue);
        return (lower.Count == 0 || t - lower.Max >= min) && (upper.Count == 0 || upper.Min - t >= min);
    }

    private static Dictionary<(byte, byte, byte), long> Weights(LightShow show, long from, long to)
    {
        var weight = new Dictionary<(byte, byte, byte), long>();
        long t = 0;
        foreach (var st in show.Steps)
        {
            var s = t; var e = t + st.DurationMs; t = e;
            if (e <= from || s >= to || st.Kind != StepKind.Color) continue;
            var c = ColorUtil.ParseHex(st.ColorHex);
            if (c is (0, 0, 0) || (c.r > 235 && c.g > 235 && c.b > 235)) continue;
            weight[c] = weight.GetValueOrDefault(c) + Math.Min(e, to) - Math.Max(s, from);
        }
        return weight;
    }

    private static (byte, byte, byte)? Dominant(LightShow show, long from, long to, (byte, byte, byte)? avoid)
    {
        var ranked = Weights(show, from, to).OrderByDescending(x => x.Value).Select(x => x.Key).ToList();
        if (ranked.Count == 0) return null;
        return avoid != null && ranked[0] == avoid && ranked.Count > 1 ? ranked[1] : ranked[0];
    }

    /// <summary>Accentkleur: de volgende kleur uit de show die duidelijk anders is (≥ 90°), anders 150° verder op de kleurcirkel.</summary>
    private static (byte, byte, byte) Accent(LightShow show, long from, long to, (byte r, byte g, byte b) main)
    {
        var h = Hue(main);
        foreach (var c in Weights(show, from, to).OrderByDescending(x => x.Value).Select(x => x.Key))
            if (HueDist(h, Hue(c)) >= 90) return c;
        return FromHue((h + 150) % 360);
    }

    /// <summary>Kleur per zone voor één tik. Zichtbare zones bewegen; de rest is een gloed die meegroeit met de energie.</summary>
    public static List<(byte r, byte g, byte b)> Render(ZoneSetup z, CalmFrame f, bool cozy)
    {
        var A = f.Color;
        var B = f.Accent == default ? A : f.Accent;
        var k = f.Tick;
        var T = Math.Max(1, f.TicksInSection);
        var prog = T <= 1 ? 1.0 : k / (double)(T - 1);
        double glowF = cozy ? 0.55 : f.Impact ? 1.0 : f.Build ? 0.2 + 0.3 * prog : f.Energy switch { 0 => 0.22, 1 => 0.32, _ => 0.5 };
        var zones = Enumerable.Repeat(Scale(A, glowF), z.Count).ToList();
        var vis = z.Visible.Where(v => v >= 1 && v <= z.Count).ToList();
        var n = vis.Count;
        if (n == 0) return zones;
        var off = Scale(A, cozy ? 0.35 : f.Energy == 0 ? 0.08 : 0.04);
        var t1 = Scale(A, 0.45); var t2 = Scale(A, 0.15);
        var tb1 = Scale(B, 0.5); var tb2 = Scale(B, 0.18);

        for (var i = 0; i < n; i++)
        {
            (byte, byte, byte) c;
            if (f.Impact) c = A;                                                     // drop: alles vol
            else if (f.SectionStart && !cozy && !f.Build && f.Energy >= 1) c = A;   // nieuwe sectie: alles aan
            else switch (f.Pattern)
            {
                case 0: c = (i + k) % 2 == 0 ? A : off; break;                                         // om-en-om
                case 1: c = i < Math.Max(1, (int)Math.Ceiling(prog * n)) ? A : off; break;            // vullen
                case 2: { var p = prog * (n - 1); var d = i - p;                                      // looplicht met staart
                          c = Math.Abs(d) <= 0.5 ? A : d < 0 && d >= -1.5 ? t1 : d < 0 && d >= -2.5 ? t2 : off; break; }
                case 3: { var mid = (n - 1) / 2.0; c = Math.Abs(i - mid) <= 0.5 + prog * (n / 2.0 - 0.5) ? A : off; break; } // midden → buiten
                case 4: c = k % 2 == 0 ? A : Scale(A, cozy ? 0.6 : 0.45); break;                     // ademen
                case 5: { var h = StableHash($"{f.AtMs}:{i}") % 10; c = h < 3 ? A : h < 4 && f.Energy == 2 ? B : off; break; } // sparkle
                case 6: c = (i + k) % 2 == 0 ? A : B; break;                                         // twee kleuren om-en-om
                case 7: { var period = Math.Max(2, 2 * (n - 1)); var step = Math.Max(1, (int)Math.Round(period / (double)Math.Max(2, T)));
                          var raw = k * step % period; var p = raw <= n - 1 ? raw : period - raw;       // pingpong-scanner
                          var dir = raw <= n - 1 ? 1 : -1; var d = (i - p) * dir;
                          c = d == 0 ? A : d == -1 ? tb1 : d == -2 ? tb2 : off; break; }
                case 8: { var edge = Math.Min(i, n - 1 - i); c = edge <= prog * ((n - 1) / 2.0) ? (edge % 2 == 0 ? A : B) : off; break; } // randen → midden
                case 9: { var filled = (int)Math.Ceiling(prog * n); c = i < filled ? (i % 2 == 0 ? A : B) : off; break; } // stapelen
                case BuildPattern:                                                                     // opbouw naar de drop
                {
                    var lit = Math.Max(1, (int)Math.Ceiling(prog * n));
                    c = i < lit ? Scale(A, 0.35 + 0.65 * prog) : off; break;
                }
                default: c = A; break;
            }
            zones[vis[i] - 1] = c;
        }
        return zones;
    }

    /// <summary>Wissels per seconde.</summary>
    public static double ChangesPerSecond(IReadOnlyList<CalmFrame> frames)
    {
        if (frames.Count < 2) return 0;
        var span = (frames[^1].AtMs + frames[^1].DurationMs - frames[0].AtMs) / 1000.0;
        return span <= 0 ? 0 : frames.Count / span;
    }

    private static (byte r, byte g, byte b) Scale((byte r, byte g, byte b) c, double f)
    {
        f = Math.Clamp(f, 0, 1);
        return ((byte)Math.Round(c.r * f), (byte)Math.Round(c.g * f), (byte)Math.Round(c.b * f));
    }

    private static double Hue((byte r, byte g, byte b) c)
    {
        double R = c.r / 255.0, G = c.g / 255.0, B = c.b / 255.0, max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B)), d = max - min;
        if (d == 0) return 0;
        var h = max == R ? 60 * ((G - B) / d % 6) : max == G ? 60 * ((B - R) / d + 2) : 60 * ((R - G) / d + 4);
        return h < 0 ? h + 360 : h;
    }

    private static double HueDist(double a, double b) { var d = Math.Abs(a - b) % 360; return Math.Min(d, 360 - d); }

    private static (byte r, byte g, byte b) FromHue(double h)
    {
        var x = 1 - Math.Abs(h / 60 % 2 - 1);
        var (r, g, b) = h < 60 ? (1.0, x, 0.0) : h < 120 ? (x, 1.0, 0.0) : h < 180 ? (0.0, 1.0, x) : h < 240 ? (0.0, x, 1.0) : h < 300 ? (x, 0.0, 1.0) : (1.0, 0.0, x);
        return ((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            var h = (int)2166136261;
            foreach (var ch in s) h = (h ^ ch) * 16777619;
            return h & 0x7FFFFFFF;
        }
    }
}
