using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using StreamLightControl2.Core;
using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Controls;

/// <summary>Placeholder-tekst voor TextBoxen: ui:Hint.Text="Zoeken…"</summary>
public static class Hint
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(Hint), new PropertyMetadata(""));
    public static string GetText(DependencyObject o) => (string)o.GetValue(TextProperty);
    public static void SetText(DependencyObject o, string value) => o.SetValue(TextProperty, value);
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is Visibility.Collapsed;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is not true;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is not true;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <summary>null/leeg → Collapsed. Parameter "invert" draait dit om.</summary>
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var empty = value == null || value is string s && string.IsNullOrWhiteSpace(s);
        if (p as string == "invert") empty = !empty;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    /// <summary>0 → Collapsed (of omgekeerd met parameter "zero": alleen zichtbaar bij 0).</summary>
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var n = value switch { int i => i, long l => (int)l, ICollection col => col.Count, _ => 0 };
        var zero = p as string == "zero";
        return (n > 0) ^ zero ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class HexToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = new();
    public static SolidColorBrush Get(string? hex)
    {
        hex = ColorUtil.Normalize(hex, "#000000");
        lock (Cache)
        {
            if (Cache.TryGetValue(hex, out var b)) return b;
            ColorUtil.TryParseHex(hex, out var r, out var g, out var bl);
            b = new SolidColorBrush(Color.FromRgb(r, g, bl));
            b.Freeze();
            if (Cache.Count < 4000) Cache[hex] = b;
            return b;
        }
    }
    public object Convert(object value, Type t, object p, CultureInfo c) => Get(value as string);
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class MsToSecondsConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is int ms ? (ms >= 10000 ? $"{ms / 1000.0:0} s" : $"{ms / 1000.0:0.0} s") : "";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class EnumEqualsConverter : IValueConverter
{
    /// <summary>Voor RadioButtons: IsChecked="{Binding Mode, Converter={StaticResource EnumEquals}, ConverterParameter=Cloud}"</summary>
    public object Convert(object value, Type t, object p, CultureInfo c) => value?.ToString() == p?.ToString();
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is true && p != null ? Enum.Parse(t, p.ToString()!) : Binding.DoNothing;
}

/// <summary>Kleur per verwerkingsstatus of foutsoort.</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => Ui.Res(Key(value));
    public static string Key(object value) => value switch
    {
        ProcessingStatus.Played => "SuccessBrush",
        ProcessingStatus.Playing or ProcessingStatus.Queued => "AccentHoverBrush",
        ProcessingStatus.Failed => "DangerBrush",
        ProcessingStatus.Interrupted or ProcessingStatus.Skipped => "WarningBrush",
        ProcessingStatus.Ignored or ProcessingStatus.NoRule or ProcessingStatus.Received => "Text3Brush",
        true => "WarningBrush",
        false => "DangerBrush",
        _ => "Text2Brush"
    };
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class StatusToSoftBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => Ui.Res(value switch
    {
        ProcessingStatus.Played => "SuccessSoftBrush",
        ProcessingStatus.Playing or ProcessingStatus.Queued => "AccentSoftBrush",
        ProcessingStatus.Failed => "DangerSoftBrush",
        ProcessingStatus.Interrupted or ProcessingStatus.Skipped => "WarningSoftBrush",
        true => "WarningSoftBrush",
        false => "DangerSoftBrush",
        _ => "ElevatedBrush"
    });
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class EventTypeToIconConverter : IValueConverter
{
    public static string Glyph(string? type) => type switch
    {
        EventTypes.Follow => "",
        EventTypes.Gift => "",
        EventTypes.Like => "",
        EventTypes.Share => "",
        EventTypes.Comment => "",
        EventTypes.Member => "",
        EventTypes.Subscribe => "",
        _ => ""
    };
    public object Convert(object value, Type t, object p, CultureInfo c) => Glyph(value as string);
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}
