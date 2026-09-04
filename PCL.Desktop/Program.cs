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
        string version = System.Reflection.Assembly.GetEntryAssembly()?
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? string.Empty;
        string[] segments = version.Split('.');
        return segments.Length >= 4 && segments[3] is "alpha" or "beta" or "ci"
            ? segments[3]
            : null;
    }
    private static int Main(string[] args)
    {
        // Composition root: the two-phase foundation composition. Phase one declares every
        // foundation module's state into one shared builder; phase two builds the store once
        // and constructs the services over it. Trim analysis therefore sees the real
        // foundation call graph, not an empty shell.
        AppFolders folders = AppFolders.ResolveDefault();
        folders.EnsureFolder(FolderNames.Logs);
        string settingsFolder = folders.EnsureFolder(FolderNames.Settings);
        string profilesFolder = folders.EnsureFolder(FolderNames.Profiles);
        string minecraftRootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".minecraft");

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
        FoundationHost host = FoundationComposer.Compose(
            new LauncherSettingsJsonPort(System.IO.Path.Combine(settingsFolder, "settings.json"), settingsSchema),
            settingsSchema,
            new LaunchProfileFilePort(System.IO.Path.Combine(profilesFolder, "profiles.json")),
            observer: stateObservation,
            declareHostState: LaunchPageState.DeclareState);
        // A console-attached launch (terminal) mirrors the full operation trace to the console
        // and the session file at RealTime verbosity; a detached GUI launch keeps the Info
        // gate and records the session file only.
        string logFilePath = System.IO.Path.Combine(folders.EnsureFolder(FolderNames.Logs), "launcher.log");
        // Level policy: alpha/beta/ci builds and console-attached launches run at RealTime so
        // every XSR operation is traceable; release builds default to Info for users.
        bool prereleaseChannel = !string.IsNullOrWhiteSpace(ResolveVersionChannel());
        bool consoleAttached = GetConsoleWindow() != IntPtr.Zero || Console.IsOutputRedirected;
        if (consoleAttached || prereleaseChannel)
        {
            host.Logging.MaximumLevel = LogLevel.RealTime;
        }

        if (consoleAttached)
        {
            host.Logging.AddSink(new ConsoleLogSink());
        }

        host.Logging.AddSink(new FileLogSink(logFilePath));
        operationLog.Attach(host.Logging);
        // The session lifecycle narrates startup/shutdown milestones at Info: every subsystem
        // the composition root brings up (and later stops) is a phase on one shared timeline.
        XsrLifecycle session = new("LauncherSession", operationLog.Lifecycle);
        session.Enter(XsrLifecyclePhase.Starting);
        host.Logging.Info(
            "Launcher",
            $"PCL Nexa（{ResolveVersionChannel() ?? "release"} 通道）已启动，"
            + (consoleAttached ? "控制台会话，RealTime 日志已启用。" : "日志记录到文件。"));
        uiIntents.IntentEmitted += (_, e) => operationLog.WriteIntent(e.Intent.Command);
        FoundationRuntime runtime = FoundationRuntimeComposer.Compose(host, operationLog.Dispatch);
        using AccountOnboardingRuntime accounts = AccountOnboardingRuntimeComposer.Compose(host);
        using MinecraftRuntime minecraft = MinecraftRuntimeComposer.Compose(
            host,
            minecraftRootDirectory);
        host.Logging.Debug(
            "Launcher",
            $"组合完成：{runtime.Host.Services.Count} 服务，"
            + $"{runtime.Commands.Count + minecraft.Commands.Count} 命令，"
            + $"{runtime.Queries.Count + minecraft.Queries.Count} 查询。");
        session.Enter(XsrLifecyclePhase.Running);

        XsrUiShell shell = PxmlShellComposer.Compose(
            runtime.Host.StateStore,
            uiRuntime,
            new XsrUiShellOptions
            {
                Style = ResolveShellStyle(args),
                Title = "Nexa Launcher",
                Version = "2.0.0.alpha.1",
            },
            uiIntents);

        // The launch page is the first product vertical slice: it routes navigation intents to
        // pages inside the shell content host and dispatches the real launch command.
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

        Console.WriteLine(
            $"PCL Nexa foundation composed: {runtime.Host.Services.Count} services, "
            + $"{runtime.Commands.Count} command routes, {runtime.Queries.Count} query routes over one host state store; "
            + $"Minecraft routes: {minecraft.Commands.Count} commands/{minecraft.Queries.Count} queries; "
            + $"UI style: {shell.Style}.");
        if (args.Any(argument => string.Equals(argument, "--validate-shell", StringComparison.OrdinalIgnoreCase)))
        {
            XsrUiScene scene = shell.Render(new XsrUiSize(1280, 800));
            Console.WriteLine($"PXML shell validated: {scene.Count} semantic nodes.");
            session.Enter(XsrLifecyclePhase.Stopping);
            session.Enter(XsrLifecyclePhase.Stopped);
            return 0;
        }

        int exitCode = AvaloniaUiShellHost.Run(shell, args, platformActions);
        session.Enter(XsrLifecyclePhase.Stopping);
        session.Enter(XsrLifecyclePhase.Stopped);
        return exitCode;
    }

    private static XsrUiShellStyle ResolveShellStyle(string[] args) =>
        args.Any(argument =>
            string.Equals(argument, "--ui-style=liquid-glass", StringComparison.OrdinalIgnoreCase)
            || string.Equals(argument, "--liquid-glass", StringComparison.OrdinalIgnoreCase))
            ? XsrUiShellStyle.LiquidGlass
            : XsrUiShellStyle.Experimental;
}
