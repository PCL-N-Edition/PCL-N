// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Threading;
using PCL.Application.Hosting;
using PCL.Application.Hosting.RuntimeExtensions;
using PCL.Application.Settings;
using PCL.Core.Logging;
using PCL.Desktop.Features.Settings.Views;

namespace PCL.Desktop.Hosting.PluginSidecar;

/// <summary>
/// Injects sidecar-declared settings groups/pages into the host registry (data-chain only).
/// </summary>
internal static class PluginSidecarUiInjector
{
    private static readonly HashSet<string> InjectedPageIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> InjectedGroupIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IHostRegistration> NavigationRegistrations =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim InjectionGate = new(1, 1);

    /// <summary>Raised on UI thread after remote pages are registered (settings sidebar should refresh).</summary>
    public static event Action? SettingsNavigationChanged;

    public static async Task InjectAsync(IPclHost host, CancellationToken cancellationToken = default)
    {
        await InjectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InjectCoreAsync(host, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            InjectionGate.Release();
        }
    }

    private static async Task InjectCoreAsync(IPclHost host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        PluginSidecarClient? client = PluginSidecarSupervisor.Instance.Client;
        if (client is null || !PluginSidecarSupervisor.Instance.IsAvailable)
            return;

        PluginSidecarResult manifest = await client.UiManifestAsync(cancellationToken).ConfigureAwait(false);
        if (!manifest.Ok)
        {
            PortableLog.Warn("PluginSidecar", "ui.manifest failed: " + (manifest.Message ?? "unknown"));
            return;
        }

        foreach (PluginUiGroupDto group in manifest.Groups ?? [])
        {
            if (string.IsNullOrWhiteSpace(group.Id) || InjectedGroupIds.Contains(group.Id))
                continue;
            try
            {
                host.SettingsPageGroups.AddGroup(new HostSettingsPageGroupDescriptor(
                    group.Id,
                    group.Title,
                    group.Icon ?? "lucide/plug",
                    group.Order,
                    group.Description));
                InjectedGroupIds.Add(group.Id);
            }
            catch (InvalidOperationException)
            {
                // already present
                InjectedGroupIds.Add(group.Id);
            }
        }

        HashSet<string> currentSettingsPages = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> currentNavigationPages = new(StringComparer.OrdinalIgnoreCase);
        foreach (PluginUiPageDto page in manifest.Pages ?? [])
        {
            if (string.IsNullOrWhiteSpace(page.Id))
                continue;

            if (!string.Equals(page.Surface, "settings", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(page.Surface, "pcl.navigation.main", StringComparison.OrdinalIgnoreCase))
                {
                    PortableLog.Warn(
                        "PluginSidecar",
                        $"忽略不支持的远程页面 Surface：{page.Id} → {page.Surface}");
                    continue;
                }

                currentNavigationPages.Add(page.Id);
                if (NavigationRegistrations.ContainsKey(page.Id))
                    continue;

                string route = page.Id;
                try
                {
                    IHostRegistration registration = DesktopHostNavigation.Instance.RegisterPage(
                        new HostPageRegistration(
                            "plugin-sidecar",
                            route,
                            route,
                            page.Title,
                            string.IsNullOrWhiteSpace(page.Icon) ? "lucide/plug" : page.Icon,
                            page.Order,
                            () => new PageSetupRemoteDataChain(route)));
                    NavigationRegistrations.Add(route, registration);
                }
                catch (InvalidOperationException ex)
                {
                    PortableLog.Warn("PluginSidecar", $"注册远程导航页失败：{route}；{ex.Message}");
                }

                continue;
            }

            currentSettingsPages.Add(page.Id);
            if (InjectedPageIds.Contains(page.Id))
                continue;

            string pageId = page.Id;
            try
            {
                host.SettingsPages.AddPage(new HostSettingsPageDescriptor(
                    page.Id,
                    page.Title,
                    string.IsNullOrWhiteSpace(page.Icon) ? "lucide/plug" : page.Icon,
                    string.IsNullOrWhiteSpace(page.Heading) ? page.Title : page.Heading,
                    page.Description ?? "",
                    [])
                {
                    GroupId = page.GroupId,
                    Order = page.Order,
                    RequiresDeveloperMode = page.RequiresDeveloperMode,
                    PageFactory = () => new PageSetupRemoteDataChain(pageId)
                });
                InjectedPageIds.Add(page.Id);
            }
            catch (InvalidOperationException)
            {
                InjectedPageIds.Add(page.Id);
            }
        }

        foreach (string stale in InjectedPageIds.Except(currentSettingsPages).ToArray())
        {
            if (host.SettingsPages.RemovePage(stale))
                InjectedPageIds.Remove(stale);
            PluginUiPageCache.Invalidate(stale);
        }

        foreach (string stale in NavigationRegistrations.Keys.Except(currentNavigationPages).ToArray())
        {
            IHostRegistration registration = NavigationRegistrations[stale];
            NavigationRegistrations.Remove(stale);
            await registration.DisposeAsync().ConfigureAwait(false);
            PluginUiPageCache.Invalidate(stale);
        }

        PortableLog.Info(
            "PluginSidecar",
            $"UI data-chain injected groups={InjectedGroupIds.Count} settings={InjectedPageIds.Count} " +
            $"navigation={NavigationRegistrations.Count}；页面正文改为按需加载。");

        Dispatcher.UIThread.Post(() => SettingsNavigationChanged?.Invoke());
    }
}
