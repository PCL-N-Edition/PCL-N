// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Accounts;
using PCL.Application.Hosting;
using PCL.Application.Hosting.RuntimeExtensions;
using PCL.Core.Logging;
using PCL.Desktop.Hosting.PluginSidecar;

namespace PCL.Desktop.Hosting;

/// <summary>
/// Host optional runtime: out-of-process plugin sidecar (AOT-safe).
/// Plugin settings pages are NOT hardcoded — they are injected via UI data-chain after sidecar starts.
/// Warmup starts with the desktop host, but the splash and first shell never wait for it.
/// </summary>
internal static partial class DesktopHost
{
    private static IDisposable? _pnpHandlerRegistration;
    private static IDisposable? _feedbackHandlerRegistration;
    private static IDisposable? _onlineMinecraftAccountRegistration;
    private static Task<PluginOptionalRuntimeResult>? _optionalRuntimeTask;

    /// <summary>Outcome of the background plugin warm-start (available after task completes).</summary>
    public static PluginOptionalRuntimeResult? OptionalRuntimeResult { get; private set; }

    static partial void RegisterOptionalModules(PclHostBuilder builder)
    {
        // No host-hardcoded plugin pages. Sidecar ui.manifest injects groups/pages at runtime.
        _ = builder;
    }

    static partial void InitializeOptionalRuntime(IPclHost host)
    {
        // Start during DesktopHost.Initialize; UI callers observe or await only when they need plugin data.
        _optionalRuntimeTask = WarmOptionalRuntimeAsync(host);
    }

    /// <summary>
    /// Wait until the plugin sidecar is ready, or confirmed missing/failed (never throws).
    /// Safe to call multiple times; shares one warm-start task.
    /// </summary>
    public static Task<PluginOptionalRuntimeResult> EnsureOptionalRuntimeReadyAsync(
        CancellationToken cancellationToken = default)
    {
        Task<PluginOptionalRuntimeResult> task = _optionalRuntimeTask ?? Task.FromResult(
            new PluginOptionalRuntimeResult(
                PluginOptionalRuntimeStatus.NotStarted,
                "Optional runtime was not scheduled."));

        if (!cancellationToken.CanBeCanceled || task.IsCompleted)
            return ObserveAsync(task);

        return WaitWithCancellationAsync(task, cancellationToken);
    }

    private static async Task<PluginOptionalRuntimeResult> ObserveAsync(Task<PluginOptionalRuntimeResult> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PluginOptionalRuntimeResult failed = new(
                PluginOptionalRuntimeStatus.Failed,
                ex.Message);
            OptionalRuntimeResult = failed;
            return failed;
        }
    }

    private static async Task<PluginOptionalRuntimeResult> WaitWithCancellationAsync(
        Task<PluginOptionalRuntimeResult> task,
        CancellationToken cancellationToken)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken))
            .ConfigureAwait(false);
        if (completed != task)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return await ObserveAsync(task).ConfigureAwait(false);
    }

    private static async Task<PluginOptionalRuntimeResult> WarmOptionalRuntimeAsync(IPclHost host)
    {
        try
        {
            PluginUiPageCache.Clear();

            // Extract embedded sidecar into config/data dir first (single-file host release path).
            PclEmbeddedPluginSidecar.InvalidateCache();
            string? executable = await PluginSidecarPaths.ResolveExecutableAsync().ConfigureAwait(false);
            if (executable is null)
            {
                PluginOptionalRuntimeResult missing = new(
                    PluginOptionalRuntimeStatus.NotPresent,
                    "Sidecar binary not found; plugin platform disabled.");
                OptionalRuntimeResult = missing;
                PortableLog.Info("DesktopHost", missing.Message);
                return missing;
            }

            PortableLog.Info("DesktopHost", "启动阶段：正在加载插件侧车… " + executable);
            bool ok = await PluginSidecarSupervisor.Instance.TryStartAsync().ConfigureAwait(false);
            if (!ok)
            {
                PluginOptionalRuntimeResult failed = new(
                    PluginOptionalRuntimeStatus.Failed,
                    "Plugin sidecar start failed or hello rejected.");
                OptionalRuntimeResult = failed;
                PortableLog.Info("DesktopHost", failed.Message);
                return failed;
            }

            _pnpHandlerRegistration ??= DesktopFileArtifactHost.Instance.Register(
                new PluginSidecarPnpFileArtifactHandler());
            _feedbackHandlerRegistration ??= RuntimeExtensionHostAccess.Current.FeedbackSubmission.Register(
                new PluginSidecarFeedbackSubmissionHandler());
            // N Cloud launch/login reads HostOnlineMinecraftAccountProvider in the host process.
            // Credentials live in the sidecar — bridge via IPC (do not rely on sidecar-local Register).
            _onlineMinecraftAccountRegistration ??= HostOnlineMinecraftAccountProvider.Register(
                new PluginSidecarOnlineMinecraftAccountProvider());
            await PluginSidecarUiInjector.InjectAsync(host).ConfigureAwait(false);

            PluginOptionalRuntimeResult ready = new(
                PluginOptionalRuntimeStatus.Ready,
                "Plugin sidecar started; UI data-chain + feedback + N Cloud bridge ready.");
            OptionalRuntimeResult = ready;
            PortableLog.Info("DesktopHost", ready.Message);
            return ready;
        }
        catch (Exception ex)
        {
            PluginOptionalRuntimeResult failed = new(
                PluginOptionalRuntimeStatus.Failed,
                "Plugin sidecar warm-start failed: " + ex.Message);
            OptionalRuntimeResult = failed;
            PortableLog.Warn("DesktopHost", failed.Message);
            return failed;
        }
    }

    public static void ShutdownOptionalRuntime()
    {
        try
        {
            _feedbackHandlerRegistration?.Dispose();
            _feedbackHandlerRegistration = null;
            _onlineMinecraftAccountRegistration?.Dispose();
            _onlineMinecraftAccountRegistration = null;
            _pnpHandlerRegistration?.Dispose();
            _pnpHandlerRegistration = null;

            // Never block Avalonia Exit indefinitely — a wedged pipe was leaving a zombie host
            // with no window, so the next launch looked like a splash flash-quit (second instance).
            Task dispose = PluginSidecarSupervisor.Instance.DisposeAsync().AsTask();
            if (!dispose.Wait(TimeSpan.FromSeconds(2)))
            {
                PortableLog.Warn("DesktopHost", "Plugin sidecar shutdown timed out; forcing process sweep.");
                try
                {
                    foreach (System.Diagnostics.Process orphan in
                             System.Diagnostics.Process.GetProcessesByName("PCL.Plugin.Sidecar"))
                    {
                        try
                        {
                            if (!orphan.HasExited)
                                orphan.Kill(entireProcessTree: true);
                        }
                        catch
                        {
                            // ignore
                        }
                        finally
                        {
                            orphan.Dispose();
                        }
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch (Exception ex)
        {
            PortableLog.Warn("DesktopHost", "Plugin sidecar shutdown: " + ex.Message);
        }
    }
}

internal enum PluginOptionalRuntimeStatus
{
    NotStarted,
    NotPresent,
    Ready,
    Failed
}

internal sealed record PluginOptionalRuntimeResult(
    PluginOptionalRuntimeStatus Status,
    string Message)
{
    public bool IsReady => Status == PluginOptionalRuntimeStatus.Ready;

    /// <summary>True when we finished probing (binary missing counts as resolved).</summary>
    public bool IsResolved =>
        Status is PluginOptionalRuntimeStatus.Ready
            or PluginOptionalRuntimeStatus.NotPresent
            or PluginOptionalRuntimeStatus.Failed;
}
