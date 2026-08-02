// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using PCL.Application.Instances;
using PCL.Application.Launching;
using PCL.Application.Minecraft.Launch;
using PCL.Application.Settings;
using PCL.Desktop.Features.Launching.Views;
using PCL.Desktop.Features.Settings.Views;
using PCL.Desktop.Localization;
using PCL.Desktop.Diagnostics;
using PCL.Core.Logging;
using PCL.Desktop.Telemetry;

namespace PCL.Desktop.Features.Launching;

/// <summary>
/// Starts Minecraft: prep + <see cref="MinecraftLaunchCoordinator"/> + success/failure host hooks.
/// UI painting / dialogs / process tracking stay in the host via <see cref="StartMinecraftHost"/>.
/// </summary>
public sealed class StartMinecraftUseCase
{
    private StartMinecraftHost? _host;
    private MinecraftLaunchCoordinator? _coordinator;

    internal void Bind(StartMinecraftHost host, MinecraftLaunchCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(coordinator);
        _host = host;
        _coordinator = coordinator;
    }

    public Task ExecuteAsync(StartMinecraftRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(request, repairSession: null, cancellationToken);
    }

    internal Task ExecuteAsync(
        StartMinecraftRequest request,
        MinecraftRepairSession? repairSession,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_host is null || _coordinator is null)
            throw new InvalidOperationException("StartMinecraftUseCase 尚未 Bind。");

        return ExecuteCoreAsync(request, repairSession, _host, _coordinator, cancellationToken);
    }

    private static async Task ExecuteCoreAsync(
        StartMinecraftRequest request,
        MinecraftRepairSession? repairSession,
        StartMinecraftHost host,
        MinecraftLaunchCoordinator coordinator,
        CancellationToken outerToken)
    {
        ILaunchHomeSurface launchPage = request.Home;
        LaunchInstanceInfo instance = request.Instance;
        string? worldName = request.WorldName;
        string? serverAddress = request.ServerAddress;
        using TelemetryOperation launchOperation = LauncherTelemetry.StartOperation(
            "game.launch",
            "game.launch");
        LauncherTelemetry.CaptureEvent(
            "game_launch_started",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["repair_mode"] = repairSession is null ? "false" : "true",
                ["target"] = string.IsNullOrWhiteSpace(serverAddress) ? "singleplayer" : "multiplayer"
            });

        LoginProfileInfo? profile = host.ResolveProfile();
        if (profile is null)
        {
            launchOperation.Cancel();
            LauncherTelemetry.CaptureEvent(
                "game_launch_failed",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["stage"] = "profile_missing" });
            await TryRollbackRepairAsync(repairSession, "缺少账户档案").ConfigureAwait(false);
            await host.InvokeUiAsync(() =>
            {
                if (launchPage.IsLaunchInProgress)
                    launchPage.PageChangeToLogin();
                host.ShowNoProfileDialog();
            }).ConfigureAwait(false);
            return;
        }

        if (!launchPage.IsLaunchInProgress)
            launchPage.ShowLaunching(instance);

        await host.WaitForUiPaintAsync().ConfigureAwait(false);

        // The host token owns UI cancellation and repair reuse; the outer token belongs to the
        // caller. Both must stop the same launch pipeline.
        CancellationToken hostToken = host.AcquireLaunchCancellation(repairSession);
        using CancellationTokenSource? linkedCancellation = CreateLinkedCancellationSource(hostToken, outerToken);
        CancellationToken cancellationToken = linkedCancellation?.Token ?? hostToken;

        LauncherSettings? runtimeSettingsForRepair = null;

        try
        {
            string instanceDirectory = instance.InstanceDirectory;
            InstanceMetadata metadata = await InstanceMetadataStore.LoadAsync(
                    instanceDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            LauncherSettings runtimeSettings = await host.LoadSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
            runtimeSettingsForRepair = runtimeSettings;
            string method = MinecraftLaunchCoordinator.FormatLoginMethod(profile);
            host.PostUi(() => launchPage.LaunchingRefresh(
                AvaloniaLocalizationManager.GetText("Common.Action.Initialize", "初始化"),
                0d,
                method: method));

            MinecraftLaunchCoordinatorResult result = await coordinator.RunAsync(
                    new MinecraftLaunchCoordinatorRequest
                    {
                        Instance = instance,
                        Profile = profile,
                        Metadata = metadata,
                        Settings = runtimeSettings,
                        MinecraftRootDirectory = host.GetMinecraftRoot(instance),
                        PreferOfficialSource = runtimeSettings.DownloadSource !=
                                               DownloadSourcePreference.MirrorOnly,
                        WorldName = worldName,
                        ServerAddress = serverAddress,
                        Report = report =>
                        {
                            host.PostUi(() => launchPage.LaunchingRefresh(
                                report.StageName,
                                report.Progress,
                                report.IsLaunched,
                                report.Method,
                                report.DownloadSpeed));
                        },
                        Log = message => host.PostUi(() => host.AppendLog(message)),
                        RefreshProfileAsync = host.RefreshProfileAsync,
                        CreatePlanAsync = host.CreatePlanAsync,
                        RunPreLaunchCommandAsync = host.RunPreLaunchCommandAsync,
                        ApplyProcessPriority = host.ApplyProcessPriority,
                        ConfirmJavaDownloadAsync = host.ConfirmJavaDownloadAsync
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            repairSession?.Transaction.Commit();
            try
            {
                await host.StopRepairServerAsync().ConfigureAwait(false);
            }
            catch (Exception serverStopException)
            {
                DesktopFileLog.Warn(
                    "MinecraftRepairAI",
                    "Minecraft 已成功启动，但释放本地模型服务失败。",
                    serverStopException);
            }

            try
            {
                await host.OnSucceededAsync(new StartMinecraftSucceededArgs(
                    launchPage,
                    instance,
                    profile,
                    result,
                    runtimeSettings,
                    worldName,
                    serverAddress)).ConfigureAwait(false);
            }
            catch (Exception postEx)
            {
                DesktopFileLog.Warn("LaunchUI", "游戏已启动，但启动后界面处理发生异常。", postEx);
                host.AppendLog("启动后界面处理异常（游戏已启动）：" + postEx.Message);
            }

            try
            {
                await host.IncrementLaunchCountAsync(instance).ConfigureAwait(false);
            }
            catch (Exception countEx)
            {
                DesktopFileLog.Warn("LaunchHistory", $"记录实例 {instance.Name} 的启动次数失败。", countEx);
                host.AppendLog("记录启动次数失败：" + countEx.Message);
            }

            launchOperation.Complete();
            LauncherTelemetry.CaptureEvent("game_launch_succeeded");
        }
        catch (OperationCanceledException)
        {
            launchOperation.Cancel();
            LauncherTelemetry.CaptureEvent("game_launch_cancelled");
            await TryStopRepairServerAsync(host).ConfigureAwait(false);
            DesktopFileLog.Warn("LaunchUI", $"实例 {instance.Name} 的启动操作已取消。");
            await TryRollbackRepairAsync(repairSession, "启动取消").ConfigureAwait(false);
            await host.InvokeUiAsync(() =>
            {
                if (launchPage.IsLaunchInProgress)
                    launchPage.PageChangeToLogin();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            launchOperation.Fail(ex);
            LauncherTelemetry.CaptureException(ex, "game.launch");
            LauncherTelemetry.CaptureEvent(
                "game_launch_failed",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["stage"] = "launch_pipeline",
                    ["failure_category"] = TelemetryDataPolicy.NormalizeName(ex.GetType().Name)
                });
            DesktopFileLog.Error("LaunchUI", $"实例 {instance.Name} 启动失败。", ex);
            try
            {
                await host.OnFailedAsync(new StartMinecraftFailedArgs(
                    launchPage,
                    instance,
                    profile,
                    ex,
                    runtimeSettingsForRepair,
                    repairSession,
                    worldName,
                    serverAddress)).ConfigureAwait(false);
            }
            catch (Exception handlerException)
            {
                DesktopFileLog.Error(
                    "LaunchUI",
                    $"实例 {instance.Name} 的错误处理器异常，改用基础失败提示。",
                    handlerException);
                await TryRollbackRepairAsync(repairSession, "错误处理器异常").ConfigureAwait(false);
                await host.InvokeUiAsync(() =>
                {
                    if (launchPage.IsLaunchInProgress)
                        launchPage.PageChangeToLogin();
                    host.AppendLog("错误处理器异常：" + handlerException.Message);
                    host.ShowLaunchFailedDialog(ex.Message);
                }).ConfigureAwait(false);
            }
        }
    }

    internal static CancellationTokenSource? CreateLinkedCancellationSource(
        CancellationToken hostToken,
        CancellationToken outerToken)
    {
        if (!outerToken.CanBeCanceled || outerToken == hostToken)
            return null;
        return CancellationTokenSource.CreateLinkedTokenSource(hostToken, outerToken);
    }

    private static async Task TryStopRepairServerAsync(StartMinecraftHost host)
    {
        try
        {
            await host.StopRepairServerAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            DesktopFileLog.Warn(
                "MinecraftRepairAI",
                "停止 Minecraft 修复模型服务失败，继续完成启动状态清理。",
                exception);
        }
    }

    private static async Task TryRollbackRepairAsync(MinecraftRepairSession? repairSession, string reason)
    {
        if (repairSession is null)
            return;

        try
        {
            await repairSession.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            DesktopFileLog.Error(
                "MinecraftRepairAI",
                $"Minecraft 修复回滚失败；原因={reason}。",
                exception);
        }
    }
}

public sealed record StartMinecraftRequest(
    ILaunchHomeSurface Home,
    LaunchInstanceInfo Instance,
    string? WorldName = null,
    string? ServerAddress = null);

internal sealed record StartMinecraftSucceededArgs(
    ILaunchHomeSurface Home,
    LaunchInstanceInfo Instance,
    LoginProfileInfo OriginalProfile,
    MinecraftLaunchCoordinatorResult Result,
    LauncherSettings Settings,
    string? WorldName,
    string? ServerAddress);

internal sealed record StartMinecraftFailedArgs(
    ILaunchHomeSurface Home,
    LaunchInstanceInfo Instance,
    LoginProfileInfo Profile,
    Exception Exception,
    LauncherSettings? RuntimeSettings,
    MinecraftRepairSession? RepairSession,
    string? WorldName,
    string? ServerAddress);

/// <summary>Host UI / process hooks required by <see cref="StartMinecraftUseCase"/>.</summary>
internal sealed class StartMinecraftHost
{
    public required Func<LoginProfileInfo?> ResolveProfile { get; init; }

    public required Func<MinecraftRepairSession?, CancellationToken> AcquireLaunchCancellation { get; init; }

    public required Func<LaunchInstanceInfo, string> GetMinecraftRoot { get; init; }

    public required Func<Task> WaitForUiPaintAsync { get; init; }

    public required Action<Action> PostUi { get; init; }

    public required Func<Action, Task> InvokeUiAsync { get; init; }

    public required Action<string> AppendLog { get; init; }

    public required Action ShowNoProfileDialog { get; init; }

    public required Action<string> ShowLaunchFailedDialog { get; init; }

    public required Func<CancellationToken, Task<LauncherSettings>> LoadSettingsAsync { get; init; }

    /// <summary>Login-stage validate/refresh; <c>status</c> is optional sub-step log text.</summary>
    public required Func<
        LoginProfileInfo,
        Action<string>?,
        CancellationToken,
        Task<LoginProfileInfo>> RefreshProfileAsync { get; init; }

    public required Func<
        LaunchInstanceInfo,
        LoginProfileInfo,
        string,
        CancellationToken,
        string?,
        InstanceMetadata?,
        string?,
        Task<MinecraftProcessLaunchPlan>> CreatePlanAsync { get; init; }

    public required Func<string, bool, string, CancellationToken, Task> RunPreLaunchCommandAsync { get; init; }

    public required Action<Process, LauncherSettings> ApplyProcessPriority { get; init; }

    public required Func<string, CancellationToken, Task<bool>> ConfirmJavaDownloadAsync { get; init; }

    public required Func<Task> StopRepairServerAsync { get; init; }

    public required Func<StartMinecraftSucceededArgs, Task> OnSucceededAsync { get; init; }

    public required Func<StartMinecraftFailedArgs, Task> OnFailedAsync { get; init; }

    public required Func<LaunchInstanceInfo, Task> IncrementLaunchCountAsync { get; init; }
}
