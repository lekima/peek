using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using Point = System.Windows.Point;

namespace Peek;

public sealed class ScreenCaptureService
{
    public Bitmap CaptureVisualBounds(Window window, FrameworkElement visual)
    {
        var source = PresentationSource.FromVisual(window);
        if (source?.CompositionTarget is null)
        {
            throw new InvalidOperationException("Window is not ready for capture.");
        }

        var transform = source.CompositionTarget.TransformToDevice;
        var topLeft = visual.PointToScreen(new Point(0, 0));
        var size = transform.Transform(new Point(visual.ActualWidth, visual.ActualHeight));

        var x = (int)Math.Round(topLeft.X);
        var y = (int)Math.Round(topLeft.Y);
        var width = Math.Max(1, (int)Math.Round(size.X));
        var height = Math.Max(1, (int)Math.Round(size.Y));

        var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
        return bitmap;
    }
}
