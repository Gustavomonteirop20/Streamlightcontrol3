using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using StreamLightControl2.Core;

namespace StreamLightControl2.Controls;

/// <summary>
/// Kleurkiezer: verzadiging/helderheid-vlak, kleurtoonbalk, HEX- en RGB-invoer en snelle kleuren.
/// Gebruik: &lt;ui:ColorPicker SelectedHex="{Binding ColorHex, Mode=TwoWay}"/&gt;
/// </summary>
public sealed class ColorPicker : UserControl
{
    public static readonly DependencyProperty SelectedHexProperty = DependencyProperty.Register(
        nameof(SelectedHex), typeof(string), typeof(ColorPicker),
        new FrameworkPropertyMetadata("#7B6CF6", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((ColorPicker)d).OnHexChanged()));

    public string SelectedHex
    {
        get => (string)GetValue(SelectedHexProperty);
        set => SetValue(SelectedHexProperty, value);
    }

    public static readonly string[] Swatches =
        { "#FF1A1A", "#FF8000", "#FFE600", "#2ECC71", "#00C2FF", "#3478F6", "#7B6CF6", "#B026FF", "#FF3DC8", "#FFFFFF" };

    private double _h, _s = 1, _v = 1;
    private bool _updating;
    private readonly Border _svArea;
    private readonly Rectangle _hueFill;
    private readonly Ellipse _svThumb;
    private readonly Canvas _svCanvas;
    private readonly Border _hueBar;
    private readonly Border _hueThumb;
    private readonly Canvas _hueCanvas;
    private readonly TextBox _hexBox, _rBox, _gBox, _bBox;
    private readonly Border _preview;

    public ColorPicker()
    {
        var root = new StackPanel();

        // --- verzadiging / helderheid ---
        _hueFill = new Rectangle { Fill = Brushes.Red };
        var white = new Rectangle { Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0) };
        var black = new Rectangle { Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90) };
        _svThumb = new Ellipse { Width = 16, Height = 16, Stroke = Brushes.White, StrokeThickness = 2.5, IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.6 } };
        _svCanvas = new Canvas { ClipToBounds = false };
        _svCanvas.Children.Add(_svThumb);
        var svGrid = new Grid();
        svGrid.Children.Add(_hueFill);
        svGrid.Children.Add(white);
        svGrid.Children.Add(black);
        svGrid.Children.Add(_svCanvas);
        _svArea = new Border { Height = 150, CornerRadius = new CornerRadius(8), Child = svGrid, Cursor = Cursors.Cross, ClipToBounds = true };
        _svArea.MouseLeftButtonDown += (_, e) => { _svArea.CaptureMouse(); PickSv(e.GetPosition(_svArea)); };
        _svArea.MouseMove += (_, e) => { if (_svArea.IsMouseCaptured) PickSv(e.GetPosition(_svArea)); };
        _svArea.MouseLeftButtonUp += (_, _) => _svArea.ReleaseMouseCapture();
        _svArea.SizeChanged += (_, _) => PlaceThumbs();
        root.Children.Add(_svArea);

        // --- kleurtoon ---
        var hueGradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (var i = 0; i <= 6; i++) hueGradient.GradientStops.Add(new GradientStop(HsvToColor(i * 60 % 360, 1, 1), i / 6.0));
        _hueThumb = new Border { Width = 8, Height = 18, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.White, BorderThickness = new Thickness(2), IsHitTestVisible = false };
        _hueCanvas = new Canvas();
        _hueCanvas.Children.Add(_hueThumb);
        var hueGrid = new Grid();
        hueGrid.Children.Add(new Border { Background = hueGradient, CornerRadius = new CornerRadius(5), Height = 12, VerticalAlignment = VerticalAlignment.Center });
        hueGrid.Children.Add(_hueCanvas);
        _hueBar = new Border { Height = 18, Margin = new Thickness(0, 12, 0, 0), Background = Brushes.Transparent, Child = hueGrid, Cursor = Cursors.Hand };
        _hueBar.MouseLeftButtonDown += (_, e) => { _hueBar.CaptureMouse(); PickHue(e.GetPosition(_hueBar)); };
        _hueBar.MouseMove += (_, e) => { if (_hueBar.IsMouseCaptured) PickHue(e.GetPosition(_hueBar)); };
        _hueBar.MouseLeftButtonUp += (_, _) => _hueBar.ReleaseMouseCapture();
        _hueBar.SizeChanged += (_, _) => PlaceThumbs();
        root.Children.Add(_hueBar);

        // --- HEX + RGB ---
        var inputs = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        inputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        inputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        for (var i = 0; i < 3; i++) inputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _preview = new Border { CornerRadius = new CornerRadius(8), Height = 36, Margin = new Thickness(0, 18, 8, 0), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)) };
        inputs.Children.Add(_preview);
        _hexBox = AddField(inputs, 1, "HEX", 120);
        _rBox = AddField(inputs, 2, "R", 0);
        _gBox = AddField(inputs, 3, "G", 0);
        _bBox = AddField(inputs, 4, "B", 0);
        _hexBox.LostFocus += (_, _) => ApplyHexBox();
        _hexBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyHexBox(); };
        foreach (var box in new[] { _rBox, _gBox, _bBox })
        {
            box.LostFocus += (_, _) => ApplyRgbBoxes();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyRgbBoxes(); };
        }
        root.Children.Add(inputs);

        // --- snelle kleuren ---
        var swatches = new UniformGrid { Columns = Swatches.Length, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var hex in Swatches)
        {
            var b = new Border
            {
                Height = 22, Margin = new Thickness(0, 0, 6, 0), CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand,
                Background = HexToBrushConverter.Get(hex), ToolTip = hex,
                BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255))
            };
            b.MouseLeftButtonUp += (_, _) => SelectedHex = hex;
            swatches.Children.Add(b);
        }
        root.Children.Add(swatches);

        Content = root;
        OnHexChanged();
    }

    private static TextBox AddField(Grid grid, int column, string label, int width)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, column < 4 ? 6 : 0, 0) };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0, 0, 0, 4), Foreground = (Brush)Application.Current.FindResource("Text3Brush") });
        var box = new TextBox { Padding = new Thickness(8, 0, 8, 0) };
        if (column == 1) box.FontFamily = (FontFamily)Application.Current.FindResource("MonoFont");
        panel.Children.Add(box);
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
        return box;
    }

    private void PickSv(Point p)
    {
        _s = Math.Clamp(p.X / Math.Max(1, _svArea.ActualWidth), 0, 1);
        _v = 1 - Math.Clamp(p.Y / Math.Max(1, _svArea.ActualHeight), 0, 1);
        CommitHsv();
    }

    private void PickHue(Point p)
    {
        _h = Math.Clamp(p.X / Math.Max(1, _hueBar.ActualWidth), 0, 0.9999) * 360;
        CommitHsv();
    }

    private void CommitHsv()
    {
        var c = HsvToColor(_h, _s, _v);
        _updating = true;
        SelectedHex = ColorUtil.ToHex(c.R, c.G, c.B);
        _updating = false;
        Refresh(c);
    }

    private void ApplyHexBox()
    {
        if (ColorUtil.TryParseHex(_hexBox.Text, out var r, out var g, out var b)) SelectedHex = ColorUtil.ToHex(r, g, b);
        else _hexBox.Text = SelectedHex;
    }

    private void ApplyRgbBoxes()
    {
        static byte P(string s) => byte.TryParse(s.Trim(), out var v) ? v : (byte)Math.Clamp(int.TryParse(s.Trim(), out var i) ? i : 0, 0, 255);
        SelectedHex = ColorUtil.ToHex(P(_rBox.Text), P(_gBox.Text), P(_bBox.Text));
    }

    private void OnHexChanged()
    {
        if (!ColorUtil.TryParseHex(SelectedHex, out var r, out var g, out var b)) return;
        var c = Color.FromRgb(r, g, b);
        if (!_updating)
        {
            ColorToHsv(c, out var h, out var s, out var v);
            // Kleurtoon niet resetten bij grijs/zwart
            if (s > 0.001 && v > 0.001) _h = h;
            _s = s; _v = v;
        }
        Refresh(c);
    }

    private void Refresh(Color c)
    {
        _hueFill.Fill = new SolidColorBrush(HsvToColor(_h, 1, 1));
        _preview.Background = new SolidColorBrush(c);
        _hueThumb.Background = new SolidColorBrush(HsvToColor(_h, 1, 1));
        _svThumb.Fill = new SolidColorBrush(c);
        if (!_hexBox.IsKeyboardFocused) _hexBox.Text = ColorUtil.ToHex(c.R, c.G, c.B);
        if (!_rBox.IsKeyboardFocused) _rBox.Text = c.R.ToString();
        if (!_gBox.IsKeyboardFocused) _gBox.Text = c.G.ToString();
        if (!_bBox.IsKeyboardFocused) _bBox.Text = c.B.ToString();
        PlaceThumbs();
    }

    private void PlaceThumbs()
    {
        Canvas.SetLeft(_svThumb, _s * _svArea.ActualWidth - 8);
        Canvas.SetTop(_svThumb, (1 - _v) * _svArea.ActualHeight - 8);
        Canvas.SetLeft(_hueThumb, _h / 360 * _hueBar.ActualWidth - 4);
        Canvas.SetTop(_hueThumb, 0);
    }

    public static Color HsvToColor(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static void ColorToHsv(Color color, out double h, out double s, out double v)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        s = max == 0 ? 0 : d / max;
        v = max;
    }
}
