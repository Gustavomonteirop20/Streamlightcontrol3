using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace StreamLightControl2.Core;

/// <summary>Basis voor alle modellen die direct aan de interface gekoppeld worden.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class ColorUtil
{
    public static bool TryParseHex(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s.Select(c => new string(c, 2)));
        if (s.Length != 6) return false;
        try
        {
            r = Convert.ToByte(s[..2], 16);
            g = Convert.ToByte(s.Substring(2, 2), 16);
            b = Convert.ToByte(s.Substring(4, 2), 16);
            return true;
        }
        catch { return false; }
    }

    public static (byte r, byte g, byte b) ParseHex(string hex) =>
        TryParseHex(hex, out var r, out var g, out var b)
            ? (r, g, b)
            : throw new FormatException("Gebruik een kleur zoals #7C6CF6.");

    public static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

    public static string Normalize(string? hex, string fallback = "#7C6CF6") =>
        TryParseHex(hex, out var r, out var g, out var b) ? ToHex(r, g, b) : fallback;

    public static (byte r, byte g, byte b) Lerp((byte r, byte g, byte b) a, (byte r, byte g, byte b) z, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return ((byte)Math.Round(a.r + (z.r - a.r) * t),
                (byte)Math.Round(a.g + (z.g - a.g) * t),
                (byte)Math.Round(a.b + (z.b - a.b) * t));
    }
}
