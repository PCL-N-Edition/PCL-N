// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Hosting;
using PCL.Application.Hosting.RuntimeExtensions;
using PCL.Core.Logging;
using PCL.Desktop.Hosting.PluginSidecar;

namespace PCL.Desktop.Hosting;

/// <summary>
/// Host optional runtime: out-of-process plugin sidecar (AOT-safe).
/// Plugin settings pages are NOT hardcoded — they are injected via UI data-chain after sidecar starts.
/// Splash/startup waits on <see cref="EnsureOptionalRuntimeReadyAsync"/> before entering the main shell.
/// </summary>
internal static partial class DesktopHost
{
    private static IDisposable? _pnpHandlerRegistration;
    private static IDisposable? _feedbackHandlerRegistration;
    private static Task<PluginOptionalRuntimeResult>? _optionalRuntimeTask;

    /// <summary>Outcome of the splash-time plugin warm-start (available after task completes).</summary>
    public static PluginOptionalRuntimeResult? OptionalRuntimeResult { get; private set; }

    static partial void RegisterOptionalModules(PclHostBuilder builder)
    {
        // No host-hardcoded plugin pages. Sidecar ui.manifest injects groups/pages at runtime.
        _ = builder;
    }

    static partial void InitializeOptionalRuntime(IPclHost host)
    {
        // Start during DesktopHost.Initialize (splash still visible). Callers await Ensure*.
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

            string? executable = PluginSidecarPaths.ResolveExecutable();
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
            await PluginSidecarUiInjector.InjectAsync(host).ConfigureAwait(false);

            PluginOptionalRuntimeResult ready = new(
                PluginOptionalRuntimeStatus.Ready,
                "Plugin sidecar started; UI data-chain + feedback bridge ready.");
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
            _pnpHandlerRegistration?.Dispose();
            _pnpHandlerRegistration = null;
            PluginSidecarSupervisor.Instance.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
