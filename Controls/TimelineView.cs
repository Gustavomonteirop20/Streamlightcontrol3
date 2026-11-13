using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StreamLightControl2.Core;
using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Controls;

/// <summary>
/// Visuele tijdlijn van een lichtshow. De x-as is echte tijd: elke stap is precies zo breed als hij duurt, zodat de
/// afspeelkop en de tijdschaal altijd kloppen. Klik selecteert een stap, met de muis erboven zie je de details.
/// Breder dan het venster (inzoomen)? Zet hem in een horizontale ScrollViewer en geef hem een Width.
/// </summary>
public sealed class TimelineView : FrameworkElement
{
    public static readonly DependencyProperty ShowProperty = DependencyProperty.Register(nameof(Show), typeof(LightShow), typeof(TimelineView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((TimelineView)d).OnShowChanged(e.OldValue as LightShow, e.NewValue as LightShow)));
    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(TimelineView),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(TimelineView),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public LightShow? Show { get => (LightShow?)GetValue(ShowProperty); set => SetValue(ShowProperty, value); }
    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    /// <summary>0–1 over de hele show (incl. herhalingen), -1 = niet aan het afspelen.</summary>
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    /// <summary>Er is op een stap geklikt (index).</summary>
    public event Action<int>? StepClicked;

    private const double BarTop = 0, RulerHeight = 20;
    private static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x1B)));
    private static readonly Brush OffBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x24, 0x26, 0x2F)));
    private static readonly Brush HoverBrush = Frozen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)));
    private static readonly Pen SelectedPen = FrozenPen(Brushes.White, 2);
    private static readonly Pen SeparatorPen = FrozenPen(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), 1);
    private static readonly Pen PlayheadPen = FrozenPen(new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x5C)), 2);
    private static readonly Pen TickPen = FrozenPen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3C, 0x4A)), 1);
    private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x6E, 0x71, 0x84)));
    private static readonly Brush PlayLabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x5C)));

    private readonly ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };
    private int _hover = -1;

    public TimelineView()
    {
        Height = 92;
        Cursor = Cursors.Hand;
        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 150);
        ToolTipService.SetShowDuration(this, 60000);
        _tip.Content = "Klik op een blok om die stap te bewerken";
    }

    private void OnShowChanged(LightShow? oldShow, LightShow? newShow)
    {
        if (oldShow != null) oldShow.PropertyChanged -= ShowPropertyChanged;
        if (newShow != null) newShow.PropertyChanged += ShowPropertyChanged;
        _hover = -1;
    }

    private void ShowPropertyChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(InvalidateVisual);

    private double BarHeight => Math.Max(24, ActualHeight - RulerHeight);

    /// <summary>Begin (ms) van elke stap binnen één doorloop, plus de totale duur.</summary>
    private static (double[] starts, double total) Times(LightShow show)
    {
        var starts = new double[show.Steps.Count];
        double t = 0;
        for (var i = 0; i < show.Steps.Count; i++) { starts[i] = t; t += show.Steps[i].DurationMs; }
        return (starts, Math.Max(1, t));
    }

    /// <summary>Plek van stap <paramref name="index"/> in pixels (links, breedte) — voor scrollen naar een stap.</summary>
    public (double left, double width) StepBounds(int index)
    {
        var show = Show;
        if (show == null || index < 0 || index >= show.Steps.Count) return (0, 0);
        var (starts, total) = Times(show);
        var w = ActualWidth;
        return (starts[index] / total * w, show.Steps[index].DurationMs / total * w);
    }

    /// <summary>Positie van de afspeelkop in pixels (of -1).</summary>
    public double PlayheadX
    {
        get
        {
            var show = Show;
            if (show == null || Progress < 0 || Progress > 1) return -1;
            var p = show.Repeats > 1 ? Progress * show.Repeats % 1.0 : Progress;
            return p * ActualWidth;
        }
    }

    private int IndexAt(double x)
    {
        var show = Show;
        if (show == null || show.Steps.Count == 0 || ActualWidth <= 0) return -1;
        var (starts, total) = Times(show);
        var t = Math.Clamp(x / ActualWidth, 0, 0.999999) * total;
        var i = Array.BinarySearch(starts, t);
        if (i < 0) i = ~i - 1;
        return Math.Clamp(i, 0, show.Steps.Count - 1);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var show = Show;
        var w = ActualWidth;
        var barH = BarHeight;
        dc.DrawRoundedRectangle(Background, null, new Rect(0, BarTop, w, barH), 8, 8);
        if (show == null || show.Steps.Count == 0 || w <= 0)
        {
            DrawText(dc, "Nog geen stappen – voeg hieronder een kleur toe", new Point(14, barH / 2 - 8), Brushes.Gray, 12);
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var (starts, total) = Times(show);
        dc.PushClip(new RectangleGeometry(new Rect(0, BarTop, w, barH), 8, 8));
        (byte r, byte g, byte b) prev = (0, 0, 0);
        for (var i = 0; i < show.Steps.Count; i++)
        {
            var s = show.Steps[i];
            var x0 = starts[i] / total * w;
            var x1 = (starts[i] + s.DurationMs) / total * w;
            var rc = new Rect(x0, BarTop, Math.Max(0.75, x1 - x0 + 0.5), barH);   // +0,5 px: geen haarlijntjes tussen blokken
            if (s.Kind == StepKind.Off)
            {
                dc.DrawRectangle(OffBrush, null, rc);
                if (rc.Width > 30) DrawText(dc, "UIT", new Point(rc.X + 6, BarTop + 6), LabelBrush, 11, dpi);
                prev = (0, 0, 0);
            }
            else
            {
                var c = ColorUtil.ParseHex(s.ColorHex);
                var shade = 0.35 + 0.65 * s.Brightness / 100.0;
                var shown = Color.FromRgb((byte)(c.r * shade), (byte)(c.g * shade), (byte)(c.b * shade));
                if (s.Flashes > 1)
                {
                    var cycle = rc.Width / s.Flashes;
                    dc.DrawRectangle(OffBrush, null, rc);
                    var fill = Frozen(new SolidColorBrush(shown));
                    for (var f = 0; f < s.Flashes; f++)
                        dc.DrawRectangle(fill, null, new Rect(rc.X + f * cycle, rc.Y, Math.Max(0.75, cycle * 0.55), rc.Height));
                }
                else if (s.TransitionMs > 0 && i > 0)
                {
                    var frac = Math.Clamp(s.TransitionMs / (double)s.DurationMs, 0, 1);
                    var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                    brush.GradientStops.Add(new GradientStop(Color.FromRgb(prev.r, prev.g, prev.b), 0));
                    brush.GradientStops.Add(new GradientStop(shown, frac));
                    brush.GradientStops.Add(new GradientStop(shown, 1));
                    brush.Freeze();
                    dc.DrawRectangle(brush, null, rc);
                }
                else if (s.IsSplit && ColorUtil.TryParseHex(s.ColorHex2, out var r2, out var g2, out var b2))
                {
                    var shown2 = Color.FromRgb((byte)(r2 * shade), (byte)(g2 * shade), (byte)(b2 * shade));
                    dc.DrawRectangle(Frozen(new SolidColorBrush(shown)), null, new Rect(rc.X, rc.Y, rc.Width, rc.Height / 2));
                    dc.DrawRectangle(Frozen(new SolidColorBrush(shown2)), null, new Rect(rc.X, rc.Y + rc.Height / 2, rc.Width, rc.Height / 2));
                }
                else dc.DrawRectangle(Frozen(new SolidColorBrush(shown)), null, rc);
                prev = ((byte)(c.r * shade), (byte)(c.g * shade), (byte)(c.b * shade));
            }
            // scheidingslijntje als er ruimte is, zodat je de blokken ziet
            if (i > 0 && x1 - x0 >= 6) dc.DrawLine(SeparatorPen, new Point(x0 + 0.5, BarTop), new Point(x0 + 0.5, BarTop + barH));
            // duur in het blok als het breed genoeg is
            if (x1 - x0 > 46)
            {
                var label = s.DurationMs >= 1000 ? $"{s.DurationMs / 1000.0:0.#} s" : $"{s.DurationMs} ms";
                DrawText(dc, label, new Point(x0 + 6, BarTop + barH - 18), Frozen(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0))), 10.5, dpi);
            }
            if (i == _hover) dc.DrawRectangle(HoverBrush, null, rc);
        }
        dc.Pop();

        // geselecteerde stap: altijd zichtbaar, ook als hij maar een paar pixels breed is
        if (SelectedIndex >= 0 && SelectedIndex < show.Steps.Count)
        {
            var x0 = starts[SelectedIndex] / total * w;
            var sw = Math.Max(4, show.Steps[SelectedIndex].DurationMs / total * w);
            dc.DrawRoundedRectangle(null, SelectedPen, new Rect(Math.Max(1, x0 - (sw < 4 ? 2 : 0)), BarTop + 1, Math.Min(sw, w - 2), barH - 2), 3, 3);
        }

        // tijdschaal
        var seconds = total / 1000.0;
        var pxPerSec = w / seconds;
        var step = new[] { 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60 }.FirstOrDefault(v => v * pxPerSec >= 56, 60);
        for (double t = 0; t <= seconds + 0.0001; t += step)
        {
            var x = t * pxPerSec;
            dc.DrawLine(TickPen, new Point(x, barH + 2), new Point(x, barH + 6));
            var txt = step < 1 ? $"{t:0.##}s" : $"{t:0}s";
            if (x < w - 24) DrawText(dc, txt, new Point(x + 2, barH + 4), LabelBrush, 10.5, dpi);
        }
        if (show.Repeats > 1)
            DrawText(dc, $"× {show.Repeats}", new Point(Math.Max(0, w - 30), barH + 4), LabelBrush, 10.5, dpi);

        // afspeelkop
        var px = PlayheadX;
        if (px >= 0)
        {
            dc.DrawLine(PlayheadPen, new Point(px, BarTop - 2), new Point(px, barH + 2));
            var played = (show.Repeats > 1 ? Progress * show.Repeats % 1.0 : Progress) * seconds;
            DrawText(dc, $"{played:0.0}s", new Point(Math.Min(px + 4, w - 34), barH + 4), PlayLabelBrush, 10.5, dpi);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var i = IndexAt(e.GetPosition(this).X);
        if (i < 0) return;
        SelectedIndex = i;
        StepClicked?.Invoke(i);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var show = Show;
        var i = IndexAt(e.GetPosition(this).X);
        if (i == _hover) return;
        _hover = i;
        if (show != null && i >= 0)
        {
            var s = show.Steps[i];
            var (starts, _) = Times(show);
            var what = s.Kind == StepKind.Off ? "Lamp uit"
                : s.ColorHex == "#FFFFFF" ? "Flashbang (wit)"
                : s.ColorHex == "#000000" ? "Zwart"
                : s.ColorHex + $" · {s.Brightness}%";
            var extra = (s.Flashes > 1 ? $" · {s.Flashes}× flits" : "") + (s.TransitionMs > 0 ? $" · overgang {s.TransitionMs} ms" : "");
            _tip.Content = $"Stap {i + 1}: {what} · {s.DurationMs} ms{extra}\nbegint op {starts[i] / 1000.0:0.00} s — klik om te bewerken";
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        InvalidateVisual();
    }

    private static void DrawText(DrawingContext dc, string text, Point at, Brush brush, double size, double dpi = 1.0)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, dpi);
        dc.DrawText(ft, at);
    }

    private static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }
    private static Pen FrozenPen(Brush b, double t) { var p = new Pen(b, t); p.Freeze(); return p; }
}
