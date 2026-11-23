using System.Windows;
using System.Windows.Media;
using StreamLightControl2.Core.Services;

namespace StreamLightControl2;

/// <summary>
/// Zet de kleuren van een thema in de app. Gebeurt bij het opstarten, vóór er een venster is: alle stijlen
/// pakken dan de nieuwe kleuren. Een ander thema kiezen = app herstarten.
/// </summary>
public static class ThemeApplier
{
    public static AppTheme Current { get; private set; } = AppThemes.All[0];

    public static void Apply(Application app, AppTheme theme)
    {
        Current = theme;
        // De kleuren staan in Themes/Theme.xaml. Dat bestand zit twee keer in de app (bovenaan én binnen
        // Controls.xaml, waar de stijlen het gebruiken): overal vervangen, en voor de zekerheid ook bovenaan.
        var dicts = new List<ResourceDictionary> { app.Resources };
        void Find(ResourceDictionary d)
        {
            foreach (var m in d.MergedDictionaries)
            {
                if (m.Source?.OriginalString.EndsWith("Theme.xaml", StringComparison.OrdinalIgnoreCase) == true) dicts.Insert(0, m);
                Find(m);
            }
        }
        Find(app.Resources);
        foreach (var key in AppThemes.Keys)
        {
            if (!theme.Colors.TryGetValue(key, out var hex)) continue;
            var color = Parse(hex);
            var brush = new SolidColorBrush(color); brush.Freeze();
            foreach (var d in dicts) { d[key + "Color"] = color; d[key + "Brush"] = brush; }
        }
        var grad = new LinearGradientBrush(Parse(theme.GradientFrom), Parse(theme.GradientTo), new Point(0, 0), new Point(1, 1));
        grad.Freeze();
        var subtitle = theme.Key == "standaard" ? "Control" : "Control · " + theme.Name.Split(' ')[0];
        foreach (var d in dicts) { d["AccentGradient"] = grad; d["BrandSubtitle"] = subtitle; }

    }

    public static Color Parse(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 6) h = "FF" + h;
        var v = Convert.ToUInt32(h, 16);
        return Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}
