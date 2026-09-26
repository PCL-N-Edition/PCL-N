using Nexa.Core.Media;

namespace Nexa.UI.Next;

/// <summary>One source-pixel crop placed into a normalized destination rectangle.</summary>
public readonly record struct XsrUiImageLayer(XsrUiRect Source, XsrUiRect Destination);

/// <summary>Immutable encoded resource and draw recipe; no I/O or native bitmap crosses into UI.Next.</summary>
public sealed record XsrUiRasterImage(PngImage Image, IReadOnlyList<XsrUiImageLayer> Layers)
{
    /// <summary>Fit a complete local image into its bounds instead of composing square skin layers.</summary>
    public bool FitToBounds { get; init; }
}
