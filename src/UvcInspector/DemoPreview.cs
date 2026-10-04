using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UvcInspector;

public static class DemoPreview
{
    public static ImageSource Create()
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var colors = new[] { "#E8EDF3", "#F5CE58", "#5FCDBE", "#5ACF80", "#CE79CE", "#F0787B", "#729FF0" };
            for (int i = 0; i < colors.Length; i++) drawing.DrawRectangle((Brush)new BrushConverter().ConvertFromString(colors[i])!, null, new Rect(i * 960.0 / 7, 0, 960.0 / 7 + 1, 390));
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(20, 35, 58)), null, new Rect(0, 390, 960, 150));
            drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(215, 14, 31, 53)), null, new Rect(260, 170, 440, 160), 20, 20);
            var typeface = new Typeface("Segoe UI");
            drawing.DrawText(new FormattedText("UVC / DEMO", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 47, Brushes.White, 1), new Point(310, 207));
            drawing.DrawText(new FormattedText("NATIVE PREVIEW   ·   1920 × 1080", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 23, new SolidColorBrush(Color.FromRgb(163, 184, 215)), 1), new Point(240, 447));
        }
        var image = new RenderTargetBitmap(960, 540, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); image.Freeze(); return image;
    }
}
