using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamLightControl2.Core;
using StreamLightControl2.Core.Models;

namespace StreamLightControl2;

/// <summary>Tekent een lichtshow als afbeelding: titel + tijdlijn met alle kleuren (voor exporteren/delen).</summary>
public static class ShowImage
{
    public static byte[] Render(LightShow show, int width = 1400, int height = 300)
    {
        var steps = show.Steps.ToList();
        var total = Math.Max(1, steps.Sum(s => s.DurationMs));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x12, 0x13, 0x19)), null, new Rect(0, 0, width, height));
            var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var thin = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            FormattedText Text(string t, double size, Typeface tf, Color c) =>
                new(t, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, size, new SolidColorBrush(c), 1.0);

            dc.DrawText(Text(show.Name, 30, face, Colors.White), new Point(32, 24));
            var info = $"{show.TotalDurationMs / 1000.0:0.0} s · {steps.Count} stappen · {steps.Count(s => s.ColorHex == "#FFFFFF")} flashbangs · " +
                       $"{steps.Count(s => s.Kind == StepKind.Off || s.ColorHex == "#000000")} zwarte stukjes";
            dc.DrawText(Text(info, 15, thin, Color.FromRgb(0x9A, 0x9C, 0xB0)), new Point(34, 66));

            // tijdlijn
            double x = 32, y = 110, w = width - 64, h = 120;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x1C, 0x1D, 0x26)), null, new Rect(x - 4, y - 4, w + 8, h + 8), 10, 10);
            double pos = x;
            foreach (var st in steps)
            {
                var sw = w * st.DurationMs / total;
                Color col;
                if (st.Kind == StepKind.Off) col = Colors.Black;
                else
                {
                    var (r, g, b) = ColorUtil.ParseHex(st.ColorHex);
                    var f = st.Brightness / 100.0;
                    col = Color.FromRgb((byte)(r * f), (byte)(g * f), (byte)(b * f));
                }
                dc.DrawRectangle(new SolidColorBrush(col), null, new Rect(pos, y, Math.Max(0.6, sw + 0.4), h));
                pos += sw;
            }
            // seconden
            for (var sec = 0; sec * 1000 <= total; sec += total > 40000 ? 10 : 5)
            {
                var px = x + w * sec * 1000.0 / total;
                dc.DrawText(Text(sec + " s", 11, thin, Color.FromRgb(0x6E, 0x70, 0x82)), new Point(px, y + h + 8));
            }
            dc.DrawText(Text("Stream Light Control · lichtshow – sleep deze afbeelding in Light Shows om te importeren", 13, thin, Color.FromRgb(0x6E, 0x70, 0x82)),
                new Point(32, height - 30));
        }
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
