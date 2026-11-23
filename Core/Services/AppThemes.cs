namespace StreamLightControl2.Core.Services;

/// <summary>Een kleurthema voor de hele app (alle kleuren in #RRGGBB of #AARRGGBB).</summary>
public sealed record AppTheme(string Key, string Name, IReadOnlyDictionary<string, string> Colors, string GradientFrom, string GradientTo);

/// <summary>
/// Thema's van de app: Standaard (paars/blauw), Kerst, Herfst en Zomer, of automatisch per seizoen.
/// De sleutels zijn de kleurnamen uit Themes/Theme.xaml zonder "Color"/"Brush" (bijv. "Accent" → AccentColor + AccentBrush).
/// </summary>
public static class AppThemes
{
    public const string Auto = "auto";

    /// <summary>Alle kleurnamen die een thema moet hebben.</summary>
    public static readonly string[] Keys =
    {
        "Sidebar", "Bg", "Card", "CardHover", "Elevated", "Input", "Line", "LineStrong",
        "Text", "Text2", "Text3",
        "Accent", "AccentHover", "AccentPressed", "Accent2", "AccentSoft",
        "Success", "SuccessSoft", "Warning", "WarningSoft", "Danger", "DangerSoft", "InfoSoft", "Live", "OnAccent"
    };

    private static Dictionary<string, string> Base(string sidebar, string bg, string card, string cardHover, string elevated, string input,
        string line, string lineStrong, string text, string text2, string text3,
        string accent, string accentHover, string accentPressed, string accent2, string onAccent = "#FFFFFF") => new()
    {
        ["Sidebar"] = sidebar, ["Bg"] = bg, ["Card"] = card, ["CardHover"] = cardHover, ["Elevated"] = elevated, ["Input"] = input,
        ["Line"] = line, ["LineStrong"] = lineStrong, ["Text"] = text, ["Text2"] = text2, ["Text3"] = text3,
        ["Accent"] = accent, ["AccentHover"] = accentHover, ["AccentPressed"] = accentPressed, ["Accent2"] = accent2,
        ["AccentSoft"] = "#26" + accent[1..],
        ["Success"] = "#3FCF8E", ["SuccessSoft"] = "#203FCF8E", ["Warning"] = "#F5A524", ["WarningSoft"] = "#22F5A524",
        ["Danger"] = "#F2556F", ["DangerSoft"] = "#24F2556F", ["InfoSoft"] = "#22" + accent2[1..], ["Live"] = "#FF3B5C",
        ["OnAccent"] = onAccent,   // tekst/vinkje op de accentkleur (donker op lichte accenten)
    };

    public static IReadOnlyList<AppTheme> All { get; } = new[]
    {
        new AppTheme("standaard", "Standaard (paars en blauw)",
            Base("#0D0E13", "#121319", "#191A22", "#1F2029", "#24262F", "#14151B", "#262833", "#353845",
                 "#ECECF1", "#A2A4B4", "#6E7184", "#7B6CF6", "#8D80F9", "#6A5AE6", "#4F8DFF"), "#8B6CF6", "#4F8DFF"),
        // Kerst: dennengroen met kerstrood en goud
        new AppTheme("kerst", "Kerst (dennengroen, rood en goud)",
            Base("#0A1310", "#0E1915", "#14231C", "#1A2B23", "#1F3229", "#0D1712", "#24392E", "#30503F",
                 "#F1F6F2", "#A9BFB2", "#6F887A", "#E5484D", "#EE6468", "#C83B40", "#FFC24B"), "#E5484D", "#FFC24B"),
        // Herfst: warm donkerbruin met oranje en mosterd
        new AppTheme("herfst", "Herfst (bruin, oranje en mosterd)",
            Base("#120E0B", "#17120E", "#211913", "#281F17", "#2E241B", "#150F0B", "#33271D", "#473628",
                 "#F6EFE7", "#C0AE9B", "#86735F", "#E8772E", "#F08C48", "#CB6322", "#D9AE34", "#1C0F05"), "#E8772E", "#B5452A"),
        // Zomer: diepe zee met zonnig geel/oranje en turquoise
        new AppTheme("zomer", "Zomer (zee, zon en turquoise)",
            Base("#09131A", "#0C1920", "#112229", "#162A32", "#1B313A", "#0B171D", "#203842", "#2C4B57",
                 "#EEF8FA", "#A2C3CB", "#698B95", "#FF9F1C", "#FFB247", "#E38A0E", "#2EC4D6", "#1A1203"), "#FF7E5F", "#FFB020"),
    };

    /// <summary>Keuzes voor in Instellingen (eerst automatisch).</summary>
    public static IReadOnlyList<(string Key, string Name)> Choices { get; } =
        new[] { (Auto, "Automatisch per seizoen (dec = kerst, sep–nov = herfst, jun–aug = zomer)") }
        .Concat(All.Select(t => (t.Key, t.Name))).ToArray();

    /// <summary>Welk thema hoort bij de instelling (bij "auto" het seizoen van de datum).</summary>
    public static AppTheme Resolve(string? setting, DateTime today)
    {
        var key = (setting ?? "").Trim().ToLowerInvariant();
        if (key == Auto)
            key = today.Month switch { 12 => "kerst", 9 or 10 or 11 => "herfst", 6 or 7 or 8 => "zomer", _ => "standaard" };
        return All.FirstOrDefault(t => t.Key == key) ?? All[0];
    }
}
