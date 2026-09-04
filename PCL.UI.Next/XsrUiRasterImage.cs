using PCL.Core.Media;

namespace PCL.UI.Next;

/// <summary>One source-pixel crop placed into a normalized destination rectangle.</summary>
public readonly record struct XsrUiImageLayer(XsrUiRect Source, XsrUiRect Destination);

/// <summary>Immutable encoded resource and draw recipe; no I/O or native bitmap crosses into UI.Next.</summary>
public sealed record XsrUiRasterImage(PngImage Image, IReadOnlyList<XsrUiImageLayer> Layers);
