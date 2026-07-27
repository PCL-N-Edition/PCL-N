// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PCL.Application.Downloads;
using PCL.Application.Instances;
using PCL.Application.Launching;
using PCL.Application.Minecraft.Java;
using PCL.Application.Minecraft.Launch;
using PCL.Application.Settings;
using PCL.Desktop.Features.Launching.Views;
using PCL.Desktop.Features.Shared;
using PCL.Desktop.Localization;
using PCL.Domain.Minecraft.Java;
using PCL.Domain.Minecraft.Launch;
using PCL.Core.Logging;
using PCL.Platform.Java;
using PCL.Platform.Paths;

namespace PCL.Desktop.Features.Launching;

/// <summary>
/// Weighted launch stages aligned with the legacy WPF <c>ModLaunch</c> pipeline.
/// </summary>
internal static class MinecraftLaunchStages
{
    public const double GetJava = 4d;
    public const double Login = 15d;
    public const double CompleteFiles = 15d;
    public const double GetArguments = 2d;
    public const double ExtractNatives = 2d;
    public const double PreLaunch = 1d;
    public const double CustomCommand = 1d;
    public const double StartProcess = 2d;
    public const double WaitWindow = 1d;
    public const double End = 1d;

    public const double Total =
        GetJava + Login + CompleteFiles + GetArguments + ExtractNatives +
        PreLaunch + CustomCommand + StartProcess + WaitWindow + End;

    public static double ProgressAt(double completedWeight) =>
        Math.Clamp(completedWeight / Total, 0d, 1d);
}

internal sealed record MinecraftLaunchStageReport(
    string StageName,
    double Progress,
    bool IsLaunched = false,
    string? Method = null,
    string? DownloadSpeed = null);

internal sealed record MinecraftLaunchCoordinatorResult(
    Process Process,
    MinecraftProcessLaunchPlan Plan,
    string JavaExecutablePath,
    LoginProfileInfo Profile,
    Guid SessionId,
    Task<MinecraftLaunchFaultReport?>? FaultReport);

internal sealed record MinecraftStartedProcess(
    Process Process,
    Task<MinecraftLaunchFaultReport?>? FaultReport = null);

internal sealed class MinecraftLaunchFailureException : InvalidOperationException
{
    public MinecraftLaunchFailureException(string message, MinecraftLaunchFaultReport? faultReport = null)
        : base(message)
    {
        FaultReport = faultReport;
    }

    public MinecraftLaunchFaultReport? FaultReport { get; }
}

internal sealed class MinecraftLaunchCoordinator
{
    /// <summary>How long to watch for an early crash after Process.Start.</summary>
    private static readonly TimeSpan EarlyExitGracePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WindowPollInterval = TimeSpan.FromMilliseconds(150);

    private readonly MinecraftVanillaInstallService _installService;

    public MinecraftLaunchCoordinator(MinecraftVanillaInstallService installService)
    {
        _installService = installService ?? throw new ArgumentNullException(nameof(installService));
    }

    public async Task<MinecraftLaunchCoordinatorResult> RunAsync(
        MinecraftLaunchCoordinatorRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        double completed = 0d;
        string method = FormatLoginMethod(request.Profile);
        PortableLog.Info("Launch", $"开始启动实例 {request.Instance.Name}；登录方式={method}。");
        PortableLog.Debug(
            "Launch",
            $"启动请求：实例目录={request.Instance.InstanceDirectory}；MinecraftRoot={request.MinecraftRootDirectory}；" +
            $"服务器={request.ServerAddress ?? "(无)"}；世界={request.WorldName ?? "(无)"}；优先官方源={request.PreferOfficialSource}。");

        try
        {

        string javaExecutable = await RunStageWithHeartbeatAsync(
                request,
                StageName("Minecraft.Launch.Stage.GetJava", "获取 Java"),
                completed,
                MinecraftLaunchStages.GetJava,
                method,
                token => ResolveJavaExecutableAsync(request, token),
                cancellationToken)
            .ConfigureAwait(false);
        completed += MinecraftLaunchStages.GetJava;
        request.Log?.Invoke("选择的 Java：" + javaExecutable);

        // Login stage owns all online session work (validate / refresh / silent re-auth).
        // Downstream stages must use the returned profile so launch args carry a fresh token.
        LoginProfileInfo profile = await RunStageWithHeartbeatAsync(
                request,
                StageName("Minecraft.Launch.Stage.Login", "登录"),
                completed,
                MinecraftLaunchStages.Login,
                method,
                async token =>
                {
                    request.Log?.Invoke(
                        "登录阶段：正在验证账户「" + request.Profile.Username + "」（" + method + "）…");
                    LoginProfileInfo verified = await request.RefreshProfileAsync(
                            request.Profile,
                            status => request.Log?.Invoke("登录阶段：" + status),
                            token)
                        .ConfigureAwait(false);
                    request.Log?.Invoke(
                        "登录阶段：账户验证完成 — " + verified.Username +
                        "（" + FormatLoginMethod(verified) + "）");
                    return verified;
                },
                cancellationToken)
            .ConfigureAwait(false);
        method = FormatLoginMethod(profile);
        completed += MinecraftLaunchStages.Login;
        Report(request, StageName("Minecraft.Launch.Stage.Login", "登录"), completed, method: method);

        Report(request, StageName("Minecraft.Launch.Stage.CompleteFiles", "补全文件"), completed, method: method);
        await CompleteFilesAsync(request, completed, method, cancellationToken).ConfigureAwait(false);
        completed += MinecraftLaunchStages.CompleteFiles;
        Report(request, StageName("Minecraft.Launch.Stage.CompleteFiles", "补全文件"), completed, method: method);

        MinecraftProcessLaunchPlan plan = await RunStageWithHeartbeatAsync(
                request,
                StageName("Minecraft.Launch.Stage.GetArguments", "获取启动参数"),
                completed,
                MinecraftLaunchStages.GetArguments + MinecraftLaunchStages.ExtractNatives,
                method,
                token => request.CreatePlanAsync(
                    request.Instance,
                    profile,
                    javaExecutable,
                    token,
                    request.WorldName,
                    request.Metadata,
                    request.ServerAddress),
                cancellationToken)
            .ConfigureAwait(false);
        completed += MinecraftLaunchStages.GetArguments;
        Report(request, StageName("Minecraft.Launch.Stage.ExtractNatives", "解压 Natives"), completed, method: method);
        completed += MinecraftLaunchStages.ExtractNatives;
        Report(request, StageName("Minecraft.Launch.Stage.ExtractNatives", "解压 Natives"), completed, method: method);

        Report(request, StageName("Minecraft.Launch.Stage.PreLaunch", "预启动处理"), completed, method: method);
        EnsureWorkingDirectory(plan.StartInfo.WorkingDirectory);
        completed += MinecraftLaunchStages.PreLaunch;
        Report(request, StageName("Minecraft.Launch.Stage.PreLaunch", "预启动处理"), completed, method: method);

        await RunStageWithHeartbeatAsync(
                request,
                StageName("Minecraft.Launch.Stage.CustomCommand", "执行自定义命令"),
                completed,
                MinecraftLaunchStages.CustomCommand,
                method,
                async token =>
                {
                    await RunCustomCommandIfNeededAsync(request, plan, token).ConfigureAwait(false);
                    return 0;
                },
                cancellationToken)
            .ConfigureAwait(false);
        completed += MinecraftLaunchStages.CustomCommand;
        Report(request, StageName("Minecraft.Launch.Stage.CustomCommand", "执行自定义命令"), completed, method: method);

        Report(request, StageName("Minecraft.Launch.Stage.StartProcess", "启动进程"), completed, method: method);
        // Do not spawn the game if the user already cancelled during earlier stages.
        cancellationToken.ThrowIfCancellationRequested();
        // Normalize FileName before logging so the UI shows the real executable used.
        NormalizeJavaExecutableForLaunch(plan.StartInfo);
        request.Log?.Invoke(
            "启动 Java：" + plan.StartInfo.FileName +
            "\n工作目录：" + plan.StartInfo.WorkingDirectory +
            "\nNatives：" + plan.NativesDirectory +
            "\n参数预览：" + Truncate(plan.StartInfo.Arguments ?? string.Empty, 240));

        Process? launchedProcess = null;
        Guid launchedSessionId = Guid.Empty;
        try
        {
            MinecraftStartedProcess startedProcess = StartProcess(plan, request.Log);
            Process process = startedProcess.Process;
            launchedProcess = process;
            GameSessionSnapshot session = GameSessionRegistry.Shared.Start(request.Instance.Name, process.Id);
            launchedSessionId = session.SessionId;
            GameSessionRegistry.Shared.PublishOutput(
                session.SessionId,
                GameProcessOutputChannel.Launcher,
                "Minecraft process started.");
            _ = ObserveProcessExitAsync(process, session.SessionId);
            request.ApplyProcessPriority(process, request.Settings);
            completed += MinecraftLaunchStages.StartProcess;
            string processMethod = "PID " + process.Id.ToString(CultureInfo.InvariantCulture);
            Report(
                request,
                StageName("Minecraft.Launch.Stage.StartProcess", "启动进程"),
                completed,
                method: processMethod);

            // Mark launched so the title flips to "游戏已启动", then briefly watch for early crash.
            // Do not flood the UI with heartbeat posts during this wait (was freezing Avalonia).
            Report(
                request,
                StageName("Minecraft.Launch.Stage.WaitWindow", "等待游戏窗口"),
                completed,
                isLaunched: true,
                method: processMethod);
            await WaitForGameAppearanceAsync(
                    process,
                    cancellationToken,
                    request.Log,
                    startedProcess.FaultReport)
                .ConfigureAwait(false);
            completed += MinecraftLaunchStages.WaitWindow;
            Report(
                request,
                StageName("Minecraft.Launch.Stage.WaitWindow", "等待游戏窗口"),
                completed,
                isLaunched: true,
                method: processMethod);

            Report(
                request,
                StageName("Minecraft.Launch.Stage.End", "结束处理"),
                completed,
                isLaunched: true,
                method: processMethod);
            completed += MinecraftLaunchStages.End;
            Report(
                request,
                StageName("Minecraft.Launch.Stage.End", "结束处理"),
                completed,
                isLaunched: true,
                method: processMethod);

            PortableLog.Info("Launch", $"实例 {request.Instance.Name} 启动成功；PID={process.Id}；会话={session.SessionId}。");
            // Success: do not kill on the outer cancel path.
            launchedProcess = null;
            launchedSessionId = Guid.Empty;
            return new MinecraftLaunchCoordinatorResult(
                process,
                plan,
                javaExecutable,
                profile,
                session.SessionId,
                startedProcess.FaultReport);
        }
        catch (OperationCanceledException)
        {
            // Process may already be alive (e.g. cancel during WaitWindow). Terminate it so the
            // game window does not orphan without the launcher tracking "running" state.
            TryTerminateIncompleteLaunch(launchedProcess, launchedSessionId, request.Log);
            throw;
        }
        catch
        {
            // Process.Start can succeed before session registration, priority assignment, or
            // the early-crash observation stage fails. Do not leave that process detached from
            // the launcher's lifecycle when the overall launch did not complete successfully.
            TryTerminateIncompleteLaunch(launchedProcess, launchedSessionId, request.Log);
            throw;
        }
        }
        catch (OperationCanceledException)
        {
            PortableLog.Warn("Launch", $"实例 {request.Instance.Name} 的启动已取消；最后进度={MinecraftLaunchStages.ProgressAt(completed):P0}。");
            throw;
        }
        catch (Exception ex)
        {
            PortableLog.Error(ex, "Launch", $"实例 {request.Instance.Name} 启动失败；最后进度={MinecraftLaunchStages.ProgressAt(completed):P0}。");
            throw;
        }
    }

    /// <summary>
    /// Ends a game process started during a launch that did not complete successfully.
    /// Safe to call when <paramref name="process"/> is null or already exited.
    /// </summary>
    internal static void TryTerminateIncompleteLaunch(
        Process? process,
        Guid sessionId,
        Action<string>? log = null)
    {
        if (process is null && sessionId == Guid.Empty)
            return;

        try
        {
            if (process is not null && !process.HasExited)
            {
                int pid = process.Id;
                log?.Invoke("启动未完成，正在结束游戏进程（PID " +
                            pid.ToString(CultureInfo.InvariantCulture) + "）…");
                PortableLog.Warn("Launch", $"启动未完成：结束游戏进程树 PID={pid}。");
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5000))
                    PortableLog.Warn("Launch", $"启动未完成：进程 PID={pid} 在 5s 内未退出。");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or
                                       NotSupportedException or AggregateException)
        {
            PortableLog.Warn(ex, "Launch", "启动未完成时结束游戏进程失败。");
            log?.Invoke("结束游戏进程失败：" + ex.Message);
        }
        finally
        {
            if (sessionId != Guid.Empty)
            {
                try
                {
                    GameSessionRegistry.Shared.Complete(sessionId, exitCode: -1, terminated: true);
                }
                catch (Exception ex)
                {
                    PortableLog.Warn(ex, "Launch", $"启动未完成时完成会话 {sessionId} 失败。");
                }
            }
        }
    }

    private static async Task<T> RunStageWithHeartbeatAsync<T>(
        MinecraftLaunchCoordinatorRequest request,
        string stage,
        double completedBefore,
        double stageWeight,
        string method,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken,
        bool isLaunched = false)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        PortableLog.Info("Launch", $"进入启动阶段：{stage}。");
        PortableLog.Debug(
            "Launch",
            $"阶段参数：名称={stage}；起始权重={completedBefore:0.##}；阶段权重={stageWeight:0.##}；方式={method}；已启动={isLaunched}。");
        Report(request, stage, completedBefore, isLaunched, method);
        Task<T> workTask;
        try
        {
            workTask = work(cancellationToken);
        }
        catch (Exception ex)
        {
            PortableLog.Error(ex, "Launch", $"启动阶段 {stage} 在创建任务时失败。");
            throw;
        }
        double softFraction = 0d;
        while (!workTask.IsCompleted)
        {
            softFraction = Math.Min(0.92d, softFraction + 0.05d);
            PortableLog.RealTime(
                "Launch",
                $"阶段运行中：{stage}；阶段软进度={softFraction:P0}；总权重={completedBefore + (stageWeight * softFraction):0.##}/{MinecraftLaunchStages.Total:0.##}。");
            Report(
                request,
                stage,
                completedBefore + (stageWeight * softFraction),
                isLaunched,
                method);
            try
            {
                await Task.WhenAny(workTask, Task.Delay(120, cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (workTask.IsCompleted)
            {
                break;
            }
        }

        T result;
        try
        {
            result = await workTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            PortableLog.Warn("Launch", $"启动阶段 {stage} 已取消；耗时={stopwatch.Elapsed.TotalSeconds:0.###}s。");
            throw;
        }
        catch (Exception ex)
        {
            PortableLog.Error(ex, "Launch", $"启动阶段 {stage} 失败；耗时={stopwatch.Elapsed.TotalSeconds:0.###}s。");
            throw;
        }
        Report(request, stage, completedBefore + stageWeight, isLaunched, method);
        PortableLog.Info("Launch", $"完成启动阶段：{stage}；耗时={stopwatch.Elapsed.TotalSeconds:0.###}s。");
        return result;
    }

    public static string FormatLoginMethod(LoginProfileInfo profile) =>
        profile.Kind switch
        {
            LaunchLoginProfileKind.Microsoft => AvaloniaLocalizationManager.GetText(
                "Launch.Account.Type.Microsoft",
                "微软"),
            LaunchLoginProfileKind.ThirdParty => FormatThirdPartyMethod(profile),
            LaunchLoginProfileKind.LittleSkin => "LittleSkin",
            LaunchLoginProfileKind.NCloud => "N Cloud",
            _ => AvaloniaLocalizationManager.GetText("Launch.Account.Type.Offline", "离线")
        };

    public static string PreferJavaExecutable(string javaExecutablePath, bool forceConsole)
    {
        if (string.IsNullOrWhiteSpace(javaExecutablePath) || !OperatingSystem.IsWindows())
            return javaExecutablePath;

        string directory = Path.GetDirectoryName(javaExecutablePath) ?? string.Empty;
        string fileName = Path.GetFileName(javaExecutablePath);
        if (forceConsole &&
            string.Equals(fileName, "javaw.exe", StringComparison.OrdinalIgnoreCase))
        {
            string consoleJava = Path.Combine(directory, "java.exe");
            if (File.Exists(consoleJava))
                return consoleJava;
        }

        if (!forceConsole &&
            string.Equals(fileName, "java.exe", StringComparison.OrdinalIgnoreCase))
        {
            string windowJava = Path.Combine(directory, "javaw.exe");
            if (File.Exists(windowJava))
                return windowJava;
        }

        return javaExecutablePath;
    }

    public static async Task<string> ResolveJavaExecutableAsync(
        MinecraftLaunchCoordinatorRequest request,
        CancellationToken cancellationToken)
    {
        InstanceMetadata metadata = request.Metadata;
        bool forceConsole = request.Settings.GetBooleanOption(
            "LaunchAdvanceNoJavaw",
            LauncherSettingDefaults.GetBoolean("LaunchAdvanceNoJavaw"));
        MinecraftLaunchProfile profile = BuildLaunchProfile(request.Instance);

        // Always prefer console java.exe for launch reliability (stderr + real exit codes).
        // javaw hides crashes and was producing "empty exit code" UX.
        const bool launchForceConsole = true;
        _ = forceConsole;

        // 1) Instance forced path
        if (metadata.JavaSelectionMode == 2 &&
            JavaRuntimeCatalog.TryResolveExistingJavaPath(metadata.SelectedJavaPath, out string instanceJava))
        {
            request.Log?.Invoke("使用实例指定的 Java：" + instanceJava);
            return PreferJavaExecutable(instanceJava, launchForceConsole);
        }

        // Load the same catalog Settings uses (custom roots + disabled flags).
        IReadOnlyList<JavaRuntimeCandidate> catalog =
            await JavaRuntimeCatalog.LoadAsync(request.Settings, cancellationToken).ConfigureAwait(false);

        JavaRequirementResolution requirement = JavaRuntimeRequirementResolver.Resolve(profile);
        if (!requirement.Success)
        {
            throw new InvalidOperationException(
                requirement.Detail ?? "无法解析该版本的 Java 要求。");
        }

        // 2) Global explicit selection from Settings (when not instance-auto)
        bool forceAuto = metadata.JavaSelectionMode == 1;
        bool hasGlobalSelection =
            request.Settings.TryGetTextOption(LauncherSettingKeys.LaunchSelectedJava, out string? globalJava) &&
            !string.IsNullOrWhiteSpace(globalJava);

        if (!forceAuto &&
            hasGlobalSelection &&
            JavaRuntimeCatalog.TryResolveExistingJavaPath(globalJava, out string resolvedGlobal) &&
            JavaRuntimeCatalog.IsJavaPathEnabled(request.Settings, resolvedGlobal))
        {
            request.Log?.Invoke("使用设置中选定的 Java：" + resolvedGlobal);
            return PreferJavaExecutable(resolvedGlobal, launchForceConsole);
        }

        // 3) Auto-select from Settings catalog by version range
        JavaRuntimeCandidate? best = JavaRuntimeCatalog.SelectBest(catalog, requirement.Range);
        if (best is not null)
        {
            string path = best.Installation.JavaExecutablePath;
            request.Log?.Invoke("自动选择 Java：" + path + " (" + best.Installation.MajorVersion + ")");
            return PreferJavaExecutable(path, launchForceConsole);
        }

        // 4) Auto-download Mojang runtime
        JavaSelectionResult fakeFailure = JavaSelectionResult.Failed(
            requirement,
            JavaSelectionFailureReason.NoCompatibleJava,
            suggestedDownloadComponent: JavaRuntimeAcquisitionPlanner.Plan(requirement, profile.HasForge).DownloadComponent);

        string? downloaded = await TryAutoDownloadJavaAsync(
                request,
                profile,
                fakeFailure,
                launchForceConsole,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(downloaded))
            return downloaded;

        throw new InvalidOperationException(BuildNoCompatibleJavaMessage(fakeFailure));
    }

    private static async Task<string?> TryAutoDownloadJavaAsync(
        MinecraftLaunchCoordinatorRequest request,
        MinecraftLaunchProfile profile,
        JavaSelectionResult selection,
        bool forceConsole,
        CancellationToken cancellationToken)
    {
        JavaRuntimeAcquisitionDecision acquisition =
            JavaRuntimeAcquisitionPlanner.Plan(selection.Requirement, profile.HasForge);
        if (!acquisition.CanAutoDownload ||
            string.IsNullOrWhiteSpace(acquisition.DownloadComponent))
        {
            return null;
        }

        string component = acquisition.DownloadComponent;
        string versionLabel = acquisition.JavaVersionCode ?? component;
        if (request.ConfirmJavaDownloadAsync is not null)
        {
            bool confirmed = await request.ConfirmJavaDownloadAsync(versionLabel, cancellationToken)
                .ConfigureAwait(false);
            if (!confirmed)
                throw new InvalidOperationException("已取消自动下载 Java " + versionLabel + "。");
        }

        request.Log?.Invoke("未找到兼容 Java，开始自动下载 Java " + versionLabel + "…");
        DefaultPlatformPathProvider paths = new();
        string runtimeRoot = JavaRuntimeInstaller.GetDefaultRuntimeRoot(paths);
        using HttpJavaRuntimeMetadataProvider metadata = new();
        JavaRuntimeInstaller installer = new(metadata);
        Progress<JavaRuntimeInstallProgress> progress = new(update =>
        {
            request.Report(new MinecraftLaunchStageReport(
                StageName("Minecraft.Launch.Stage.GetJava", "获取 Java") + " · " + update.Stage,
                Math.Clamp(update.Progress * (MinecraftLaunchStages.GetJava / MinecraftLaunchStages.Total), 0d, 0.09d),
                Method: update.Detail ?? versionLabel));
        });

        string javaPath = await installer.InstallAsync(component, runtimeRoot, progress, cancellationToken)
            .ConfigureAwait(false);
        request.Log?.Invoke("Java 已安装：" + javaPath);

        // Re-scan catalog so the new runtime is preferred next time.
        IReadOnlyList<JavaRuntimeCandidate> catalog =
            await JavaRuntimeCatalog.LoadAsync(request.Settings, cancellationToken).ConfigureAwait(false);
        JavaRequirementResolution requirement = JavaRuntimeRequirementResolver.Resolve(profile);
        if (requirement.Success)
        {
            JavaRuntimeCandidate? best = JavaRuntimeCatalog.SelectBest(catalog, requirement.Range);
            if (best is not null)
            {
                string path = forceConsole
                    ? best.Installation.JavaExecutablePath
                    : best.Installation.WindowedJavaExecutablePath
                      ?? best.Installation.JavaExecutablePath;
                return PreferJavaExecutable(path, forceConsole);
            }
        }

        return PreferJavaExecutable(javaPath, forceConsole);
    }

    public static MinecraftLaunchProfile BuildLaunchProfile(LaunchInstanceInfo instance)
    {
        MinecraftVersionJsonInfo info = MinecraftVersionJsonInspector.Read(instance);
        Version? vanillaVersion = TryParseMinecraftVanillaVersion(info.MinecraftVersionId);
        string? neoForgeVersion = MinecraftLoaderLibraryDetector.DetectVersion(
            info.LoaderEntries,
            "net.neoforged:neoforge",
            "net.neoforge:forge");
        // Do not use bare "forge" needle — it matches NeoForge library names.
        string? forgeVersion = neoForgeVersion is null
            ? MinecraftLoaderLibraryDetector.DetectVersion(info.LoaderEntries, "net.minecraftforge:forge")
            : null;
        string? cleanroomVersion = MinecraftLoaderLibraryDetector.DetectVersion(
            info.LoaderEntries,
            "com.cleanroommc:cleanroom");

        return new MinecraftLaunchProfile
        {
            InstanceId = instance.Name,
            VanillaVersion = vanillaVersion,
            HasReliableVanillaVersion = vanillaVersion is not null,
            ReleaseTime = TryReadReleaseTime(instance),
            ManifestJavaMajorVersion = TryReadManifestJavaMajor(instance),
            ManifestJavaComponent = TryReadManifestJavaComponent(instance),
            HasOptiFine = HasLoaderNeedle(instance, "optifine"),
            // Treat NeoForge as Forge-family for Java constraints, without dual labeling elsewhere.
            HasForge = forgeVersion is not null ||
                       neoForgeVersion is not null ||
                       HasLoaderNeedle(instance, "net.minecraftforge:forge") ||
                       HasLoaderNeedle(instance, "net.neoforged:neoforge"),
            ForgeVersion = forgeVersion ?? neoForgeVersion,
            HasCleanroom = cleanroomVersion is not null || HasLoaderNeedle(instance, "cleanroom"),
            CleanroomVersion = cleanroomVersion,
            HasFabric = HasLoaderNeedle(instance, "fabric-loader", "quilt-loader", "legacyfabric", "legacy-fabric"),
            HasLiteLoader = HasLoaderNeedle(instance, "liteloader"),
            HasLabyMod = HasLoaderNeedle(instance, "labymod")
        };
    }

    public static async Task WaitForGameAppearanceAsync(
        Process process,
        CancellationToken cancellationToken,
        Action<string>? log = null,
        Task<MinecraftLaunchFaultReport?>? faultReportTask = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + EarlyExitGracePeriod;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PortableLog.RealTime("GameProcess", $"等待游戏稳定运行；PID={process.Id}；HasExited={process.HasExited}。");

            if (process.HasExited)
                throw await CreateEarlyExitExceptionAsync(process, faultReportTask).ConfigureAwait(false);

            await Task.Delay(WindowPollInterval, cancellationToken).ConfigureAwait(false);
        }

        // Still alive after grace → success. Do not require MainWindowHandle (java/javaw often lag).
        if (process.HasExited)
            throw await CreateEarlyExitExceptionAsync(process, faultReportTask).ConfigureAwait(false);

        log?.Invoke("游戏进程仍在运行（PID " + process.Id.ToString(CultureInfo.InvariantCulture) + "）。");
    }

    private static async Task ObserveProcessExitAsync(Process process, Guid sessionId)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            GameSessionRegistry.Shared.Complete(sessionId, process.ExitCode);
            PortableLog.Info("GameProcess", $"游戏进程已退出；PID={process.Id}；ExitCode={process.ExitCode}；会话={sessionId}。");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            GameSessionRegistry.Shared.Complete(sessionId, -1);
            PortableLog.Warn(exception, "GameProcess", $"无法读取游戏进程退出状态；会话={sessionId}。");
        }
    }

    private static void NormalizeJavaExecutableForLaunch(ProcessStartInfo startInfo)
    {
        // Log-time normalize only: StartProcess decides final java/javaw.
        _ = startInfo;
    }

    private static string TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited
                ? process.ExitCode.ToString(CultureInfo.InvariantCulture)
                : "未知（进程仍在运行）";
        }
        catch (Exception)
        {
            return "未知";
        }
    }

    private static async Task<MinecraftLaunchFailureException> CreateEarlyExitExceptionAsync(
        Process process,
        Task<MinecraftLaunchFaultReport?>? faultReportTask)
    {
        MinecraftLaunchFaultReport? fault = null;
        if (faultReportTask is not null)
        {
            Task completed = await Task.WhenAny(
                    faultReportTask,
                    Task.Delay(TimeSpan.FromMilliseconds(750)))
                .ConfigureAwait(false);
            if (ReferenceEquals(completed, faultReportTask))
                fault = await faultReportTask.ConfigureAwait(false);
        }

        string message = "游戏进程在启动后立即退出，退出码：" + TryGetExitCode(process) + "。";
        if (fault is not null)
        {
            message += $"\nJvm.NET 已定位到：{fault.Subsystem}/{fault.Stage}（{fault.Code}）。" +
                       "\n" + fault.Message;
        }
        else
        {
            message += "\n请检查版本目录下的 LatestLaunch-PCLN.bat，或查看游戏日志（logs/latest.log）。";
        }
        return new MinecraftLaunchFailureException(message, fault);
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
            return text;
        return text[..max] + "…";
    }

    private async Task CompleteFilesAsync(
        MinecraftLaunchCoordinatorRequest request,
        double completedBeforeStage,
        string method,
        CancellationToken cancellationToken)
    {
        // Fast path: client jar present → skip full RepairAsync (was freezing launches).
        string versionJson = request.Instance.VersionJsonPath;
        MinecraftVersionJsonInfo versionInfo = MinecraftVersionJsonInspector.Read(request.Instance);
        string? clientJar = MinecraftVersionFileResolver.ResolveJarPath(
                                request.MinecraftRootDirectory,
                                request.Instance.InstanceDirectory,
                                versionInfo.MinecraftVersionId)
                            ?? MinecraftVersionFileResolver.ResolveJarPath(
                                request.MinecraftRootDirectory,
                                request.Instance.InstanceDirectory,
                                request.Instance.Name);
        bool looksInstalled = clientJar is not null && File.Exists(versionJson);
        if (looksInstalled)
        {
            request.Log?.Invoke("版本文件已存在，跳过完整补全（快速路径）。");
            request.Report(new MinecraftLaunchStageReport(
                StageName("Minecraft.Launch.Stage.CompleteFiles", "补全文件"),
                MinecraftLaunchStages.ProgressAt(completedBeforeStage + MinecraftLaunchStages.CompleteFiles),
                Method: method));
            return;
        }

        Progress<MinecraftInstallProgress> progress = new(update =>
        {
            double stageFraction = Math.Clamp(update.Progress, 0d, 1d);
            double weightProgress = completedBeforeStage + (MinecraftLaunchStages.CompleteFiles * stageFraction);
            string? speed = update.SpeedBytesPerSecond > 0
                ? FormatSpeed(update.SpeedBytesPerSecond)
                : null;
            string stage = StageName("Minecraft.Launch.Stage.CompleteFiles", "补全文件");
            if (!string.IsNullOrWhiteSpace(update.Detail))
                stage = stage + " · " + update.Detail;

            request.Report(new MinecraftLaunchStageReport(
                stage,
                MinecraftLaunchStages.ProgressAt(weightProgress),
                Method: method,
                DownloadSpeed: speed));
        });

        await _installService.RepairAsync(
                new MinecraftRepairRequest
                {
                    VersionId = request.Instance.Name,
                    VersionJsonPath = request.Instance.VersionJsonPath,
                    MinecraftRootDirectory = request.MinecraftRootDirectory,
                    InstanceDirectory = request.Instance.InstanceDirectory,
                    PreferOfficialSource = request.PreferOfficialSource
                },
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task RunCustomCommandIfNeededAsync(
        MinecraftLaunchCoordinatorRequest request,
        MinecraftProcessLaunchPlan plan,
        CancellationToken cancellationToken)
    {
        string preLaunchCommand = FirstNonEmpty(
            request.Metadata.PreLaunchCommand,
            request.Settings.GetTextOption(
                "LaunchAdvanceRun",
                LauncherSettingDefaults.GetText("LaunchAdvanceRun"))) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(preLaunchCommand))
            return;

        bool waitForPreLaunchCommand = !string.IsNullOrWhiteSpace(request.Metadata.PreLaunchCommand)
            ? request.Metadata.WaitForPreLaunchCommand
            : request.Settings.GetBooleanOption(
                "LaunchAdvanceRunWait",
                LauncherSettingDefaults.GetBoolean("LaunchAdvanceRunWait"));

        await request.RunPreLaunchCommandAsync(
                preLaunchCommand,
                waitForPreLaunchCommand,
                plan.StartInfo.WorkingDirectory,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static MinecraftStartedProcess StartProcess(MinecraftProcessLaunchPlan plan, Action<string>? log)
    {
        if (plan.JvmHostRequest is { } hostRequest)
        {
            // Keep the traditional command line as a diagnostic/fallback artifact even though
            // the live launch is performed by the host process.
            TryWriteLatestLaunchBatch(plan.StartInfo);
            log?.Invoke("实验性 Jvm.NET Host 已启用；游戏将在隔离 Host 进程中运行。");
            MinecraftJvmHostProcessHandle handle = MinecraftJvmHostProcessLauncher.Start(hostRequest, log);
            return new MinecraftStartedProcess(handle.Process, handle.FaultReport);
        }

        ProcessStartInfo startInfo = plan.StartInfo;

        // Prefer java.exe (matches WPF LatestLaunch.bat). Do NOT permanently redirect stdio —
        // Minecraft logs heavily; an undrained redirected stderr pipe freezes/kills the process.
        if (OperatingSystem.IsWindows() &&
            string.Equals(Path.GetFileName(startInfo.FileName), "javaw.exe", StringComparison.OrdinalIgnoreCase))
        {
            string? dir = Path.GetDirectoryName(startInfo.FileName);
            string console = Path.Combine(dir ?? string.Empty, "java.exe");
            if (File.Exists(console))
                startInfo.FileName = console;
        }

        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardError = false;
        startInfo.RedirectStandardOutput = false;
        startInfo.RedirectStandardInput = false;
        // Always hide console — flashing black CMD is poor UX. Errors go to game logs / LatestLaunch bat.
        startInfo.CreateNoWindow = true;

        // Prefer javaw on Windows for GUI games (no console attach). Keep java.exe only if javaw missing.
        if (OperatingSystem.IsWindows() &&
            string.Equals(Path.GetFileName(startInfo.FileName), "java.exe", StringComparison.OrdinalIgnoreCase))
        {
            string? dir = Path.GetDirectoryName(startInfo.FileName);
            string windowed = Path.Combine(dir ?? string.Empty, "javaw.exe");
            if (File.Exists(windowed))
                startInfo.FileName = windowed;
        }

        if (string.IsNullOrWhiteSpace(startInfo.FileName) || !File.Exists(startInfo.FileName))
        {
            throw new FileNotFoundException(
                "找不到 Java 可执行文件：" + (startInfo.FileName ?? "(null)"),
                startInfo.FileName);
        }

        TryWriteLatestLaunchBatch(startInfo);

        Process? process = Process.Start(startInfo);
        if (process is null)
            throw new InvalidOperationException("Java 进程未能启动。文件：" + startInfo.FileName);
        return new MinecraftStartedProcess(process);
    }

    private static void TryWriteLatestLaunchBatch(ProcessStartInfo startInfo)
    {
        try
        {
            string workDir = string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
                ? Environment.CurrentDirectory
                : startInfo.WorkingDirectory;
            Directory.CreateDirectory(workDir);
            string batPath = Path.Combine(workDir, "LatestLaunch-PCLN.bat");
            string fileName = startInfo.FileName ?? "java";
            string args = startInfo.Arguments ?? string.Empty;
            string content =
                "chcp 65001>nul\r\n" +
                "@echo off\r\n" +
                "cd /D \"" + workDir.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"\r\n" +
                "\"" + fileName.Replace("\"", "\"\"", StringComparison.Ordinal) + "\" " + args + "\r\n" +
                "echo.\r\necho Exit code: %ERRORLEVEL%\r\npause\r\n";
            File.WriteAllText(batPath, content, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            // Best-effort debug aid only.
            PortableLog.Warn(ex, "Launch", "写入 LatestLaunch-PCLN.bat 失败，游戏启动不受影响。");
        }
    }

    private static void EnsureWorkingDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return;

        Directory.CreateDirectory(workingDirectory);
    }

    private static void Report(
        MinecraftLaunchCoordinatorRequest request,
        string stage,
        double completedWeight,
        bool isLaunched = false,
        string? method = null)
    {
        double progress = MinecraftLaunchStages.ProgressAt(completedWeight);
        PortableLog.RealTime(
            "Launch",
            $"阶段报告：{stage}；进度={progress:P1}；已启动={isLaunched}；方式={method ?? "(无)"}。");
        request.Report(new MinecraftLaunchStageReport(
            stage,
            progress,
            isLaunched,
            method));
    }

    private static string StageName(string key, string fallback) =>
        AvaloniaLocalizationManager.GetText(key, fallback);

    private static string FormatThirdPartyMethod(LoginProfileInfo profile)
    {
        string baseName = AvaloniaLocalizationManager.GetText("Launch.Account.Type.ThirdParty", "第三方");
        // Prefer server host; fall back to DisplayInfo so experimental JVM host hides Authlib-Injector badge.
        string serverLabel = !string.IsNullOrWhiteSpace(profile.AuthServer)
            ? profile.AuthServer
            : profile.DisplayInfo;
        return string.IsNullOrWhiteSpace(serverLabel) ||
               string.Equals(serverLabel, baseName, StringComparison.OrdinalIgnoreCase)
            ? baseName
            : baseName + " / " + serverLabel;
    }

    private static string BuildNoCompatibleJavaMessage(JavaSelectionResult selection)
    {
        if (!string.IsNullOrWhiteSpace(selection.SuggestedDownloadComponent))
        {
            return "未找到兼容的 Java。建议安装 " + selection.SuggestedDownloadComponent +
                   " 后重试，或在设置中手动选择 Java。";
        }

        if (selection.Requirement.Success)
        {
            return "未找到兼容的 Java（需要 " +
                   FormatJavaVersionBound(selection.Requirement.Range.Minimum) +
                   "–" +
                   FormatJavaVersionBound(selection.Requirement.Range.Maximum) +
                   "）。请在设置中安装或选择合适的 Java。";
        }

        return selection.Detail ?? "未找到兼容的 Java。";
    }

    private static string FormatJavaVersionBound(Version version)
    {
        int major = version.Major == 1 ? version.Minor : version.Major;
        return major.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatSpeed(long bytesPerSecond)
    {
        if (bytesPerSecond < 1024)
            return bytesPerSecond.ToString(CultureInfo.InvariantCulture) + " B/s";
        if (bytesPerSecond < 1024 * 1024)
            return (bytesPerSecond / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " KB/s";
        return (bytesPerSecond / (1024d * 1024d)).ToString("0.00", CultureInfo.InvariantCulture) + " MB/s";
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    /// <summary>
    /// Maps Minecraft ids like "1.20.5" / "1.8.9" to the domain form used by
    /// <see cref="JavaRuntimeRequirementResolver"/> (e.g. Version(20, 0, 5)).
    /// </summary>
    internal static Version? TryParseMinecraftVanillaVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string cleaned = text.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0]
            .Trim();
        if (cleaned.Length == 0)
            return null;

        // Snapshot / non-semver ids: leave unresolved so release-time + manifest rules apply.
        if (!char.IsDigit(cleaned[0]))
            return null;

        // Classic Minecraft ids are "1.X" or "1.X.Y" where X is the series major (8, 12, 20…).
        if (cleaned.StartsWith("1.", StringComparison.Ordinal) &&
            cleaned.Length > 2 &&
            char.IsDigit(cleaned[2]))
        {
            string rest = cleaned[2..];
            string[] parts = rest.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 1 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int major))
            {
                int minor = 0;
                int patch = 0;
                if (parts.Length == 2 &&
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int second))
                {
                    // "1.20.5" → (20, 0, 5); "1.8.9" → (8, 0, 9)
                    patch = second;
                }
                else if (parts.Length >= 3 &&
                         int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minor) &&
                         int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out patch))
                {
                    // Defensive for unexpected "1.20.0.1" style.
                }

                return new Version(major, minor, patch, 0);
            }
        }

        return Version.TryParse(cleaned, out Version? version) ? version : null;
    }

    private static bool HasLoaderNeedle(LaunchInstanceInfo instance, params string[] needles)
    {
        MinecraftVersionJsonInfo info = MinecraftVersionJsonInspector.Read(instance);
        return info.LoaderEntries.Any(library =>
            needles.Any(needle => library.Contains(needle, StringComparison.OrdinalIgnoreCase)));
    }

    private static DateTimeOffset? TryReadReleaseTime(LaunchInstanceInfo instance)
    {
        try
        {
            if (!File.Exists(instance.VersionJsonPath))
                return null;

            using FileStream stream = File.OpenRead(instance.VersionJsonPath);
            using JsonDocument document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("releaseTime", out JsonElement releaseTime) &&
                releaseTime.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(
                    releaseTime.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out DateTimeOffset value))
            {
                return value;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            PortableLog.Warn(ex, "LaunchInspect", $"读取版本发布时间失败，将改用其他规则：{instance.VersionJsonPath}");
        }

        return null;
    }

    private static int? TryReadManifestJavaMajor(LaunchInstanceInfo instance)
    {
        try
        {
            if (!File.Exists(instance.VersionJsonPath))
                return null;

            using FileStream stream = File.OpenRead(instance.VersionJsonPath);
            using JsonDocument document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("javaVersion", out JsonElement javaVersion) &&
                javaVersion.ValueKind == JsonValueKind.Object &&
                javaVersion.TryGetProperty("majorVersion", out JsonElement major) &&
                major.TryGetInt32(out int value))
            {
                return value;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            PortableLog.Warn(ex, "LaunchInspect", $"读取版本 Java 主版本失败，将自动推断：{instance.VersionJsonPath}");
        }

        return null;
    }

    private static string? TryReadManifestJavaComponent(LaunchInstanceInfo instance)
    {
        try
        {
            if (!File.Exists(instance.VersionJsonPath))
                return null;

            using FileStream stream = File.OpenRead(instance.VersionJsonPath);
            using JsonDocument document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("javaVersion", out JsonElement javaVersion) &&
                javaVersion.ValueKind == JsonValueKind.Object &&
                javaVersion.TryGetProperty("component", out JsonElement component) &&
                component.ValueKind == JsonValueKind.String)
            {
                return component.GetString();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            PortableLog.Warn(ex, "LaunchInspect", $"读取版本 Java 组件失败，将自动推断：{instance.VersionJsonPath}");
        }

        return null;
    }
}

internal sealed class MinecraftLaunchCoordinatorRequest
{
    public required LaunchInstanceInfo Instance { get; init; }
    public required LoginProfileInfo Profile { get; init; }
    public required InstanceMetadata Metadata { get; init; }
    public required LauncherSettings Settings { get; init; }
    public required string MinecraftRootDirectory { get; init; }
    public bool PreferOfficialSource { get; init; } = true;
    public string? WorldName { get; init; }
    public string? ServerAddress { get; init; }

    public required Action<MinecraftLaunchStageReport> Report { get; init; }
    public Action<string>? Log { get; init; }

    /// <summary>
    /// Validates / refreshes the selected account during the Login stage.
    /// <paramref name="status"/> receives human-readable sub-step messages for the launch log.
    /// </summary>
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

    /// <summary>
    /// Optional UI confirmation before downloading a Mojang Java runtime.
    /// Return true to download, false to cancel launch.
    /// </summary>
    public Func<string, CancellationToken, Task<bool>>? ConfirmJavaDownloadAsync { get; init; }
}
