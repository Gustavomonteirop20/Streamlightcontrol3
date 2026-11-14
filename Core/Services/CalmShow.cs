using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Core.Services;

/// <summary>Eén beeld van een rustige show: sectiekleur, welk patroon, welke tik.</summary>
public sealed record CalmFrame(long AtMs, int DurationMs, (byte r, byte g, byte b) Color, int Pattern, int Tick, bool SectionStart, int TicksInSection = 1);

/// <summary>
/// "Rustige shows" (minder knipperen), zoals in het voorbeeldfilmpje: per sectie (±3,5 s) één kleur, en de stukjes
/// strip die op stream te zien zijn bewegen op de beat (om-en-om, vullen, looplicht, vanuit het midden, ademen,
/// willekeurig). Geen witte flitsen, hooguit ±1,5 wissels per seconde. De kleuren komen uit de show zelf, dus elke
/// show blijft anders; de volgorde van de patronen hangt ook af van de show.
/// </summary>
public static class CalmShow
{
    public const int Patterns = 6;
    public const int MinTickMs = 640;        // ≤ ±1,5 wissels per seconde (ruim onder de 3 per seconde die als veilig geldt)
    public const int CozyMinTickMs = 850;
    public const int SectionMs = 3500;

    public static List<CalmFrame> Build(LightShow show, bool cozy)
    {
        var minTick = cozy ? CozyMinTickMs : MinTickMs;
        // 1. Tikken: begin van elke stap, maar nooit sneller dan minTick
        var ticks = new List<long>();
        long t = 0, last = long.MinValue / 2;
        foreach (var st in show.Steps)
        {
            if (t - last >= minTick) { ticks.Add(t); last = t; }
            t += st.DurationMs;
        }
        var total = t;
        if (ticks.Count == 0) ticks.Add(0);

        // 2. Secties van ±3,5 s, op een tik beginnend
        var sections = new List<(long start, long end)>();
        var secStart = ticks[0];
        for (var i = 1; i < ticks.Count; i++)
            if (ticks[i] - secStart >= SectionMs && total - ticks[i] >= 1500) { sections.Add((secStart, ticks[i])); secStart = ticks[i]; }
        sections.Add((secStart, total));

        // 3. Kleur per sectie: de kleur die in die tijd het langst in de show staat (geen wit/zwart)
        var seed = StableHash(show.PresetKey.Length > 0 ? show.PresetKey : show.Id);
        var order = Enumerable.Range(0, Patterns).OrderBy(p => StableHash(seed + ":" + p)).ToArray();
        if (cozy) order = new[] { 1, 4, 1, 4 };   // gezellig: alleen vullen en ademen
        var frames = new List<CalmFrame>();
        (byte, byte, byte)? prev = null;
        for (var s = 0; s < sections.Count; s++)
        {
            var (a, b) = sections[s];
            var color = Dominant(show, a, b, prev) ?? prev ?? (180, 60, 255);
            prev = color;
            var pattern = order[s % order.Length];
            var secTicks = ticks.Where(x => x >= a && x < b).ToList();
            if (secTicks.Count == 0 || secTicks[0] != a) secTicks.Insert(0, a);
            for (var k = 0; k < secTicks.Count; k++)
            {
                var end = k + 1 < secTicks.Count ? secTicks[k + 1] : b;
                frames.Add(new CalmFrame(secTicks[k], (int)Math.Max(1, end - secTicks[k]), color, pattern, k, k == 0, secTicks.Count));
            }
        }
        return frames;
    }

    private static (byte, byte, byte)? Dominant(LightShow show, long from, long to, (byte, byte, byte)? avoid)
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
        var ranked = weight.OrderByDescending(x => x.Value).Select(x => x.Key).ToList();
        if (ranked.Count == 0) return null;
        return avoid != null && ranked[0] == avoid && ranked.Count > 1 ? ranked[1] : ranked[0];
    }

    /// <summary>Kleur per zone voor één tik. Zichtbare zones bewegen, de rest is een rustige gloed in de sectiekleur.</summary>
    public static List<(byte r, byte g, byte b)> Render(ZoneSetup z, CalmFrame f, bool cozy)
    {
        var glow = Scale(f.Color, cozy ? 0.55 : 0.35);
        var zones = Enumerable.Repeat(glow, z.Count).ToList();
        var vis = z.Visible.Where(v => v >= 1 && v <= z.Count).ToList();
        var n = vis.Count;
        if (n == 0) return zones;
        var on = f.Color;
        var off = Scale(f.Color, cozy ? 0.35 : 0.06);
        var k = f.Tick;
        var T = Math.Max(1, f.TicksInSection);
        // Voortgang door de sectie (0 → 1): vullen, looplicht en midden gaan precies één keer over het hele stuk
        var prog = T <= 1 ? 1.0 : k / (double)(T - 1);
        for (var i = 0; i < n; i++)
        {
            bool lit;
            if (f.SectionStart && !cozy) lit = true;                         // nieuwe sectie: alles aan
            else switch (f.Pattern)
            {
                case 0: lit = (i + k) % 2 == 0; break;                         // om-en-om
                case 1: lit = i < Math.Max(1, (int)Math.Ceiling(prog * n)); break;                    // vullen van links naar rechts
                case 2: { var p = prog * (n - 1); lit = Math.Abs(i - p) <= 1.5; break; }               // looplicht (blok van ±3)
                case 3: { var mid = (n - 1) / 2.0; lit = Math.Abs(i - mid) <= 0.5 + prog * (n / 2.0 - 0.5); break; } // vanuit het midden
                case 4: lit = true; break;                                     // ademen (helderheid hieronder)
                default: lit = StableHash($"{k}:{i}") % 2 == 0; break;         // willekeurig de helft
            }
            var c = lit ? on : off;
            if (f.Pattern == 4 && !f.SectionStart) c = k % 2 == 0 ? on : Scale(on, cozy ? 0.6 : 0.45);
            zones[vis[i] - 1] = c;
        }
        return zones;
    }

    /// <summary>Wissels per seconde (voor tests en de instellingenpagina).</summary>
    public static double ChangesPerSecond(IReadOnlyList<CalmFrame> frames)
    {
        if (frames.Count < 2) return 0;
        var span = (frames[^1].AtMs + frames[^1].DurationMs - frames[0].AtMs) / 1000.0;
        return span <= 0 ? 0 : frames.Count / span;
    }

    private static (byte r, byte g, byte b) Scale((byte r, byte g, byte b) c, double f) =>
        ((byte)Math.Round(c.r * f), (byte)Math.Round(c.g * f), (byte)Math.Round(c.b * f));

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
