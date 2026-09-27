using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinSaver.Editor;

internal static class ImageComposer
{
    /// <summary>Flattens the drawing onto the image at its own resolution and applies the crop.</summary>
    public static BitmapSource Compose(EditDocument doc)
    {
        if (doc.IsUntouched)
            return doc.Image;

        var crop = doc.Crop;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-crop.X, -crop.Y));
            doc.Render(dc);
            dc.Pop();
        }

        var target = new RenderTargetBitmap(crop.Width, crop.Height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        // Opaque 32-bit pixels paste correctly everywhere; some apps show premultiplied alpha as black.
        var opaque = new FormatConvertedBitmap(target, PixelFormats.Bgr32, null, 0);
        int stride = crop.Width * 4;
        var pixels = new byte[stride * crop.Height];
        opaque.CopyPixels(pixels, stride, 0);
        var result = BitmapSource.Create(crop.Width, crop.Height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
