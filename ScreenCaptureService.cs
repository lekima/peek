using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using Point = System.Windows.Point;

namespace Peek;

internal static class ScreenCaptureService
{
    public static Bitmap CaptureVisualBounds(Window window, FrameworkElement visual)
    {
        var bounds = GetVisualScreenBounds(window, visual);
        return CaptureScreenBounds(bounds);
    }

    public static Rect GetVisualScreenBounds(Window window, FrameworkElement visual)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(visual);

        var source = PresentationSource.FromVisual(window);
        if (source?.CompositionTarget is null)
        {
            throw new InvalidOperationException("Window is not ready for capture.");
        }

        var transform = source.CompositionTarget.TransformToDevice;
        var topLeft = visual.PointToScreen(new Point(0, 0));
        var widthDip = Math.Max(1, visual.ActualWidth);
        var heightDip = Math.Max(1, visual.ActualHeight);
        var size = transform.Transform(new Point(widthDip, heightDip));

        return new Rect(
            Math.Round(topLeft.X),
            Math.Round(topLeft.Y),
            Math.Max(1, Math.Round(size.X)),
            Math.Max(1, Math.Round(size.Y)));
    }

    public static Bitmap CaptureScreenBounds(Rect bounds)
    {
        var bitmap = new Bitmap((int)bounds.Width, (int)bounds.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(
            (int)bounds.X,
            (int)bounds.Y,
            0,
            0,
            new System.Drawing.Size((int)bounds.Width, (int)bounds.Height),
            CopyPixelOperation.SourceCopy);
        return bitmap;
    }
}
