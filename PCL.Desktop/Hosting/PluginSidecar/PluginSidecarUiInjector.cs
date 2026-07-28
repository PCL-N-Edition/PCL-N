// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Threading;
using PCL.Application.Hosting;
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

    /// <summary>Raised on UI thread after remote pages are registered (settings sidebar should refresh).</summary>
    public static event Action? SettingsNavigationChanged;

    public static async Task InjectAsync(IPclHost host, CancellationToken cancellationToken = default)
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

        foreach (PluginUiPageDto page in manifest.Pages ?? [])
        {
            if (string.IsNullOrWhiteSpace(page.Id) || InjectedPageIds.Contains(page.Id))
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
                    PageFactory = () => new PageSetupRemoteDataChain(pageId)
                });
                InjectedPageIds.Add(page.Id);
            }
            catch (InvalidOperationException)
            {
                InjectedPageIds.Add(page.Id);
            }
        }

        PortableLog.Info(
            "PluginSidecar",
            $"UI data-chain injected groups={InjectedGroupIds.Count} pages={InjectedPageIds.Count}.");

        Dispatcher.UIThread.Post(() => SettingsNavigationChanged?.Invoke());
    }
}
