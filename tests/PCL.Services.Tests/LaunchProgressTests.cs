using System.Text.Json.Nodes;
using PCL.Services.Accounts;
using PCL.Services.Foundation;
using PCL.Services.Logging;
using PCL.Services.Minecraft;
using PCL.Services.Minecraft.Java;
using PCL.Services.Minecraft.Launch;
using PCL.Services.Minecraft.Libraries;
using PCL.Services.Minecraft.Process;
using PCL.Services.Settings;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Services.Tests;

// XSR-712: the launch progress contract — the legacy stage-weight table, one coherent cell
// set per report, the pipeline narration from login through a launched game, and pipeline
// cancellation.
internal static partial class Program
{
    private static void LaunchStageWeightsMatchLegacyTable()
    {
        double total = MinecraftLaunchStages.Total;
        AssertEqual(44d, total);
        AssertEqual(0d, MinecraftLaunchStages.ProgressAt(0d));
        AssertEqual(1d, MinecraftLaunchStages.ProgressAt(total));
        AssertEqual(15d / 44d, MinecraftLaunchStages.ProgressAt(MinecraftLaunchStages.LoginWeight));
        AssertEqual(30d / 44d, MinecraftLaunchStages.ProgressAt(
            MinecraftLaunchStages.LoginWeight + MinecraftLaunchStages.CompleteFilesWeight));
        AssertEqual(36d / 44d, MinecraftLaunchStages.ProgressAt(36d));
        AssertTrue(Math.Abs(40d / 44d - MinecraftLaunchStages.ProgressAt(
            36d + MinecraftLaunchStages.ExtractNativesWeight + MinecraftLaunchStages.StartProcessWeight)) < 1e-9);
    }

    private static void ProgressPublisherWritesCoherentCells()
    {
        XsrStateStoreBuilder builder = new();
        MinecraftLaunchProgressState.DeclareState(builder);
        XsrStateStore store = builder.Build();
        MinecraftLaunchProgressPublisher publisher = new(store);

        publisher.Start();
        AssertTrue(ReadProgressFlag(store, MinecraftLaunchProgressState.ActiveKey));
        AssertEqual("get_java", ReadProgressText(store, MinecraftLaunchProgressState.StageKey));
        AssertEqual(0d, ReadProgressNumber(store, MinecraftLaunchProgressState.ProgressKey));

        publisher.Report(new MinecraftLaunchStageReport(
            "login", 0.5d, IsLaunched: false, Method: "offline", DownloadSpeed: "1.2 MB/s"));
        AssertEqual("login", ReadProgressText(store, MinecraftLaunchProgressState.StageKey));
        AssertEqual(0.5d, ReadProgressNumber(store, MinecraftLaunchProgressState.ProgressKey));
        AssertEqual("offline", ReadProgressText(store, MinecraftLaunchProgressState.MethodKey));
        AssertEqual("1.2 MB/s", ReadProgressText(store, MinecraftLaunchProgressState.SpeedKey));
        AssertFalse(ReadProgressFlag(store, MinecraftLaunchProgressState.LaunchedKey));

        publisher.Stop();
        AssertFalse(ReadProgressFlag(store, MinecraftLaunchProgressState.ActiveKey));
        AssertEqual(string.Empty, ReadProgressText(store, MinecraftLaunchProgressState.StageKey));
    }

    private static void CancelActiveLaunchWithoutLaunchReturnsFalse()
    {
        string root = CreateTempDirectory();
        try
        {
            FoundationHost host = FoundationComposer.Compose(
                new InMemorySettingsPort(),
                LauncherDefaults.CreateSchema(),
                new LaunchProfileFilePort(Path.Combine(root, "profiles.json")));
            MinecraftLaunchCoordinator coordinator = new(
                root,
                Path.Combine(root, "runtime"),
                new MinecraftInstanceDiscovery(),
                host.Accounts,
                host.Settings,
                new JavaSelectionService(new InMemoryJavaLocator([])),
                new NeverJavaInstaller(),
                new MinecraftLaunchExecutor(new MinecraftProcessService()));
            AssertFalse(coordinator.CancelActiveLaunch());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async ValueTask LaunchPipelineNarratesStagesAndReachesLaunchedAsync()
    {
        string root = CreateTempDirectory();
        try
        {
            string baseDirectory = CreateVersionDirectory(root, "1.20.1", new JsonObject
            {
                ["id"] = "1.20.1",
                ["type"] = "release",
                ["mainClass"] = "net.minecraft.client.main.Main",
                ["releaseTime"] = "2023-06-12T00:00:00Z",
                ["javaVersion"] = new JsonObject
                {
                    ["majorVersion"] = 17,
                    ["component"] = "java-runtime-gamma",
                },
            });
            MinecraftInstanceMetadataStore metadataStore = new();
            await metadataStore.SaveAsync(baseDirectory, new MinecraftInstanceMetadata());
            File.WriteAllBytes(Path.Combine(baseDirectory, "1.20.1.jar"), [0xCA, 0xFE]);

            SettingsSchema schema = LauncherDefaults.CreateSchema();
            FoundationHost host = FoundationComposer.Compose(
                new InMemorySettingsPort(),
                schema,
                new LaunchProfileFilePort(Path.Combine(root, "profiles.json")));
            AssertTrue(host.Accounts.AddProfile(new LaunchProfile
            {
                Username = "Player",
                Kind = LaunchProfileKind.Offline,
            }).IsSuccess);

            RecordingProgressPublisher progress = new(host.StateStore);
            string javaHome = Path.Combine(root, "test-java");
            string javaExecutable = Path.Combine(javaHome, "bin",
                OperatingSystem.IsWindows() ? "java.exe" : "java");
            Directory.CreateDirectory(Path.GetDirectoryName(javaExecutable)!);
            await File.WriteAllBytesAsync(javaExecutable, [0x00]);
            JavaRuntimeCandidate candidate = new(new JavaInstallation(
                javaHome,
                javaExecutable,
                null,
                new Version(17, 0, 10),
                JavaBrand.EclipseTemurin,
                JavaArchitecture.X64,
                is64Bit: true,
                isJre: false));
            MinecraftProcessService processes = new(new ExitingProcessPort(), host.StateStore);
            MinecraftLaunchCoordinator coordinator = new(
                root,
                Path.Combine(root, "runtime"),
                new MinecraftInstanceDiscovery(
                    versionDiscovery: new MinecraftVersionDiscovery(),
                    metadataStore: metadataStore),
                host.Accounts,
                host.Settings,
                new JavaSelectionService(new InMemoryJavaLocator([candidate])),
                new NeverJavaInstaller(),
                new MinecraftLaunchExecutor(processes),
                new MinecraftLaunchPlatform(
                    MinecraftLibraryOperatingSystem.Linux,
                    "6.12",
                    Is64BitArchitecture: true,
                    IsArm64Architecture: false),
                progress: progress);

            XsrResult result = await coordinator.StartAsync("1.20.1", accountIndex: 0);
            if (!result.IsSuccess)
            {
                Console.WriteLine("DIAG launch failed: " + result.Error?.Code.Value + " " + result.Error?.Message);
            }

            AssertTrue(result.IsSuccess);


            // The narration reaches the launched state through the pipeline stage order; each
            // stage reports its entry, optional heartbeats, and completion, so assertions use
            // first-appearance order and monotonic progress instead of an exact report list.
            string[] expectedOrder =
            {
                "login", "complete_files", "get_java", "get_arguments",
                "extract_natives", "start_process", "end",
            };
            string[] firstAppearance = progress.Stages.Distinct().ToArray();
            if (!expectedOrder.SequenceEqual(firstAppearance))
            {
                Console.WriteLine("DIAG stages: " + string.Join(",", progress.Stages));
            }

            AssertTrue(expectedOrder.SequenceEqual(firstAppearance));
            AssertTrue(progress.Progress.SequenceEqual(progress.Progress.OrderBy(value => value)));
            AssertTrue(ReadProgressFlag(host.StateStore, MinecraftLaunchProgressState.LaunchedKey));
            AssertEqual(1d, ReadProgressNumber(host.StateStore, MinecraftLaunchProgressState.ProgressKey));
            AssertEqual("end", ReadProgressText(host.StateStore, MinecraftLaunchProgressState.StageKey));
            AssertEqual("offline", ReadProgressText(host.StateStore, MinecraftLaunchProgressState.MethodKey));
            AssertFalse(coordinator.CancelActiveLaunch());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async ValueTask JavaAcquisitionWaitsForDecisionAndDenialFailsLaunch()
    {
        (MinecraftLaunchCoordinator coordinator, FoundationHost host, RecordingNeverInstaller installer, string root) =
            ComposeAcquisitionCoordinator(new RecordingNeverInstaller());
        try
        {
            Task<XsrResult> launchTask = Task.Run(
                () => coordinator.StartAsync("1.20.1", accountIndex: 0).AsTask());
            XsrStateStore store = host.StateStore;
            AssertTrue(SpinWait.SpinUntil(
                () => store.ReadAppliedValue(store.Resolve(MinecraftLaunchProgressState.AcquirePendingKey)) is bool waiting && waiting,
                TimeSpan.FromSeconds(5)));
            AssertTrue(coordinator.DecideJavaAcquisition(approve: false));
            XsrResult result = await launchTask;
            AssertFalse(result.IsSuccess);
            AssertEqual(MinecraftErrors.JavaUnavailableCode, result.Error!.Code);
            AssertEqual(0, installer.Calls);
            AssertFalse(ReadProgressFlag(store, MinecraftLaunchProgressState.AcquirePendingKey));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async ValueTask JavaAcquisitionApprovalDownloadsAndLaunches()
    {
        RecordingStubInstaller installer = new();
        (MinecraftLaunchCoordinator coordinator, FoundationHost host, _, string root) =
            ComposeAcquisitionCoordinator(installer);
        try
        {
            Task<XsrResult> launchTask = Task.Run(
                () => coordinator.StartAsync("1.20.1", accountIndex: 0).AsTask());
            XsrStateStore store = host.StateStore;
            AssertTrue(SpinWait.SpinUntil(
                () => store.ReadAppliedValue(store.Resolve(MinecraftLaunchProgressState.AcquirePendingKey)) is bool waiting && waiting,
                TimeSpan.FromSeconds(5)));
            AssertEqual("java-runtime-gamma", store.ReadAppliedValue(store.Resolve(MinecraftLaunchProgressState.AcquireComponentKey)));
            AssertTrue(coordinator.DecideJavaAcquisition(approve: true));
            XsrResult result = await launchTask;
            if (!result.IsSuccess)
            {
                Console.WriteLine("DIAG approve launch failed: " + result.Error?.Message);
            }

            AssertTrue(result.IsSuccess);
            AssertEqual(1, installer.Calls);
            AssertTrue(ReadProgressFlag(store, MinecraftLaunchProgressState.LaunchedKey));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Composes a launchable corpus with NO compatible Java installed, so the pipeline stops
    /// at the acquisition approval gate.
    /// </summary>
    private static (MinecraftLaunchCoordinator Coordinator, FoundationHost Host, T Installer, string Root)
        ComposeAcquisitionCoordinator<T>(T installer) where T : IJavaRuntimeInstaller
    {
        string root = CreateTempDirectory();
        string baseDirectory = CreateVersionDirectory(root, "1.20.1", new JsonObject
        {
            ["id"] = "1.20.1",
            ["type"] = "release",
            ["mainClass"] = "net.minecraft.client.main.Main",
            ["releaseTime"] = "2023-06-12T00:00:00Z",
            ["javaVersion"] = new JsonObject
            {
                ["majorVersion"] = 17,
                ["component"] = "java-runtime-gamma",
            },
        });
        MinecraftInstanceMetadataStore metadataStore = new();
        metadataStore.SaveAsync(baseDirectory, new MinecraftInstanceMetadata()).GetAwaiter().GetResult();
        File.WriteAllBytes(Path.Combine(baseDirectory, "1.20.1.jar"), [0xCA, 0xFE]);

        SettingsSchema schema = LauncherDefaults.CreateSchema();
        FoundationHost host = FoundationComposer.Compose(
            new InMemorySettingsPort(),
            schema,
            new LaunchProfileFilePort(Path.Combine(root, "profiles.json")));
        AssertTrue(host.Accounts.AddProfile(new LaunchProfile
        {
            Username = "Player",
            Kind = LaunchProfileKind.Offline,
        }).IsSuccess);
        MinecraftProcessService processes = new(new ExitingProcessPort(), host.StateStore);
        MinecraftLaunchCoordinator coordinator = new(
            root,
            Path.Combine(root, "runtime"),
            new MinecraftInstanceDiscovery(
                versionDiscovery: new MinecraftVersionDiscovery(),
                metadataStore: metadataStore),
            host.Accounts,
            host.Settings,
            new JavaSelectionService(new InMemoryJavaLocator([])),
            installer,
            new MinecraftLaunchExecutor(processes),
            new MinecraftLaunchPlatform(
                MinecraftLibraryOperatingSystem.Linux,
                "6.12",
                Is64BitArchitecture: true,
                IsArm64Architecture: false),
            progress: new MinecraftLaunchProgressPublisher(host.StateStore));
        return (coordinator, host, installer, root);
    }

    private sealed class RecordingNeverInstaller : IJavaRuntimeInstaller
    {
        public int Calls { get; private set; }

        public Task<string> InstallAsync(
            string requestedComponent,
            string runtimeRootDirectory,
            IProgress<JavaRuntimeInstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("The declined acquisition must not download.");
        }
    }

    /// <summary>Fakes a runtime installation by creating an executable and returning its path.</summary>
    private sealed class RecordingStubInstaller : IJavaRuntimeInstaller
    {
        public int Calls { get; private set; }

        public Task<string> InstallAsync(
            string requestedComponent,
            string runtimeRootDirectory,
            IProgress<JavaRuntimeInstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            string executable = Path.Combine(runtimeRootDirectory, requestedComponent,
                OperatingSystem.IsWindows() ? "java.exe" : "java");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllBytes(executable, [0x00]);
            return Task.FromResult(executable);
        }
    }

    private static async ValueTask SecondConcurrentLaunchIsRejectedAsAlreadyActive()
    {
        (MinecraftLaunchCoordinator coordinator, FoundationHost host, RecordingStubInstaller installer, string root) =
            ComposeAcquisitionCoordinator(installer: new RecordingStubInstaller());
        try
        {
            // Park the first pipeline at the acquisition gate, then start a second one.
            Task<XsrResult> first = Task.Run(
                () => coordinator.StartAsync("1.20.1", accountIndex: 0).AsTask());
            XsrStateStore store = host.StateStore;
            AssertTrue(SpinWait.SpinUntil(
                () => store.ReadAppliedValue(store.Resolve(MinecraftLaunchProgressState.AcquirePendingKey)) is bool waiting && waiting,
                TimeSpan.FromSeconds(5)));

            XsrResult second = await coordinator.StartAsync("1.20.1", accountIndex: 0);
            AssertFalse(second.IsSuccess);
            AssertEqual(MinecraftErrors.LaunchAlreadyActiveCode, second.Error!.Code);

            // The first pipeline keeps its registration: cancellation is single-flight too.
            AssertTrue(coordinator.DecideJavaAcquisition(approve: true));
            XsrResult firstResult = await first;
            if (!firstResult.IsSuccess)
            {
                Console.WriteLine("DIAG first launch failed: " + firstResult.Error?.Message);
            }

            AssertTrue(firstResult.IsSuccess);
            AssertEqual(1, installer.Calls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async ValueTask UnsupportedAccountKindsRefuseToLaunch()
    {
        string root = CreateTempDirectory();
        try
        {
            FoundationHost host = FoundationComposer.Compose(
                new InMemorySettingsPort(),
                LauncherDefaults.CreateSchema(),
                new LaunchProfileFilePort(Path.Combine(root, "profiles.json")));
            AssertTrue(host.Accounts.AddProfile(new LaunchProfile
            {
                Username = "Player",
                Kind = LaunchProfileKind.LittleSkin,
            }).IsSuccess);
            AccountLaunchIdentityResolver resolver = new(host.Accounts);
            LaunchProfile profile = host.Accounts.GetProfile(0).Value;
            XsrResult<MinecraftLaunchIdentity> identity = await resolver.ResolveAsync(0, profile);
            AssertFalse(identity.IsSuccess);
            AssertEqual(AccountErrors.LaunchNotSupportedCode, identity.Error!.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool ReadProgressFlag(XsrStateStore store, XsrSemanticId key) =>
        store.ReadAppliedValue(store.Resolve(key)) is bool flag && flag;

    private static string ReadProgressText(XsrStateStore store, XsrSemanticId key) =>
        store.ReadAppliedValue(store.Resolve(key)) as string ?? string.Empty;

    private static double ReadProgressNumber(XsrStateStore store, XsrSemanticId key) =>
        store.ReadAppliedValue(store.Resolve(key)) is double value ? value : -1d;

    private sealed class RecordingProgressPublisher(XsrStateStore store)
        : MinecraftLaunchProgressPublisher(store)
    {
        public List<string> Stages { get; } = [];

        public List<double> Progress { get; } = [];

        public override void Report(MinecraftLaunchStageReport report)
        {
            if (report.Stage.Length > 0)
            {
                Stages.Add(report.Stage);
                Progress.Add(report.Progress);
            }

            base.Report(report);
        }
    }

    /// <summary>A process port that starts a child which exits immediately with code zero.</summary>
    private sealed class ExitingProcessPort : IMinecraftProcessPort
    {
        public ValueTask<System.Diagnostics.Process> StartAsync(
            System.Diagnostics.ProcessStartInfo startInfo,
            CancellationToken cancellationToken = default)
        {
            System.Diagnostics.ProcessStartInfo exit = OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo("cmd", "/c exit 0")
                : new System.Diagnostics.ProcessStartInfo("/bin/sh", "-c exit 0");
            exit.UseShellExecute = false;
            exit.CreateNoWindow = true;
            return ValueTask.FromResult(System.Diagnostics.Process.Start(exit)!);
        }
    }
}
