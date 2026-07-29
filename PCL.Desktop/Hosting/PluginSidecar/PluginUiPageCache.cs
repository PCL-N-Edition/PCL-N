// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
namespace PCL.Desktop.Hosting.PluginSidecar;

/// <summary>
/// Caches sidecar UI data-chain roots after a page is requested.
/// </summary>
internal static class PluginUiPageCache
{
    private static readonly ConcurrentDictionary<string, PluginUiNodeDto> Roots =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, string> Failures =
        new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> CachedPageIds => Roots.Keys.ToArray();

    public static bool TryGetRoot(string pageId, out PluginUiNodeDto? root)
    {
        if (string.IsNullOrWhiteSpace(pageId))
        {
            root = null;
            return false;
        }

        if (Roots.TryGetValue(pageId, out PluginUiNodeDto? cached))
        {
            root = cached;
            return true;
        }

        root = null;
        return false;
    }

    public static bool TryGetFailure(string pageId, out string? message)
    {
        if (Failures.TryGetValue(pageId, out string? error))
        {
            message = error;
            return true;
        }

        message = null;
        return false;
    }

    public static void SetRoot(string pageId, PluginUiNodeDto root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        ArgumentNullException.ThrowIfNull(root);
        Roots[pageId] = root;
        Failures.TryRemove(pageId, out _);
    }

    public static void Invalidate(string pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId))
            return;
        Roots.TryRemove(pageId, out _);
        Failures.TryRemove(pageId, out _);
    }

    public static void Clear()
    {
        Roots.Clear();
        Failures.Clear();
    }
}
