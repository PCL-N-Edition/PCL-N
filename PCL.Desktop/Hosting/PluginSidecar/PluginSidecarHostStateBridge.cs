// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Hosting.RuntimeExtensions;
using PCL.Application.Launching;
using PCL.Core.Logging;
using PCL.Desktop.Diagnostics;

namespace PCL.Desktop.Hosting.PluginSidecar;

/// <summary>
/// Mirrors low-frequency host-owned instance/session state into the CoreCLR sidecar.
/// The sidecar remains unable to reach launcher-private CLR objects directly.
/// </summary>
internal static class PluginSidecarHostStateBridge
{
    private static int _subscribed;
    private static int _syncPending;
    private static int _draining;

    public static async Task AttachAndSynchronizeAsync(
        PluginSidecarClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (Interlocked.Exchange(ref _subscribed, 1) == 0)
            GameSessionRegistry.Shared.LaunchEventPublished += OnLaunchEvent;
        await SynchronizeAsync(client, cancellationToken).ConfigureAwait(false);
    }

    private static void OnLaunchEvent(GameLaunchEvent _) => QueueSynchronization();

    private static void QueueSynchronization()
    {
        Interlocked.Exchange(ref _syncPending, 1);
        if (Interlocked.Exchange(ref _draining, 1) != 0)
            return;
        UnhandledExceptionGuard.Observe(DrainAsync(), "PluginSidecar.HostStateSync");
    }

    private static async Task DrainAsync()
    {
        try
        {
            do
            {
                Interlocked.Exchange(ref _syncPending, 0);
                await Task.Delay(40).ConfigureAwait(false);
                if (PluginSidecarSupervisor.Instance is { IsAvailable: true, Client: { } client } &&
                    client.ProtocolVersion >= PluginSidecarProtocolVersions.Current)
                {
                    await SynchronizeAsync(client, CancellationToken.None).ConfigureAwait(false);
                }
            }
            while (Interlocked.CompareExchange(ref _syncPending, 0, 0) != 0);
        }
        catch (Exception exception)
        {
            PortableLog.Warn("PluginSidecar", "同步宿主实例/游戏会话失败：" + exception.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _draining, 0);
            if (Interlocked.CompareExchange(ref _syncPending, 0, 0) != 0)
                QueueSynchronization();
        }
    }

    private static async Task SynchronizeAsync(
        PluginSidecarClient client,
        CancellationToken cancellationToken)
    {
        PluginSidecarHostInstance[] instances = DesktopHostInstanceQuery.Instance.ListInstances()
            .Select(static item => new PluginSidecarHostInstance
            {
                Id = item.Id,
                Name = item.Name,
                InstanceDirectory = item.InstanceDirectory,
                VersionJsonPath = item.VersionJsonPath
            })
            .ToArray();
        PluginSidecarGameSession[] sessions = GameSessionRegistry.Shared.ListSessions()
            .Select(static item => new PluginSidecarGameSession
            {
                SessionId = item.SessionId,
                InstanceId = item.InstanceId,
                ProcessId = item.ProcessId,
                State = (int)item.State,
                StartedAt = item.StartedAt,
                EndedAt = item.EndedAt,
                ExitCode = item.ExitCode,
                LastSequence = item.LastSequence,
                LanAddress = item.LanAddress
            })
            .ToArray();
        PluginSidecarResult result = await client.SyncHostStateAsync(instances, sessions, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Ok)
            throw new InvalidOperationException(result.Message ?? "Sidecar rejected host state.");
    }
}
