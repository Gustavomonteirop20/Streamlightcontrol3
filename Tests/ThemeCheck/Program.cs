using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamLightControl2;
using StreamLightControl2.Core.Services;

// Gebruik: ThemeCheck <thema> <uitvoer.png>
var key = args.Length > 0 ? args[0] : "kerst";
var outPath = args.Length > 1 ? args[1] : $"thema-{key}.png";
var result = 1;
var t = new Thread(() =>
{
    try
    {
        var app = new App();
        app.InitializeComponent();
        var theme = AppThemes.Resolve(key, DateTime.Today);
        ThemeApplier.Apply(app, theme);

        T Res<T>(string k) => (T)app.FindResource(k);
        var root = new Border { Background = Res<Brush>("BgBrush"), Padding = new Thickness(24), Width = 520 };
        var stack = new StackPanel();
        root.Child = stack;
        var card = new Border { Style = Res<Style>("Card") };
        var inner = new StackPanel();
        card.Child = inner;
        inner.Children.Add(new TextBlock { Text = "Thema: " + theme.Name, Style = Res<Style>("H2") });
        inner.Children.Add(new TextBlock { Text = "Zo zien knoppen, schakelaars en velden eruit.", Style = Res<Style>("Caption"), Margin = new Thickness(0, 4, 0, 14) });
        var btn = new Button { Content = "Afspelen op lampen", Style = Res<Style>("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 220, MinHeight = 40 };
        inner.Children.Add(btn);
        inner.Children.Add(new CheckBox { Content = "Rustige shows (minder knipperen)", Style = Res<Style>("Switch"), IsChecked = true, Margin = new Thickness(0, 14, 0, 0) });
        inner.Children.Add(new CheckBox { Content = "Festival-flitsen", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) });
        inner.Children.Add(new TextBox { Text = "nickstreams2", Margin = new Thickness(0, 14, 0, 0) });
        inner.Children.Add(new Button { Content = "Annuleren", Style = Res<Style>("SecondaryButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) });
        stack.Children.Add(card);
        var logo = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(9), Background = Res<Brush>("AccentGradient"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
        stack.Children.Add(logo);
        stack.Children.Add(new TextBlock { Text = (string)app.FindResource("BrandSubtitle"), Style = Res<Style>("Caption") });

        root.Measure(new Size(520, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(outPath)) enc.Save(fs);

        Color Pixel(UIElement e)
        {
            var p = e.TranslatePoint(new Point(e.RenderSize.Width / 2, 3), root);
            var px = new byte[4];
            bmp.CopyPixels(new Int32Rect((int)p.X, (int)p.Y, 1, 1), px, 4, 0);
            return Color.FromRgb(px[2], px[1], px[0]);
        }
        bool Close(Color a, string hex) { var b = ThemeApplier.Parse(hex); return Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) <= 12; }
        var btnColor = Pixel(btn); var bgColor = Pixel(root);
        var ok1 = Close(btnColor, theme.Colors["Accent"]);
        var ok2 = Close(bgColor, theme.Colors["Bg"]);
        Console.WriteLine($"  {(ok1 ? "✔" : "✘")} {theme.Key}: knop {btnColor} (verwacht {theme.Colors["Accent"]})");
        Console.WriteLine($"  {(ok2 ? "✔" : "✘")} {theme.Key}: achtergrond {bgColor} (verwacht {theme.Colors["Bg"]})");
        result = ok1 && ok2 ? 0 : 1;
    }
    catch (Exception ex) { Console.WriteLine("  ✘ " + key + ": " + ex); }
});
t.SetApartmentState(ApartmentState.STA);
t.Start(); t.Join();
return result;
