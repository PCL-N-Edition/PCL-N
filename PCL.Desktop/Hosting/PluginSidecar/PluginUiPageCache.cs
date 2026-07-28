// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using PCL.Core.Logging;

namespace PCL.Desktop.Hosting.PluginSidecar;

/// <summary>
/// Prefetches sidecar UI data-chain page roots during splash so settings pages
/// open without a visible "正在加载" state.
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

    /// <summary>
    /// Fetch and cache every listed page. Runs after sidecar hello during splash.
    /// </summary>
    public static async Task PreloadAsync(
        PluginSidecarClient client,
        IEnumerable<string> pageIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(pageIds);

        List<string> unique = pageIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int ok = 0;
        int fail = 0;
        foreach (string pageId in unique)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                PluginSidecarResult page = await client.UiGetPageAsync(pageId, cancellationToken)
                    .ConfigureAwait(false);
                if (page.Ok && page.Root is not null)
                {
                    SetRoot(pageId, page.Root);
                    ok++;
                }
                else
                {
                    Failures[pageId] = page.Message ?? "页面为空。";
                    fail++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Failures[pageId] = ex.Message;
                fail++;
                PortableLog.Warn("PluginSidecar", $"预加载页面失败 {pageId}：{ex.Message}");
            }
        }

        PortableLog.Info(
            "PluginSidecar",
            $"插件页面预加载完成：ok={ok} fail={fail} total={unique.Count}。");
    }
}
