using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Nexa.Services.Accounts;
using Nexa.Services.Logging;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Minecraft.Libraries;
using Nexa.Services.Minecraft.ModLoaders;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Settings;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Launch;

/// <summary>The concrete platform facts required by Mojang rules and native selection.</summary>
public readonly record struct MinecraftLaunchPlatform(
    MinecraftLibraryOperatingSystem OperatingSystem,
    string OperatingSystemVersion,
    bool Is64BitArchitecture,
    bool IsArm64Architecture)
{
    public static MinecraftLaunchPlatform Detect()
    {
        MinecraftLibraryOperatingSystem operatingSystem = System.OperatingSystem.IsWindows()
            ? MinecraftLibraryOperatingSystem.Win32
            : System.OperatingSystem.IsMacOS()
                ? MinecraftLibraryOperatingSystem.MacOs
                : System.OperatingSystem.IsLinux()
                    ? MinecraftLibraryOperatingSystem.Linux
                    : throw new PlatformNotSupportedException(
                        "Minecraft launch is not supported on this operating system.");
        Architecture architecture = RuntimeInformation.OSArchitecture;
        return new MinecraftLaunchPlatform(
            operatingSystem,
            Environment.OSVersion.Version.ToString(),
            architecture is Architecture.X64 or Architecture.Arm64,
            architecture == Architecture.Arm64);
    }
}

/// <summary>Prepared product launch input and the Java contract which selected its runtime.</summary>
public sealed record MinecraftLaunchPreparation(
    MinecraftInstanceDescriptor Instance,
    MinecraftLaunchRequest Request,
    JavaRequirementResolution JavaRequirement);

/// <summary>
/// Product-level launch orchestration. Callers identify an instance and account; this service
/// owns manifests, inheritance, platform, credentials, Java, settings, planning, and execution.
/// </summary>
public sealed class MinecraftLaunchCoordinator
{
    private static readonly int[] SelectableJavaMajors = [8, 16, 17, 21, 25];
    private static readonly TimeSpan StageHeartbeatInterval = TimeSpan.FromMilliseconds(120);
    private const double StageHeartbeatStep = 0.05d;
    private const double StageHeartbeatCeiling = 0.92d;

    private readonly string _minecraftRootDirectory;
    private readonly string _javaRuntimeRootDirectory;
    private readonly LogService? _log;
    private readonly MinecraftLaunchProgressPublisher? _progress;
    private readonly MinecraftInstanceDiscovery _instances;
    private readonly AccountService _accounts;
    private readonly SettingsService _settings;
    private readonly SettingsPolicyService? _settingsPolicy;
    private readonly JavaSelectionService _javaSelection;
    private readonly IJavaRuntimeInstaller _javaInstaller;
    private readonly MinecraftLaunchExecutor _executor;
    private readonly MinecraftLaunchFileCompletion? _fileCompletion;
    private readonly MinecraftLaunchPlatform _platform;
    private readonly IAccountLaunchIdentityResolver _identityResolver;
    private readonly string _launcherVersion;
    private readonly IMinecraftWindowProbe _windowProbe;
    private readonly Action<int>? _gameWindowAppeared;
    private readonly object _launchGate = new();
    private CancellationTokenSource? _activeLaunch;
    private sealed record JavaChoice(bool Approve, ResolvedJava? Manual = null, string? Component = null, int? Major = null);
    private TaskCompletionSource<JavaChoice>? _acquisitionDecision;
    private JavaRequirementResolution? _pendingJavaRequirement;
    private readonly IAuthlibInjectorProvider? _authlib;
    private readonly LaunchPreflightGate? _preflight;

    public MinecraftLaunchCoordinator(
        string minecraftRootDirectory,
        string javaRuntimeRootDirectory,
        MinecraftInstanceDiscovery instances,
        AccountService accounts,
        SettingsService settings,
        JavaSelectionService javaSelection,
        IJavaRuntimeInstaller javaInstaller,
        MinecraftLaunchExecutor executor,
        MinecraftLaunchPlatform? platform = null,
        LogService? log = null,
        MinecraftLaunchProgressPublisher? progress = null,
        IAccountLaunchIdentityResolver? identityResolver = null,
        string? launcherVersion = null,
        IMinecraftWindowProbe? windowProbe = null,
        IAuthlibInjectorProvider? authlib = null,
        Action<int>? gameWindowAppeared = null,
        MinecraftLaunchFileCompletion? fileCompletion = null,
        SettingsPolicyService? settingsPolicy = null,
        LaunchPreflightGate? preflight = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftRootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(javaRuntimeRootDirectory);
        _minecraftRootDirectory = Path.GetFullPath(minecraftRootDirectory);
        _javaRuntimeRootDirectory = Path.GetFullPath(javaRuntimeRootDirectory);
        _log = log;
        _progress = progress;
        _identityResolver = identityResolver ?? new AccountLaunchIdentityResolver(accounts, log: log);
        _launcherVersion = string.IsNullOrWhiteSpace(launcherVersion) ? "2.0.0" : launcherVersion;
        _windowProbe = windowProbe ?? new MinecraftWindowProbe();
        _gameWindowAppeared = gameWindowAppeared;
        _authlib = authlib;
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _javaSelection = javaSelection ?? throw new ArgumentNullException(nameof(javaSelection));
        _javaInstaller = javaInstaller ?? throw new ArgumentNullException(nameof(javaInstaller));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _fileCompletion = fileCompletion;
        _settingsPolicy = settingsPolicy;
        _preflight = preflight;
        _platform = platform ?? MinecraftLaunchPlatform.Detect();
        if (_platform.OperatingSystem == MinecraftLibraryOperatingSystem.Unknown)
        {
            throw new ArgumentException(
                "The launch platform must identify a concrete Mojang operating system.",
                nameof(platform));
        }
    }

    /// <summary>
    /// Resolves a pending Java acquisition approval: true downloads the runtime, false declines
    /// it. Returns false when no acquisition is awaiting a decision.
    /// </summary>
    public bool DecideJavaAcquisition(bool approve)
    {
        TaskCompletionSource<JavaChoice>? decision;
        lock (_launchGate)
        {
            decision = _acquisitionDecision;
        }

        if (decision is null)
        {
            _log?.Debug("Java", "Acquisition decision ignored: nothing is pending.");
            return false;
        }

        _log?.Info("Java", $"Runtime acquisition decision approve={approve}.");
        return decision.TrySetResult(new(approve));
    }

    public async ValueTask<XsrResult> SelectJavaVersionAsync(int major, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<JavaChoice>? decision;
        JavaRequirementResolution? requirement;
        lock (_launchGate) { decision = _acquisitionDecision; requirement = _pendingJavaRequirement; }
        if (decision is null || requirement is null || major is not (8 or 16 or 17 or 21 or 25))
            return XsrResult.Failure(MinecraftErrors.InvalidRequest("没有待处理的 Java 选择。"));
        var requested = major == 8
            ? new JavaVersionRange(new Version(1, 8), JavaVersionRange.Java8Maximum)
            : new JavaVersionRange(new Version(major, 0), new Version(major, int.MaxValue));
        if (!requirement.Range.TryIntersect(requested, out var range))
            return XsrResult.Failure(MinecraftErrors.JavaUnavailable($"Java {major} 不兼容此游戏版本。"));
        var narrowed = JavaRequirementResolution.Valid(range, major.ToString(CultureInfo.InvariantCulture));
        var selected = await Task.Run(async () => await _javaSelection.SelectAsync(narrowed, new AutoSelectJavaPreference(), cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        var acquisition = JavaRuntimeAcquisitionPlanner.Plan(narrowed);
        if (!selected.Success && !acquisition.CanAutoDownload)
            return XsrResult.Failure(MinecraftErrors.JavaUnavailable("此 Java 版本无法自动下载。"));
        JavaChoice choice = selected.Success && selected.SelectedJava is { } java
            ? new(false, new ResolvedJava(SelectExecutable(java.Installation), java.Installation.MajorVersion))
            : new(true, Component: acquisition.DownloadComponent, Major: major);
        lock (_launchGate)
        {
            if (!ReferenceEquals(_acquisitionDecision, decision) || !decision.TrySetResult(choice))
                return XsrResult.Failure(MinecraftErrors.InvalidRequest("Java 选择已失效。"));
        }
        return XsrResult.Success();
    }

    public async ValueTask<XsrResult> SelectJavaAsync(string path, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<JavaChoice>? decision;
        JavaRequirementResolution? requirement;
        lock (_launchGate) { decision = _acquisitionDecision; requirement = _pendingJavaRequirement; }
        if (decision is null || requirement is null) return XsrResult.Failure(MinecraftErrors.InvalidRequest("no Java choice is pending."));
        JavaSelectionResult selection = await _javaSelection.SelectAsync(requirement, new ExistingJavaPreference(path), cancellationToken).ConfigureAwait(false);
        if (!selection.Success || selection.SelectedJava is not { } java)
            return XsrResult.Failure(MinecraftErrors.JavaUnavailable("所选 Java 不满足此版本要求，请选择兼容的 Java。"));
        lock (_launchGate)
        {
            if (!ReferenceEquals(_acquisitionDecision, decision) || !decision.TrySetResult(new(false,
                new ResolvedJava(SelectExecutable(java.Installation), java.Installation.MajorVersion))))
                return XsrResult.Failure(MinecraftErrors.InvalidRequest("the pending Java choice changed."));
        }
        return XsrResult.Success();
    }

    /// <summary>
    /// Cancels the active launch pipeline between stages. Returns false when no launch is
    /// running; the game process itself is cancelled through the process service instead.
    /// </summary>
    public bool CancelActiveLaunch()
    {
        // Cancel under the same lock that governs registration and disposal: a racing launch
        // completion may null the field and dispose the CTS between a split read and Cancel.
        lock (_launchGate)
        {
            if (_activeLaunch is null)
            {
                _log?.Debug("Launch", "Launch cancellation ignored: no pipeline is running.");
                return false;
            }

            _log?.Info("Launch", "Launch pipeline cancellation requested.");
            _activeLaunch.Cancel();
            return true;
        }
    }

    public ValueTask<XsrResult<MinecraftLaunchPreparation>> PrepareAsync(
        string instanceId,
        int accountIndex,
        CancellationToken cancellationToken = default)
        => PrepareAsync(instanceId, accountIndex, _minecraftRootDirectory, cancellationToken);

    public async ValueTask<XsrResult<MinecraftLaunchPreparation>> PrepareAsync(
        string instanceId, int accountIndex, string minecraftRootDirectory,
        CancellationToken cancellationToken = default)
    {
        using LogOperation? operation = _log?.BeginOperation("Launch", "PrepareLaunch", $"instance={instanceId} account_index={accountIndex}");
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            operation?.Reject("minecraft.invalid_request");
            return XsrResult.Failure<MinecraftLaunchPreparation>(
                MinecraftErrors.InvalidRequest("an instance id is required."));
        }

        try
        {
            string root = MinecraftLibraryService.NormalizeDirectory(minecraftRootDirectory);
            string javaRoot = MinecraftLibraryService.PathComparer.Equals(root, _minecraftRootDirectory)
                ? _javaRuntimeRootDirectory : Path.Combine(root, "runtime");
            operation?.Stage("resolve_instance");
            IReadOnlyList<MinecraftInstanceDescriptor> installed = await _instances
                .DiscoverAsync(root, cancellationToken)
                .ConfigureAwait(false);
            MinecraftInstanceDescriptor? instance = installed.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, instanceId, StringComparison.OrdinalIgnoreCase));
            if (instance is null)
            {
                operation?.Reject("minecraft.instance_not_found");
                return XsrResult.Failure<MinecraftLaunchPreparation>(
                    MinecraftErrors.InstanceNotFound(instanceId));
            }

            operation?.Stage("resolve_account");
            string method = string.Empty;
            XsrResult<MinecraftLaunchIdentity> identityResult =
                XsrResult.Failure<MinecraftLaunchIdentity>(MinecraftErrors.InvalidRequest("the account was not resolved."));
            XsrResult<LaunchProfile> profileResult =
                XsrResult.Failure<LaunchProfile>(MinecraftErrors.InvalidRequest("the account was not resolved."));
            await RunStageAsync(
                MinecraftLaunchStages.Login,
                completedBefore: 0d,
                MinecraftLaunchStages.LoginWeight,
                method,
                async token =>
                {
                    profileResult = _accounts.GetProfile(accountIndex);
                    if (!profileResult.IsSuccess)
                    {
                        return;
                    }

                    identityResult = await _identityResolver
                        .ResolveAsync(accountIndex, profileResult.Value, token)
                        .ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
            if (!profileResult.IsSuccess)
            {
                operation?.Reject(profileResult.Error!.Code.Value);
                return XsrResult.Failure<MinecraftLaunchPreparation>(profileResult.Error!);
            }

            if (!identityResult.IsSuccess)
            {
                operation?.Reject(identityResult.Error!.Code.Value);
                return XsrResult.Failure<MinecraftLaunchPreparation>(identityResult.Error!);
            }

            method = identityResult.Value.Mode.ToString().ToLowerInvariant();

            operation?.Stage("resolve_manifests");
            MinecraftResolvedVersionManifests manifests = default!;
            MinecraftModLoaderDescriptor loader = default!;
            await RunStageAsync(
                MinecraftLaunchStages.CompleteFiles,
                MinecraftLaunchStages.LoginWeight,
                MinecraftLaunchStages.CompleteFilesWeight,
                method,
                async token =>
                {
                    manifests = await MinecraftVersionJsonReader
                        .ResolveAsync(instance, root, token)
                        .ConfigureAwait(false);
                    loader = MinecraftModLoaderDetector.Detect(manifests.Current);
                    // The legacy 补全文件 step: verify every referenced file on disk and
                    // repair the missing ones before the JVM starts. Real download progress
                    // flows through the same stage reports, so the narration stays honest.
                    if (_fileCompletion is { } completion)
                    {
                        await completion.CompleteAsync(
                            root,
                            instance,
                            manifests,
                            _platform,
                            method,
                            _progress,
                            token).ConfigureAwait(false);
                    }
                },
                cancellationToken,
                heartbeat: false).ConfigureAwait(false);
            _log?.Debug("Launch", $"Effective manifest resolved instance={instanceId} inherited={manifests.Inherited.Count} loader={loader.Kind}");
            MinecraftJavaRequirementRequest javaRequest = CreateJavaRequirement(
                instance,
                manifests,
                loader);
            JavaPreference preference = instance.Metadata.JavaSelectionMode == 2
                && !string.IsNullOrWhiteSpace(instance.Metadata.SelectedJavaPath)
                ? new ExistingJavaPreference(instance.Metadata.SelectedJavaPath)
                : new AutoSelectJavaPreference();
            if (_settingsPolicy is not null)
            {
                var policy = _settingsPolicy.Read(new(instance.DirectoryPath));
                if (!policy.IsSuccess) throw new InvalidOperationException("无法读取版本 Java 设置。");
                preference = ApplyJavaPreference(preference, policy.Value!);
            }
            operation?.Stage("select_java", $"os={_platform.OperatingSystem} os_version={_platform.OperatingSystemVersion} arm64={_platform.IsArm64Architecture} manifest_major={javaRequest.ManifestJavaMajorVersion}");
            JavaSelectionResult java = default!;
            XsrResult<ResolvedJava> resolvedJava = XsrResult.Failure<ResolvedJava>(MinecraftErrors.JavaUnavailable("java was not resolved."));
            await RunStageAsync(
                MinecraftLaunchStages.GetJava,
                MinecraftLaunchStages.LoginWeight + MinecraftLaunchStages.CompleteFilesWeight,
                MinecraftLaunchStages.GetJavaWeight,
                method,
                async token =>
                {
                    java = await _javaSelection
                        .SelectAsync(javaRequest, preference, token)
                        .ConfigureAwait(false);
                    if (!java.Success && java.FailureReason == JavaSelectionFailureReason.NoCompatibleRuntime
                        && !MinecraftLibraryService.PathComparer.Equals(root, _minecraftRootDirectory))
                    {
                        JavaSelectionResult local = await new JavaSelectionService(new LocalJavaRuntimeLocator(javaRoot, _log))
                            .SelectAsync(javaRequest, preference, token).ConfigureAwait(false);
                        if (local.Success) java = local;
                    }
                    resolvedJava = await ResolveJavaAsync(
                        java,
                        preference,
                        loader.Kind is MinecraftModLoaderKind.Forge or MinecraftModLoaderKind.NeoForge,
                        operation, javaRoot, token).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
            if (!resolvedJava.IsSuccess)
            {
                operation?.Reject(resolvedJava.Error!.Code.Value);
                return XsrResult.Failure<MinecraftLaunchPreparation>(resolvedJava.Error!);
            }

            operation?.Stage("apply_launch_settings", $"java_major={resolvedJava.Value.MajorVersion} java_path={resolvedJava.Value.ExecutablePath}");
            MinecraftLaunchRequest request = CreateRequest(
                instance,
                manifests,
                loader,
                identityResult.Value,
                resolvedJava.Value, root);
            if (identityResult.Value.AuthServer is { } authServer)
            {
                if (_authlib is null) throw new InvalidOperationException("Authlib Injector preparation is not composed.");
                operation?.Stage("prepare_authlib_injector");
                string injector = await _authlib.EnsureAsync(root, cancellationToken).ConfigureAwait(false);
                request = request with { AuthlibServer = authServer, AuthlibInjectorPath = injector };
            }
            operation?.Complete($"instance={instance.Id} memory_mb={request.MemoryMegabytes}");
            return XsrResult.Success(new MinecraftLaunchPreparation(
                instance,
                request,
                java.Requirement));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation?.Cancel();
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or ArgumentException
            or HttpRequestException
            or PlatformNotSupportedException)
        {
            operation?.Fail(exception);
            return XsrResult.Failure<MinecraftLaunchPreparation>(
                MinecraftErrors.LaunchPreparationFailed(exception.Message));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            operation?.Fail(exception);
            throw;
        }
    }

    public ValueTask<XsrResult> StartAsync(
        string instanceId,
        int accountIndex,
        CancellationToken cancellationToken = default)
        => StartAsync(instanceId, accountIndex, _minecraftRootDirectory, cancellationToken);

    public async ValueTask<XsrResult> StartAsync(
        string instanceId, int accountIndex, string minecraftRootDirectory,
        CancellationToken cancellationToken = default)
    {
        using LogOperation? operation = _log?.BeginOperation("Launch", "StartMinecraft", $"instance={instanceId} account_index={accountIndex}");
        CancellationTokenSource launchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_launchGate)
        {
            if (_activeLaunch is not null)
            {
                operation?.Reject(MinecraftErrors.LaunchAlreadyActiveCode.Value);
                launchCancellation.Dispose();
                return XsrResult.Failure(MinecraftErrors.LaunchAlreadyActive());
            }

            _activeLaunch = launchCancellation;
        }

        try
        {
            return await StartLockedAsync(instanceId, accountIndex, minecraftRootDirectory, launchCancellation, operation).ConfigureAwait(false);
        }
        finally
        {
            // Only this pipeline's registration may be cleared: a newer launch keeps its own.
            lock (_launchGate)
            {
                if (ReferenceEquals(_activeLaunch, launchCancellation))
                {
                    _activeLaunch = null;
                }
            }

            launchCancellation.Dispose();
        }
    }

    private async ValueTask<XsrResult> StartLockedAsync(
        string instanceId,
        int accountIndex,
        string minecraftRootDirectory,
        CancellationTokenSource launchCancellation,
        LogOperation? operation)
    {
        CancellationToken launchToken = launchCancellation.Token;
        // Set as soon as the game process exists, so a cancellation before the window
        // confirmation can terminate the process we created (the game must not pop up after
        // the user has already cancelled the launch).
        Process.MinecraftProcessSession? startedSession = null;
        try
        {
            _progress?.Start();
            operation?.Stage("prepare");
            XsrResult<MinecraftLaunchPreparation> preparation = await PrepareAsync(
                instanceId, accountIndex, minecraftRootDirectory, launchToken).ConfigureAwait(false);
            if (!preparation.IsSuccess)
            {
                operation?.Reject(preparation.Error!.Code.Value);
                _progress?.Stop();
                return XsrResult.Failure(preparation.Error!);
            }

            string method = preparation.Value.Request.IdentityMode.ToString().ToLowerInvariant();
            operation?.Stage("create_plan");
            MinecraftLaunchPlan plan = default!;
            await RunStageAsync(
                MinecraftLaunchStages.GetArguments,
                MinecraftLaunchStages.LoginWeight
                    + MinecraftLaunchStages.CompleteFilesWeight
                    + MinecraftLaunchStages.GetJavaWeight,
                MinecraftLaunchStages.GetArgumentsWeight,
                method,
                token =>
                {
                    plan = MinecraftLaunchPlanner.CreatePlan(preparation.Value.Request);
                    return Task.CompletedTask;
                },
                launchToken).ConfigureAwait(false);

            if (_preflight is not null)
            {
                operation?.Stage("preflight");
                _progress?.Report(new MinecraftLaunchStageReport("preflight", MinecraftLaunchStages.ProgressAt(36), Method: method));
                if (!await _preflight.CheckAsync(minecraftRootDirectory, instanceId, plan, launchToken).ConfigureAwait(false))
                {
                    operation?.Reject("preflight_declined"); _progress?.Stop();
                    return XsrResult.Failure(MinecraftErrors.LaunchPreparationFailed("启动预检未通过或已取消。"));
                }
            }
            operation?.Stage("execute_plan", $"native_archives={plan.NativeLibraries.Count}");
            // Stage boundaries as named constants: every hand-written +Weight chain eventually
            // desynced from the weight table, so the math is written exactly once here.
            const double afterLogin = MinecraftLaunchStages.LoginWeight;
            const double afterCompleteFiles = afterLogin + MinecraftLaunchStages.CompleteFilesWeight;
            const double afterJava = afterCompleteFiles + MinecraftLaunchStages.GetJavaWeight;
            const double afterArguments = afterJava + MinecraftLaunchStages.GetArgumentsWeight;
            const double afterExtract = afterArguments + MinecraftLaunchStages.ExtractNativesWeight;
            const double afterPreLaunch = afterExtract + MinecraftLaunchStages.PreLaunchWeight;
            const double afterCustomCommand = afterPreLaunch + MinecraftLaunchStages.CustomCommandWeight;
            const double afterStart = afterCustomCommand + MinecraftLaunchStages.StartProcessWeight;
            _progress?.Report(new MinecraftLaunchStageReport(
                MinecraftLaunchStages.ExtractNatives,
                MinecraftLaunchStages.ProgressAt(afterArguments),
                Method: method));
            // The legacy pre-launch stage: the working directory must exist before the game
            // (or anything the plan references) writes into it — strictly before start_process.
            _progress?.Report(new MinecraftLaunchStageReport(
                MinecraftLaunchStages.PreLaunch,
                MinecraftLaunchStages.ProgressAt(afterExtract),
                Method: method));
            Directory.CreateDirectory(plan.WorkingDirectory);
            Process.MinecraftProcessSession session = startedSession = await _executor.ExecuteAsync(
                plan,
                preparation.Value.Instance.Id,
                stage: stageToken =>
                {
                    if (stageToken == MinecraftLaunchStages.StartProcess)
                    {
                        // custom_command has not migrated; its reserved weight is skipped over.
                        _progress?.Report(new MinecraftLaunchStageReport(
                            MinecraftLaunchStages.StartProcess,
                            MinecraftLaunchStages.ProgressAt(afterCustomCommand),
                            Method: method));
                    }
                },
                launchToken)
                .ConfigureAwait(false);
            Guid sessionId = session.Snapshot.SessionId;
            // The narration truth must not outlive the game. A JVM can die between process
            // creation and this point, and the terminal Changed event has then already fired —
            // so this is the classic subscribe-then-recheck: the handler is named so it can
            // unsubscribe, and the current snapshot is re-run after subscribing to catch a
            // transition that happened before we listened.
            void OnSessionChanged(MinecraftProcessSnapshot snapshot)
            {
                if (snapshot.SessionId != sessionId
                    || snapshot.State is not (MinecraftProcessState.Exited
                        or MinecraftProcessState.Failed
                        or MinecraftProcessState.Cancelled))
                {
                    return;
                }

                session.Changed -= OnSessionChanged;
                _progress?.Stop(sessionId);
            }

            session.Changed += OnSessionChanged;
            // wait_window ENTERS at its legacy boundary (42/44); its completion folds into
            // the end report (44/44).
            // From here on every report carries the session id, so a Stop(sessionId) reset
            // can match the narration it belongs to.
            _progress?.Report(new MinecraftLaunchStageReport(
                MinecraftLaunchStages.WaitWindow,
                MinecraftLaunchStages.ProgressAt(afterStart),
                Method: method,
                SessionId: sessionId));
            GameWindowWaitResult wait = await WaitForGameWindowAsync(session, launchToken).ConfigureAwait(false);
            if (wait == GameWindowWaitResult.ProcessExited)
            {
                // The JVM died before presenting a window: this is a failed launch, not a
                // launched one — the failure path closes the launching page and feeds crash
                // analysis downstream. The narration reset here also covers the exit that
                // happened before the terminal callback could be observed by readers.
                _progress?.Stop(sessionId);
                operation?.Reject(MinecraftErrors.ExitedBeforeWindowCode.Value);
                return XsrResult.Failure(MinecraftErrors.ExitedBeforeWindow());
            }

            _progress?.Report(new MinecraftLaunchStageReport(
                MinecraftLaunchStages.End,
                MinecraftLaunchStages.ProgressAt(MinecraftLaunchStages.Total),
                IsLaunched: true,
                Method: method,
                SessionId: sessionId));
            OnSessionChanged(session.Snapshot);
            operation?.Complete($"session={sessionId} pid={session.Snapshot.ProcessId} state={session.Snapshot.State}");
            return XsrResult.Success();
        }
        catch (OperationCanceledException) when (launchToken.IsCancellationRequested)
        {
            operation?.Cancel();
            // Pre-confirmation cancellation kills the process this pipeline created; once the
            // game is confirmed running the UI's back action handles it instead.
            if (startedSession is { } session
                && session.Snapshot.State is MinecraftProcessState.Created or MinecraftProcessState.Running)
            {
                _log?.Info("Launch", $"Cancelling the pre-confirmation game process session={session.Snapshot.SessionId}.");
                session.Cancel();
            }

            _progress?.Stop();
            return XsrResult.Failure(MinecraftErrors.LaunchFailed("the launch was cancelled."));
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or ArgumentException)
        {
            operation?.Fail(exception);
            _progress?.Stop();
            return XsrResult.Failure(MinecraftErrors.LaunchFailed(exception.Message));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            operation?.Fail(exception);
            _progress?.Stop();
            throw;
        }
    }

    internal enum GameWindowWaitResult
    {
        Visible,
        Unsupported,
        TimedOut,
        ProcessExited,
    }

    private static readonly TimeSpan GameWindowPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GameWindowWaitLimit = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The legacy wait-for-window stage: the narration stays at the wait stage until the game
    /// process presents a visible window (or the limit lapses — a headless or slow-windowing
    /// game still counts as launched rather than blocking the flow forever).
    /// </summary>
    private ValueTask<GameWindowWaitResult> WaitForGameWindowAsync(
        Process.MinecraftProcessSession session,
        CancellationToken cancellationToken) =>
        WaitForGameWindowAsync(_windowProbe, _log, session, cancellationToken, _gameWindowAppeared);

    internal static async ValueTask<GameWindowWaitResult> WaitForGameWindowAsync(
        IMinecraftWindowProbe windowProbe,
        LogService? log,
        Process.MinecraftProcessSession session,
        CancellationToken cancellationToken,
        Action<int>? gameWindowAppeared = null)
    {
        int processId = session.Snapshot.ProcessId;
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A JVM that already died will never present a window; the terminal callback below
            // resets the narration, so waiting would only burn the limit.
            if (session.Snapshot.State is MinecraftProcessState.Exited
                or MinecraftProcessState.Failed
                or MinecraftProcessState.Cancelled)
            {
                log?.Info("Launch", $"Game process already ended before its window appeared pid={processId}.");
                return GameWindowWaitResult.ProcessExited;
            }

            MinecraftWindowProbeResult probe = await windowProbe
                .ProbeAsync(processId, cancellationToken)
                .ConfigureAwait(false);
            if (probe == MinecraftWindowProbeResult.Visible)
            {
                session.ConfirmGameWindow();
                log?.Info("Launch", $"Game window confirmed pid={processId}.");
                // The window exists now: the host detaches it from the launcher's taskbar group
                // while the game keeps its own icon. Calling earlier races window creation.
                try
                {
                    gameWindowAppeared?.Invoke(processId);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
                {
                    log?.Warn("Launch", $"Game window integration failed pid={processId}: {exception.GetType().Name}: {exception.Message}");
                }
                return GameWindowWaitResult.Visible;
            }

            if (probe == MinecraftWindowProbeResult.Unsupported)
            {
                // No window detection on this platform: a wait could only burn its limit, so
                // the legacy behavior degrades to "process started counts as launched".
                log?.Info("Launch", $"Window detection unsupported; skipping the wait pid={processId}.");
                return GameWindowWaitResult.Unsupported;
            }

            if (System.Diagnostics.Stopwatch.GetElapsedTime(startedAt) >= GameWindowWaitLimit)
            {
                log?.Warn("Launch", $"No game window appeared within the limit pid={processId}; continuing.");
                return GameWindowWaitResult.TimedOut;
            }

            await Task.Delay(GameWindowPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs one pipeline stage with the legacy heartbeat: the stage enters at its completed
    /// weight, soft progress advances by one step per heartbeat (clamped inside the stage),
    /// and the stage completes at its full weight. Stages without a progress publisher run
    /// unchanged.
    /// </summary>
    private async ValueTask RunStageAsync(
        string stage,
        double completedBefore,
        double stageWeight,
        string method,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken,
        bool heartbeat = true)
    {
        if (_progress is null)
        {
            await work(cancellationToken).ConfigureAwait(false);
            return;
        }

        _progress.Report(new MinecraftLaunchStageReport(
            stage,
            MinecraftLaunchStages.ProgressAt(completedBefore),
            Method: method));
        Task workTask = work(cancellationToken);
        double softFraction = 0d;
        // Stages that publish their own real progress opt out of the heartbeat: the two
        // clocks fight, and the display flickers between the real value and the heartbeat's
        // ceiling for as long as the stage runs.
        while (heartbeat && !workTask.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            softFraction = Math.Min(StageHeartbeatCeiling, softFraction + StageHeartbeatStep);
            _progress.Report(new MinecraftLaunchStageReport(
                stage,
                MinecraftLaunchStages.ProgressAt(completedBefore + (stageWeight * softFraction)),
                Method: method));
            try
            {
                await Task.WhenAny(workTask, Task.Delay(StageHeartbeatInterval, cancellationToken))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (workTask.IsCompleted)
            {
                break;
            }
        }

        await workTask.ConfigureAwait(false);
        _progress.Report(new MinecraftLaunchStageReport(
            stage,
            MinecraftLaunchStages.ProgressAt(completedBefore + stageWeight),
            Method: method));
    }

    private async ValueTask<XsrResult<ResolvedJava>> ResolveJavaAsync(
        JavaSelectionResult selection,
        JavaPreference preference,
        bool hasForge,
        LogOperation? operation,
        string javaRuntimeRootDirectory,
        CancellationToken cancellationToken)
    {
        _log?.Info("Java", $"Java selection completed success={selection.Success} failure={selection.FailureReason} minimum={selection.Requirement.Range.Minimum} maximum={selection.Requirement.Range.Maximum}");
        if (selection.Success && selection.SelectedJava is { } selected)
        {
            string installedExecutable = SelectExecutable(selected.Installation);
            return XsrResult.Success(new ResolvedJava(
                installedExecutable,
                selected.Installation.MajorVersion));
        }

        if (selection.FailureReason != JavaSelectionFailureReason.NoCompatibleRuntime)
        {
            return XsrResult.Failure<ResolvedJava>(MinecraftErrors.JavaUnavailable(
                selection.Detail ?? "the Java requirement could not be resolved."));
        }

        // An explicit, incompatible Java choice is a user-visible error. Never replace it with
        // a downloaded runtime behind the user's back.
        if (preference is ExistingJavaPreference)
        {
            return XsrResult.Failure<ResolvedJava>(MinecraftErrors.JavaUnavailable(
                "the selected Java executable is missing or incompatible with this instance."));
        }

        JavaRuntimeAcquisitionDecision acquisition = JavaRuntimeAcquisitionPlanner.Plan(
            selection.Requirement,
            hasForge);
        if (!acquisition.CanAutoDownload || string.IsNullOrWhiteSpace(acquisition.DownloadComponent))
        {
            return XsrResult.Failure<ResolvedJava>(MinecraftErrors.JavaUnavailable(
                $"no compatible Java runtime is installed and automatic acquisition is blocked ({acquisition.BlockReason})."));
        }

        JavaChoice choice = await RequestAcquisitionApprovalAsync(
                acquisition,
                JavaMajor(selection.Requirement.Range.Minimum),
                operation,
                selection.Requirement, cancellationToken).ConfigureAwait(false);
        if (choice.Manual is { } manual) return XsrResult.Success(manual);
        if (!choice.Approve)
        {
            return XsrResult.Failure<ResolvedJava>(MinecraftErrors.JavaUnavailable(
                "the Java runtime acquisition was declined."));
        }

        if (choice.Component is not null)
            _progress?.Report(new MinecraftLaunchStageReport(MinecraftLaunchStages.GetJava, 0, Method: $"未找到 Java {choice.Major}，正在自动下载"));
        operation?.Stage("install_java", $"component={acquisition.DownloadComponent}");
        string acquiredExecutable = await _javaInstaller.InstallAsync(
            choice.Component ?? acquisition.DownloadComponent,
            javaRuntimeRootDirectory,
            progress: null,
            cancellationToken).ConfigureAwait(false);
        if (!File.Exists(acquiredExecutable))
        {
            return XsrResult.Failure<ResolvedJava>(MinecraftErrors.JavaUnavailable(
                "the acquired Java runtime did not provide an executable."));
        }

        int major = choice.Major ?? JavaMajor(selection.Requirement.Range.Minimum);
        return XsrResult.Success(new ResolvedJava(SelectWindowedSibling(acquiredExecutable), major));
    }

    /// <summary>
    /// Asks the user before auto-downloading a Java runtime (legacy behavior): the pipeline
    /// pauses with the acquisition cells published until a decision command or cancellation
    /// resolves it.
    /// </summary>
    private async ValueTask<JavaChoice> RequestAcquisitionApprovalAsync(
        JavaRuntimeAcquisitionDecision acquisition,
        int majorVersion,
        LogOperation? operation,
        JavaRequirementResolution requirement,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<JavaChoice> decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_launchGate)
        {
            _acquisitionDecision = decision;
            _pendingJavaRequirement = requirement;
        }
        int[] choices = SelectableJavaMajors.Where(candidate => requirement.Range.TryIntersect(
            candidate == 8 ? new JavaVersionRange(new Version(1, 8), JavaVersionRange.Java8Maximum)
                : new JavaVersionRange(new Version(candidate, 0), new Version(candidate, int.MaxValue)), out _)).ToArray();
        _progress?.RequestAcquisition(acquisition.DownloadComponent ?? "unknown", majorVersion, Array.AsReadOnly(choices));
        _log?.Info("Java", $"Runtime acquisition awaiting approval component={acquisition.DownloadComponent}.");
        try
        {
            return await decision.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_launchGate)
            {
                if (ReferenceEquals(_acquisitionDecision, decision))
                {
                    _acquisitionDecision = null;
                    _pendingJavaRequirement = null;
                }
            }

            _progress?.ResolveAcquisition();
        }
    }

    private MinecraftLaunchRequest CreateRequest(
        MinecraftInstanceDescriptor instance,
        MinecraftResolvedVersionManifests manifests,
        MinecraftModLoaderDescriptor loader,
        MinecraftLaunchIdentity identity,
        ResolvedJava java,
        string minecraftRootDirectory)
    {
        MinecraftInstanceMetadata metadata = instance.Metadata;
        int width = GetSetting("LaunchArgumentWindowWidth", 854);
        int height = GetSetting("LaunchArgumentWindowHeight", 480);
        string customJvm = FirstNonEmpty(
            metadata.JvmArguments,
            GetSetting("LaunchAdvanceJvm", string.Empty));
        string customGame = FirstNonEmpty(
            metadata.GameArguments,
            GetSetting("LaunchAdvanceGame", string.Empty));
        string versionType = FirstNonEmpty(
            metadata.CustomInfo,
            GetSetting("LaunchArgumentInfo", "NexaN"),
            "NexaCL");
        DateTimeOffset? releaseTime = ReadReleaseTime(manifests) ?? instance.Version.ReleaseTime;

        var request = new MinecraftLaunchRequest
        {
            VersionJson = manifests.Current,
            InheritedVersionJsons = manifests.Inherited,
            VersionId = instance.VersionId,
            InstanceDirectory = instance.DirectoryPath,
            MinecraftRootDirectory = minecraftRootDirectory,
            PlayerName = identity.PlayerName,
            PlayerUuid = identity.PlayerUuid,
            AccessToken = identity.AccessToken,
            IdentityMode = identity.Mode,
            JavaExecutablePath = java.ExecutablePath,
            JavaMajorVersion = java.MajorVersion,
            MemoryMegabytes = ResolveMemoryMegabytes(metadata, loader.Kind),
            Width = Math.Max(1, width),
            Height = Math.Max(1, height),
            Fullscreen = GetSetting("LaunchArgumentWindowType", 1) == 0,
            IsolatedGameDirectory = metadata.InstanceIsolation,
            CustomJvmArguments = string.IsNullOrWhiteSpace(customJvm) ? null : customJvm,
            CustomGameArguments = string.IsNullOrWhiteSpace(customGame) ? null : customGame,
            ClasspathHeadEntries = SplitClasspathHead(metadata.ClasspathHead),
            Server = string.IsNullOrWhiteSpace(metadata.ServerToEnter) ? null : metadata.ServerToEnter,
            ReleaseTime = releaseTime,
            LauncherName = "NexaCL",
            LauncherVersion = _launcherVersion,
            VersionType = versionType,
            UseSystemGlfw = metadata.UseSystemGlfw || GetSetting("LaunchUseSystemGlfw", false),
            HasCleanroom = loader.Kind == MinecraftModLoaderKind.Cleanroom,
            OperatingSystem = _platform.OperatingSystem,
            OperatingSystemVersion = _platform.OperatingSystemVersion,
            Is64BitArchitecture = _platform.Is64BitArchitecture,
            IsArm64Architecture = _platform.IsArm64Architecture,
            Features = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["has_custom_resolution"] = true,
                ["is_demo_user"] = false,
            },
        };
        if (_settingsPolicy is null) return request;
        var effective = _settingsPolicy.Read(new(instance.DirectoryPath));
        if (!effective.IsSuccess) throw new InvalidOperationException("无法读取版本设置：" + effective.Error?.Message);
        return ApplySettings(request, effective.Value!);
    }

    internal static JavaPreference ApplyJavaPreference(JavaPreference fallback, SettingsEffectiveSnapshot snapshot)
    {
        var setting = snapshot.Values.Single(value => value.Key == "java.runtime");
        if (setting.ValidationError is not null) throw new InvalidDataException("首选 Java 设置无效。");
        if (setting.Source == SettingsLayer.Builtin) return fallback;
        return setting.Value.Mode == SettingsOverrideMode.Auto ? new AutoSelectJavaPreference()
            : new ExistingJavaPreference(setting.Value.Value!);
    }

    internal static MinecraftLaunchRequest ApplySettings(MinecraftLaunchRequest request, SettingsEffectiveSnapshot snapshot)
    {
        foreach (var setting in snapshot.Values)
        {
            if (setting.Value.Mode != SettingsOverrideMode.Custom
                || (setting.Source != SettingsLayer.Instance && !(setting.Key == "game.window-mode" && setting.Source == SettingsLayer.Global))) continue;
            string value = setting.Value.Value ?? "";
            request = setting.Key switch
            {
                "game.width" => request with { Width = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) },
                "game.height" => request with { Height = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) },
                "game.window-mode" => request with { Fullscreen = value == "fullscreen" },
                "game.jvm" => request with { CustomJvmArguments = value },
                "game.arguments" => request with { CustomGameArguments = value },
                _ => request,
            };
        }
        return request;
    }

    internal static MinecraftJavaRequirementRequest CreateJavaRequirement(
        MinecraftInstanceDescriptor instance,
        MinecraftResolvedVersionManifests manifests,
        MinecraftModLoaderDescriptor loader)
    {
        (int? major, string? component) = ReadManifestJava(manifests);
        MinecraftGameVersion? gameVersion = ResolveGameVersion(manifests);
        return new MinecraftJavaRequirementRequest
        {
            MinecraftVersion = gameVersion,
            HasReliableVanillaVersion = gameVersion is not null,
            ReleaseTime = ReadReleaseTime(manifests) ?? instance.Version.ReleaseTime,
            ManifestJavaMajorVersion = major,
            ManifestJavaComponent = component,
            HasOptiFine = loader.Kind == MinecraftModLoaderKind.OptiFine,
            HasForge = loader.Kind is MinecraftModLoaderKind.Forge or MinecraftModLoaderKind.NeoForge,
            ForgeVersion = loader.Version,
            HasCleanroom = loader.Kind == MinecraftModLoaderKind.Cleanroom,
            CleanroomVersion = loader.Version ?? ReadLoaderVersion(manifests.Current, "cleanroom"),
            HasFabric = loader.Kind is MinecraftModLoaderKind.Fabric or MinecraftModLoaderKind.Quilt,
            HasLiteLoader = loader.Kind == MinecraftModLoaderKind.LiteLoader,
            HasLabyMod = loader.Kind == MinecraftModLoaderKind.LabyMod,
        };
    }

    private static (int? Major, string? Component) ReadManifestJava(
        MinecraftResolvedVersionManifests manifests)
    {
        foreach (JsonObject manifest in EnumerateEffectiveOrder(manifests))
        {
            if (manifest["javaVersion"] is not JsonObject java)
            {
                continue;
            }

            int? major = java["majorVersion"]?.GetValue<int>();
            string? component = java["component"]?.ToString();
            if (major is not null || !string.IsNullOrWhiteSpace(component))
            {
                return (major, component);
            }
        }

        return (null, null);
    }

    private static MinecraftGameVersion? ResolveGameVersion(
        MinecraftResolvedVersionManifests manifests)
    {
        foreach (JsonObject manifest in EnumerateEffectiveOrder(manifests))
        {
            if (MinecraftGameVersion.TryParse(manifest["id"]?.ToString(), out MinecraftGameVersion parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadReleaseTime(MinecraftResolvedVersionManifests manifests)
    {
        foreach (JsonObject manifest in EnumerateEffectiveOrder(manifests))
        {
            string? raw = manifest["releaseTime"]?.ToString() ?? manifest["time"]?.ToString();
            if (DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static IEnumerable<JsonObject> EnumerateEffectiveOrder(
        MinecraftResolvedVersionManifests manifests)
    {
        yield return manifests.Current;
        foreach (JsonObject inherited in manifests.Inherited)
        {
            yield return inherited;
        }
    }

    private static string? ReadLoaderVersion(JsonObject manifest, string marker)
    {
        string? id = manifest["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        int index = id.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        index += marker.Length;
        while (index < id.Length && id[index] is '-' or '_' or ' ')
        {
            index++;
        }

        int end = index;
        while (end < id.Length && (char.IsDigit(id[end]) || id[end] is '.'))
        {
            end++;
        }

        return end > index ? id[index..end] : null;
    }

    private int ResolveMemoryMegabytes(
        MinecraftInstanceMetadata metadata,
        MinecraftModLoaderKind loader)
    {
        if (metadata.MemorySolution == 1)
        {
            return SliderValueToMemoryMegabytes(metadata.CustomMemorySize);
        }

        if (GetSetting("LaunchRamType", 0) == 1)
        {
            return SliderValueToMemoryMegabytes(GetSetting("LaunchRamCustom", 15));
        }

        double availableGigabytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes > 0
            ? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024d / 1024d
            : 4d;
        double target = loader switch
        {
            MinecraftModLoaderKind.Forge or MinecraftModLoaderKind.NeoForge
                or MinecraftModLoaderKind.Fabric or MinecraftModLoaderKind.Quilt
                or MinecraftModLoaderKind.Cleanroom => 4.5d,
            MinecraftModLoaderKind.OptiFine => 3d,
            _ => 2.5d,
        };
        return Math.Max(512, (int)Math.Round(Math.Min(target, Math.Max(1.5d, availableGigabytes * 0.4d)) * 1024d));
    }

    private static int SliderValueToMemoryMegabytes(int value)
    {
        double gigabytes = value switch
        {
            <= 12 => value * 0.1d + 0.3d,
            <= 25 => (value - 12) * 0.5d + 1.5d,
            <= 33 => value - 25d + 8d,
            _ => (value - 33d) * 2d + 16d,
        };
        return Math.Max(256, (int)Math.Round(gigabytes * 1024d));
    }

    private T GetSetting<T>(string key, T fallback)
    {
        XsrResult<T> result = _settings.GetValue<T>(key);
        return result.IsSuccess ? result.Value : fallback;
    }

    private string SelectExecutable(JavaInstallation installation)
    {
        bool forceConsole = GetSetting("LaunchAdvanceNoJavaw", false);
        return !forceConsole && installation.WindowedJavaExecutablePath is { Length: > 0 } windowed
            ? windowed
            : installation.JavaExecutablePath;
    }

    private string SelectWindowedSibling(string executable)
    {
        if (!OperatingSystem.IsWindows() || GetSetting("LaunchAdvanceNoJavaw", false))
        {
            return executable;
        }

        string windowed = Path.Combine(Path.GetDirectoryName(executable)!, "javaw.exe");
        return File.Exists(windowed) ? windowed : executable;
    }

    private static string[] SplitClasspathHead(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split([';', Path.PathSeparator], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static int JavaMajor(Version version) => version.Major == 1 ? version.Minor : version.Major;

    private sealed record ResolvedJava(string ExecutablePath, int MajorVersion);
}
