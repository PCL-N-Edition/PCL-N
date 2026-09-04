using System.Collections.Concurrent;
using System.Collections.Frozen;
using Avalonia.Media;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>
/// The embedded icon registry for scene <c>ImageSource</c> values. Path data is frozen in code
/// from the legacy icon packs (lucide-static v1.17.0, ISC license; the PCL window-restore glyph
/// belongs to the product's own pack), so the shell needs no runtime SVG parsing and no icon
/// asset files. Names are the contract and keep the legacy "pack/key" spelling, e.g.
/// <c>lucide/play</c>. Every icon shares the 24×24 lucide viewBox with stroke-width 2 and round
/// caps/joins; consumers scale it into their own bounds and stroke it with their tint color.
/// </summary>
internal static class AvaloniaUiIcons
{
    public const double ViewBoxSize = 24;

    public const double StrokeWidth = 2;

    private static readonly FrozenDictionary<string, string[]> Paths = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["lucide/user"] =
        [
            "M12 3a4 4 0 1 0 0 8 4 4 0 0 0 0-8z",
            "M5 21v-2a7 7 0 0 1 14 0v2",
        ],
        ["lucide/chevron-right"] = ["m9 6 6 6-6 6"],
        ["lucide/chevron-up"] = ["m18 15-6-6-6 6"],
        ["lucide/chevron-down"] = ["m6 9 6 6 6-6"],
        ["lucide/arrow-left"] = ["m12 19-7-7 7-7", "M5 12h14"],
        ["lucide/wrench"] =
        [
            "M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94z",
        ],
        ["lucide/play"] =
        [
            "M5 5a2 2 0 0 1 3.008-1.728l11.997 6.998a2 2 0 0 1 .003 3.458l-12 7A2 2 0 0 1 5 19z",
        ],
        ["lucide/package-plus"] =
        [
            "M12 22V12",
            "M16 17h6",
            "M19 14v6",
            "M21 10.535V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.729l7 4a2 2 0 0 0 2 .001l1.675-.955",
            "M3.29 7 12 12l8.71-5",
            "m7.5 4.27 8.997 5.148",
        ],
        ["lucide/blocks"] =
        [
            "M15 3h5a1 1 0 0 1 1 1v5a1 1 0 0 1-1 1h-5a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1z",
            "M10 21V8a1 1 0 0 0-1-1H4a1 1 0 0 0-1 1v12a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-5a1 1 0 0 0-1-1H3",
        ],
        ["lucide/settings"] =
        [
            "M9.671 4.136a2.34 2.34 0 0 1 4.659 0 2.34 2.34 0 0 0 3.319 1.915 2.34 2.34 0 0 1 2.33 4.033 2.34 2.34 0 0 0 0 3.831 2.34 2.34 0 0 1-2.33 4.033 2.34 2.34 0 0 0-3.319 1.915 2.34 2.34 0 0 1-4.659 0 2.34 2.34 0 0 0-3.32-1.915 2.34 2.34 0 0 1-2.33-4.033 2.34 2.34 0 0 0 0-3.831A2.34 2.34 0 0 1 6.35 6.051a2.34 2.34 0 0 0 3.319-1.915",
            "M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6z",
        ],
        ["lucide/menu"] =
        [
            "M4 6h16",
            "M4 12h16",
            "M4 18h16",
        ],
        ["lucide/minus"] =
        [
            "M5 12h14",
        ],
        ["lucide/square"] =
        [
            "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z",
        ],
        ["lucide/x"] =
        [
            "M18 6 6 18",
            "m6 6 12 12",
        ],
        ["pcl/window-restore"] =
        [
            "M8 8V5h11v11h-3",
            "M6 8h9a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V9a1 1 0 0 1 1-1z",
        ],
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, Geometry[]> GeometryCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Resolves an icon name into its parsed geometry. Unknown names return false so a scene can
    /// carry icons this build does not embed; the consumer then draws text-only or nothing.
    /// </summary>
    public static bool TryGetGeometry(string source, out IReadOnlyList<Geometry> geometry)
    {
        if (!Paths.ContainsKey(source))
        {
            geometry = [];
            return false;
        }

        Geometry[] parsed = GeometryCache.GetOrAdd(source, static key =>
        [
            .. Paths[key].Select(Geometry.Parse),
        ]);
        geometry = parsed;
        return true;
    }
}
