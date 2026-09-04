using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>Two bounded, process-lifetime default textures; no filesystem or network resolution.</summary>
internal static class AvaloniaUiAvatars
{
    private static readonly Lazy<Bitmap> Steve = new(() => Load("Steve"));
    private static readonly Lazy<Bitmap> Alex = new(() => Load("Alex"));

    internal static bool TryDraw(DrawingContext context, string source, Rect bounds)
    {
        Bitmap? texture = source switch
        {
            "pcl/avatar/steve" => Steve.Value,
            "pcl/avatar/alex" => Alex.Value,
            _ => null,
        };
        if (texture is null) return false;
        double size = Math.Min(bounds.Width, bounds.Height);
        double scale = texture.PixelSize.Width / 64d;
        Rect Destination(double fraction) => new(
            bounds.X + (bounds.Width - size * fraction) / 2,
            bounds.Y + (bounds.Height - size * fraction) / 2,
            size * fraction, size * fraction);
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
        {
            context.DrawImage(texture, new Rect(8 * scale, 8 * scale, 8 * scale, 8 * scale), Destination(.75));
            context.DrawImage(texture, new Rect(40 * scale, 8 * scale, 8 * scale, 8 * scale), Destination(.875));
        }
        return true;
    }

    private static Bitmap Load(string name)
    {
        using Stream stream = typeof(AvaloniaUiAvatars).Assembly.GetManifestResourceStream(
            $"PCL.UI.Next.Backend.Avalonia.Assets.Avatars.{name}.png")
            ?? throw new InvalidOperationException($"Missing embedded default avatar: {name}.");
        return new Bitmap(stream);
    }
}
