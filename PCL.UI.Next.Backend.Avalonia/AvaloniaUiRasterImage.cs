using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PCL.UI.Next;

namespace PCL.UI.Next.Backend.Avalonia;

internal sealed partial class AvaloniaUiSceneNodeControl
{
    private Bitmap? _rasterBitmap;
    private string? _rasterKey;
    internal bool HasDecodedRaster => _rasterBitmap is not null;

    private void UpdateRaster(XsrUiRasterImage? raster)
    {
        if (_rasterKey == raster?.Image.Key) return;
        _rasterBitmap?.Dispose(); _rasterBitmap = null; _rasterKey = raster?.Image.Key;
        if (raster is null) return;
        try
        {
            using MemoryStream stream = new(raster.Image.Bytes.ToArray(), writable: false);
            Bitmap bitmap = new(stream);
            if (bitmap.PixelSize.Width != raster.Image.Width || bitmap.PixelSize.Height != raster.Image.Height) bitmap.Dispose();
            else _rasterBitmap = bitmap;
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
        { /* Malformed pixels keep the embedded source; image decode never breaks scene commit. */ }
    }

    internal void ReleasePresentation()
    {
        AvaloniaUiMotion.CancelAll(this);
        _rasterBitmap?.Dispose(); _rasterBitmap = null; _rasterKey = null;
    }

    private bool DrawRaster(DrawingContext context, Rect bounds)
    {
        if (_rasterBitmap is not { } bitmap || _node.RasterImage is not { } raster) return false;
        double size = Math.Min(bounds.Width, bounds.Height);
        double x = bounds.X + (bounds.Width - size) / 2, y = bounds.Y + (bounds.Height - size) / 2;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
        {
            foreach (XsrUiImageLayer layer in raster.Layers)
                context.DrawImage(bitmap, new Rect(layer.Source.X, layer.Source.Y, layer.Source.Width, layer.Source.Height),
                    new Rect(x + layer.Destination.X * size, y + layer.Destination.Y * size, layer.Destination.Width * size, layer.Destination.Height * size));
        }
        return true;
    }
}
