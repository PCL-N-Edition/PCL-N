using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Nexa.UI.Next.Backend.Avalonia;

/// <summary>A bounded embedded registry; image keys never resolve arbitrary paths or URLs.</summary>
internal static class AvaloniaUiVersionImages
{
    private static readonly Dictionary<string, Lazy<Bitmap>> Images = new[]
    {
        "Grass", "CommandBlock", "CobbleStone", "GoldBlock", "GrassPath", "Egg",
        "Anvil", "NeoForge", "Cleanroom", "Quilt", "Fabric", "LabyMod",
    }.ToDictionary(name => "nexa/version/" + name, name => new Lazy<Bitmap>(() => Load(name)), StringComparer.Ordinal);

    internal static bool TryDraw(DrawingContext context, string source, Rect bounds)
    {
        if (!Images.TryGetValue(source, out Lazy<Bitmap>? image)) return false;
        Bitmap bitmap = image.Value;
        double scale = Math.Min(bounds.Width / bitmap.Size.Width, bounds.Height / bitmap.Size.Height);
        Size size = bitmap.Size * scale;
        Rect target = new(bounds.X + (bounds.Width - size.Width) / 2, bounds.Y + (bounds.Height - size.Height) / 2, size.Width, size.Height);
        context.DrawImage(bitmap, target);
        return true;
    }

    private static Bitmap Load(string name)
    {
        using Stream stream = typeof(AvaloniaUiVersionImages).Assembly.GetManifestResourceStream(
            $"Nexa.UI.Next.Backend.Avalonia.Assets.Versions.{name}.png")
            ?? throw new InvalidOperationException($"Missing embedded version image: {name}.");
        return new Bitmap(stream);
    }
}
