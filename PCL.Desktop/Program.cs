using System.Reflection;
using PCL.Desktop.Ui;
using PCL.Services.Accounts;
using PCL.Services.Composition;
using PCL.Services.Files;
using PCL.Services.Foundation;
using PCL.Services.Logging;
using PCL.Services.Minecraft;
using PCL.Services.Settings;
using PCL.UI.Next;
using PCL.UI.Next.Backend.Avalonia;
using PCL.Xsr.Runtime;

namespace PCL.Desktop;

internal static class Program
{
    /// <summary>
    /// A WinExe process has a console handle only when launched from a terminal (or with
    /// redirected output); double-clicking the exe reports none.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetConsoleWindow();

    /// <summary>
    /// The version channel (alpha, beta, ci, or release) parsed from the informational version
    /// that Xsr.Version.props composes, e.g. 2.0.0.ci.1a2b3c.
    /// </summary>
    private static string? ResolveVersionChannel()
    {
        string version = ResolveInformationalVersion();
        string[] segments = version.Split('.');
        return segments.Length >= 4 && segments[3] is "alpha" or "beta" or "ci"
            ? segments[3]
            : null;
    }

    private static string ResolveInformationalVersion() => Assembly.GetEntryAssembly()?
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    private static int Main(string[] args)
    {
        LogService? log = null;
        FileLogSink? fileSink = null;
        string stage = "resolve_folders";
        int exitCode = 1;
        void OnUnhandled(object sender, UnhandledExceptionEventArgs e) => log?.Error(
            "Launcher", $"Unhandled exception terminating={e.IsTerminating} stage={stage}",
            e.ExceptionObject is Exception exception ? ExceptionDiagnostics.Describe(exception) : "Unknown exception object.");
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) =>
            log?.Error("Launcher", "Unobserved background task failure", ExceptionDiagnostics.Describe(e.Exception));
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            exitCode = Run(args, logging => log = logging, sink => fileSink = sink, value => stage = value);
            return exitCode;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            string message = $"Launcher session failed stage={stage}";
            string detail = ExceptionDiagnostics.Describe(exception);
            log?.Error("Launcher", message, detail);
            if (log is null && fileSink is not null)
            {
                // Keep early folder/schema failures in the same file, before a host store exists.
                LogEntry bootstrap = new(0, DateTimeOffset.UtcNow, LogLevel.Error, "Launcher",
                    LogRedactor.Redact(message), LogRedactor.Redact(detail));
                fileSink.Write(bootstrap, bootstrap.ToDisplayText());
            }
            // A directory/bootstrap failure may precede the host logger entirely.
            try { Console.Error.WriteLine($"{message}{Environment.NewLine}{detail}"); } catch (IOException) { }
            return 1;
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
            log?.Info("Launcher", $"Session ended pid={Environment.ProcessId} exit_code={exitCode} last_stage={stage}");
            fileSink?.Dispose();
        }
    }

    private static int Run(string[] args, Action<LogService> onLogReady, Action<FileLogSink> onSinkReady, Action<string> setStage)
    {
        // Composition root: the two-phase foundation composition. Phase one declares every
        // foundation module's state into one shared builder; phase two builds the store once
        // and constructs the services over it. Trim analysis therefore sees the real
        // foundation call graph, not an empty shell.
        AppFolders folders = AppFolders.ResolveDefault();
        string logFilePath = Path.Combine(folders.EnsureFolder(FolderNames.Logs), "launcher.log");
        FileLogSink sink = new(logFilePath);
        onSinkReady(sink);
        setStage("ensure_settings_folder");
        string settingsFolder = folders.EnsureFolder(FolderNames.Settings);
        setStage("ensure_profiles_folder");
        string profilesFolder = folders.EnsureFolder(FolderNames.Profiles);
        string minecraftRootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".minecraft");

        setStage("declare_host_state");
        SettingsSchema settingsSchema = LauncherDefaults.CreateSchema();
        // This context exists before the host store is built. Its observer is therefore the
        // production publication path, and PXML later loads into the very same render tree.
        XsrUiRuntimeContext uiRuntime = new();
        DesktopUiIntentSink uiIntents = new();

        // The operation log rides the same store observation as the render bridge: command,
        // query, state, event, scheduler, and lifecycle telemetry flow into LogService through
        // one composition-root wiring, with the logging domain excluded to prevent recursion.
        XsrOperationLog operationLog = new();
        XsrCompositeStateObserver stateObservation = new(uiRuntime.StateBridge, operationLog.State);
        string channel = ResolveVersionChannel() ?? "release";
        bool consoleAttached = Console.IsOutputRedirected
            || (OperatingSystem.IsWindows() && GetConsoleWindow() != IntPtr.Zero);
        setStage("compose_foundation");
        FoundationHost host = FoundationComposer.Compose(
            new LauncherSettingsJsonPort(System.IO.Path.Combine(settingsFolder, "settings.json"), settingsSchema),
            settingsSchema,
            new LaunchProfileFilePort(System.IO.Path.Combine(profilesFolder, "profiles.json")),
            observer: stateObservation,
            declareHostState: LaunchPageState.DeclareState,
            configureLogging: logging =>
            {
                if (consoleAttached || channel != "release") logging.MaximumLevel = LogLevel.RealTime;
                if (consoleAttached) logging.AddSink(new ConsoleLogSink());
                logging.AddSink(sink);
                onLogReady(logging);
                operationLog.Attach(logging);
                logging.Info("Launcher", $"Session started pid={Environment.ProcessId} version={ResolveInformationalVersion()} channel={channel} level={logging.MaximumLevel} runtime={Environment.Version} os={System.Runtime.InteropServices.RuntimeInformation.OSDescription} arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
                logging.Info("Launcher", "Foundation composition started; loading persisted profiles and settings.");
            });
        // The session lifecycle narrates startup/shutdown milestones at Info: every subsystem
        // the composition root brings up (and later stops) is a phase on one shared timeline.
        XsrLifecycle session = new("LauncherSession", operationLog.Lifecycle);
        session.Enter(XsrLifecyclePhase.Starting);
        host.Logging.Info("Launcher", "Foundation composition completed; registering runtime routes.");
        setStage("compose_runtimes");
        uiIntents.IntentEmitted += (_, e) => operationLog.WriteIntent(e.Intent.Command, e.Intent.CorrelationId);
        FoundationRuntime runtime = FoundationRuntimeComposer.Compose(host, operationLog.Dispatch);
        using AccountOnboardingRuntime accounts = AccountOnboardingRuntimeComposer.Compose(host, observer: operationLog.Dispatch);
        using MinecraftRuntime minecraft = MinecraftRuntimeComposer.Compose(
            host,
            minecraftRootDirectory, observer: operationLog.Dispatch);
        host.Logging.Debug(
            "Launcher",
            $"Runtime composition completed services={runtime.Host.Services.Count} "
            + $"commands={runtime.Commands.Count + minecraft.Commands.Count + accounts.Commands.Count} "
            + $"queries={runtime.Queries.Count + minecraft.Queries.Count}");

        setStage("load_pxml_shell");
        host.Logging.Info("Launcher", "Loading embedded PXML shell and attaching the host state bridge.");
        XsrUiShell shell = PxmlShellComposer.Compose(
            runtime.Host.StateStore,
            uiRuntime,
            new XsrUiShellOptions
            {
                Title = "Nexa Launcher",
                Version = "2.0.0.alpha.1",
            },
            uiIntents);

        // The launch page is the first product vertical slice: it routes navigation intents to
        // pages inside the shell content host and dispatches the real launch command.
        setStage("attach_product_controllers");
        using LaunchPageController launchPage = new(
            shell,
            uiIntents,
            minecraft,
            runtime.Commands,
            runtime.Host.StateStore,
            minecraftRootDirectory, accountCommands: accounts.Commands);
        AvaloniaUiPlatformActions platformActions = new();
        using AccountFormController accountForm = new(shell, uiIntents, accounts.Commands,
            runtime.Host.StateStore, launchPage.AccountBody, new NativeAccountUiEffects(platformActions));
        launchPage.Attach();
        host.Logging.Info("Launcher", "Product controllers attached; initial instance scan scheduled.");
        session.Enter(XsrLifecyclePhase.Running);

        Console.WriteLine(
            $"PCL Nexa foundation composed: {runtime.Host.Services.Count} services, "
            + $"{runtime.Commands.Count} command routes, {runtime.Queries.Count} query routes over one host state store; "
            + $"Minecraft routes: {minecraft.Commands.Count} commands/{minecraft.Queries.Count} queries; "
            + $"UI style: {shell.Style}.");
        if (args.Any(argument => string.Equals(argument, "--validate-shell", StringComparison.OrdinalIgnoreCase)))
        {
            setStage("validate_shell");
            host.Logging.Info("Launcher", "Headless shell validation started viewport=1280x800");
            XsrUiScene scene = shell.Render(new XsrUiSize(1280, 800));
            Console.WriteLine($"PXML shell validated: {scene.Count} semantic nodes.");
            host.Logging.Info("Launcher", $"Headless shell validation completed nodes={scene.Count}");
            setStage("shutdown");
            session.Enter(XsrLifecyclePhase.Stopping);
            session.Enter(XsrLifecyclePhase.Stopped);
            return 0;
        }

        setStage("gui_lifetime");
        host.Logging.Info("Launcher", "Entering Avalonia GUI lifetime.");
        int exitCode = AvaloniaUiShellHost.Run(shell, args, platformActions);
        setStage("shutdown");
        host.Logging.Info("Launcher", $"GUI lifetime completed exit_code={exitCode}; releasing session resources.");
        session.Enter(XsrLifecyclePhase.Stopping);
        session.Enter(XsrLifecyclePhase.Stopped);
        return exitCode;
    }

}
