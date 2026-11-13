namespace StreamLightControl2.Core.Services;

/// <summary>Zones van één strip: hoeveel er zijn, welke op stream te zien zijn en via welke methode.</summary>
public sealed record ZoneSetup(int Count, IReadOnlyList<int> Visible, bool Segment);

/// <summary>
/// Festival-effecten op de stukjes strip die op stream te zien zijn (zoals een hardstyle-tent: beams om-en-om,
/// een scanner, licht vanuit het midden en contrasterende washes in magenta, rood en paars). De rest van de strip
/// houdt de kleur van de show. Gezellige shows krijgen een zacht kleurverloop in plaats van effecten.
/// Zone-nummers zijn 1-gebaseerd (zone 1 = begin bij de controller).
/// </summary>
public static class ZoneEffects
{
    public const int Patterns = 4;

    // Gemeten uit het voorbeeldfilmpje: magenta/roze 42%, rood 26%, paars 15%, wit 9%, blauw en amber 4%
    private static readonly (byte r, byte g, byte b)[] Festival =
    {
        (255, 20, 150),   // magenta
        (255, 16, 32),    // rood
        (140, 40, 255),   // paars
        (255, 90, 210),   // roze
        (40, 90, 255),    // blauw
        (255, 150, 30),   // amber
    };
    private static readonly (byte r, byte g, byte b) Beam = (255, 215, 240);   // wit-roze lichtbundel

    /// <summary>Hoeveel beelden een stap krijgt (bewegende patronen), afhankelijk van duur en snelheid van de strip.</summary>
    public static int FramesFor(int pattern, int stepMs, int minFrameMs, int visibleCount, bool cozy)
    {
        if (cozy || pattern is 3) return 1;
        var max = pattern == 0 ? 2 : Math.Max(2, visibleCount);
        return Math.Clamp(stepMs / Math.Max(1, minFrameMs), 1, max);
    }

    /// <summary>Kleur per zone voor één beeld van een stap.</summary>
    public static List<(byte r, byte g, byte b)> Render(ZoneSetup z, (byte r, byte g, byte b) baseColor, int pattern, int frame, int frames, bool cozy)
    {
        var zones = Enumerable.Repeat(baseColor, z.Count).ToList();
        var vis = z.Visible.Where(v => v >= 1 && v <= z.Count).ToList();
        var n = vis.Count;
        if (n == 0) return zones;
        var isBlack = baseColor is (0, 0, 0);
        var isWhite = baseColor.r > 235 && baseColor.g > 235 && baseColor.b > 235;
        if (isBlack || isWhite) return zones;             // uit of witte klap: hele strip hetzelfde

        if (cozy)
        {
            // Zacht verloop over het zichtbare stuk: van de showkleur naar een warme buurkleur
            var warm = ShiftHue(baseColor, 40);
            for (var k = 0; k < n; k++)
                zones[vis[k] - 1] = Lerp(baseColor, warm, n == 1 ? 0 : k / (double)(n - 1));
            return zones;
        }

        var partner = Partner(baseColor, pattern);
        var t = frames <= 1 ? 1.0 : frame / (double)(frames - 1);
        for (var k = 0; k < n; k++)
        {
            (byte, byte, byte) c;
            switch (pattern % Patterns)
            {
                case 0:   // beams om-en-om, wisselen halverwege de stap
                    c = (k + frame) % 2 == 0 ? baseColor : partner;
                    break;
                case 1:   // scanner: een felle bundel loopt over het zichtbare stuk
                {
                    var pos = t * (n - 1);
                    var d = Math.Abs(k - pos);
                    c = d < 0.75 ? Beam : d < 1.75 ? Lerp(partner, Beam, 0.4) : partner;
                    break;
                }
                case 2:   // vanuit het midden naar buiten
                {
                    var mid = (n - 1) / 2.0;
                    var reach = 0.5 + t * Math.Max(0, n / 2.0 - 1.5);   // de buitenste zones blijven contrastkleur
                    c = Math.Abs(k - mid) <= reach ? baseColor : partner;
                    break;
                }
                default:  // contrasterende wash op het zichtbare stuk
                    c = partner;
                    break;
            }
            zones[vis[k] - 1] = c;
        }
        return zones;
    }

    /// <summary>Festivalkleur die het meest afsteekt tegen de showkleur (wisselt per patroon voor variatie).</summary>
    public static (byte r, byte g, byte b) Partner((byte r, byte g, byte b) c, int pattern)
    {
        var h = Hue(c);
        var ranked = Festival.Select((f, i) => (f, d: HueDist(h, Hue(f)), i))
            .Where(x => x.d >= 60).OrderByDescending(x => x.i < 3 ? 1 : 0).ThenByDescending(x => x.d).ToList();
        if (ranked.Count == 0) return Festival[0];
        return ranked[Math.Abs(pattern) % Math.Min(2, ranked.Count)].f;
    }

    private static double Hue((byte r, byte g, byte b) c)
    {
        double R = c.r / 255.0, G = c.g / 255.0, B = c.b / 255.0, max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B)), d = max - min;
        if (d == 0) return 0;
        var h = max == R ? 60 * ((G - B) / d % 6) : max == G ? 60 * ((B - R) / d + 2) : 60 * ((R - G) / d + 4);
        return h < 0 ? h + 360 : h;
    }

    private static double HueDist(double a, double b) { var d = Math.Abs(a - b) % 360; return Math.Min(d, 360 - d); }

    private static (byte r, byte g, byte b) Lerp((byte r, byte g, byte b) a, (byte r, byte g, byte b) b, double t) =>
        ((byte)Math.Round(a.r + (b.r - a.r) * t), (byte)Math.Round(a.g + (b.g - a.g) * t), (byte)Math.Round(a.b + (b.b - a.b) * t));

    private static (byte r, byte g, byte b) ShiftHue((byte r, byte g, byte b) c, double deg)
    {
        double R = c.r / 255.0, G = c.g / 255.0, B = c.b / 255.0, max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
        var h = (Hue(c) + deg) % 360; var s = max == 0 ? 0 : (max - min) / max; var v = max;
        var cc = v * s; var x = cc * (1 - Math.Abs(h / 60 % 2 - 1)); var m = v - cc;
        var (r, g, b) = h < 60 ? (cc, x, 0.0) : h < 120 ? (x, cc, 0.0) : h < 180 ? (0.0, cc, x) : h < 240 ? (0.0, x, cc) : h < 300 ? (x, 0.0, cc) : (cc, 0.0, x);
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
