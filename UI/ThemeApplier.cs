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
        // De kleuren staan in Themes/Theme.xaml; daar vervangen (en voor de zekerheid ook bovenaan in de app)
        var dicts = new List<ResourceDictionary> { app.Resources };
        var themeDict = app.Resources.MergedDictionaries.FirstOrDefault(d => d.Source?.OriginalString.EndsWith("Theme.xaml", StringComparison.OrdinalIgnoreCase) == true);
        if (themeDict != null) dicts.Insert(0, themeDict);
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

        // De stijlen (Controls.xaml) hebben bij het laden van de app de oude kleuren al vastgelegd:
        // opnieuw laden, zodat knoppen, schakelaars enz. de kleuren van het thema pakken.
        var merged = app.Resources.MergedDictionaries;
        var controls = merged.FirstOrDefault(d => d.Source?.OriginalString.EndsWith("Controls.xaml", StringComparison.OrdinalIgnoreCase) == true);
        if (controls != null)
        {
            var idx = merged.IndexOf(controls);
            merged.Remove(controls);
            merged.Insert(idx, new ResourceDictionary { Source = new Uri("pack://application:,,,/StreamLightControl;component/Themes/Controls.xaml", UriKind.Absolute) });
        }
    }

    public static Color Parse(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 6) h = "FF" + h;
        var v = Convert.ToUInt32(h, 16);
        return Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}
