using System.Text.Json.Nodes;
using PCL.Services.Accounts;
using PCL.Services.Composition;
using PCL.Services.Foundation;
using PCL.Services.Minecraft;
using PCL.Services.Minecraft.Java;
using PCL.Services.Minecraft.Launch;
using PCL.Services.Minecraft.Libraries;
using PCL.Services.Minecraft.Process;
using PCL.Services.Settings;
using PCL.Xsr;

namespace PCL.Services.Tests;

internal static partial class Program
{
    private static void OfflineIdentityFallsBackToVanillaUuid()
    {
        AssertEqual(
            ("Alice", "uuid-alice"),
            MinecraftOfflineIdentity.Resolve("Alice", "uuid-alice"));

        string uuid = MinecraftOfflineIdentity.UuidFromName("Player");
        AssertEqual(32, uuid.Length);
        AssertFalse(uuid.Contains('-'));
        AssertEqual(uuid, MinecraftOfflineIdentity.UuidFromName("Player"));
        AssertEqual(("Player", uuid), MinecraftOfflineIdentity.Resolve(null, null));
    }

    private static async Task LaunchCoordinatorBuildsCompleteLowLevelRequest()
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
            _ = baseDirectory;
            string loaderDirectory = CreateVersionDirectory(root, "fabric-loader", new JsonObject
            {
                ["id"] = "fabric-loader-0.15.11-1.20.1",
                ["inheritsFrom"] = "1.20.1",
                ["mainClass"] = "net.fabricmc.loader.impl.launch.knot.KnotClient",
                ["libraries"] = new JsonArray(),
            });
            MinecraftInstanceMetadataStore metadataStore = new();
            string javaHome = Path.Combine(root, "test-java");
            string javaExecutable = Path.Combine(javaHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
            Directory.CreateDirectory(Path.GetDirectoryName(javaExecutable)!);
            await File.WriteAllBytesAsync(javaExecutable, [0x00]);
            await metadataStore.SaveAsync(loaderDirectory, new MinecraftInstanceMetadata
            {
                JavaSelectionMode = 2,
                SelectedJavaPath = javaExecutable,
                InstanceIsolation = true,
                UseSystemGlfw = true,
                JvmArguments = "-Dinstance=true",
                GameArguments = "--demo-flag",
                ClasspathHead = "first.jar;second.jar",
                ServerToEnter = "example.invalid:25565",
            });

            SettingsSchema schema = LauncherDefaults.CreateSchema();
            FoundationHost host = FoundationComposer.Compose(
                new InMemorySettingsPort(),
                schema,
                new LaunchProfileFilePort(Path.Combine(root, "profiles.json")));
            AssertTrue(host.Accounts.AddProfile(new LaunchProfile
            {
                Username = "Player",
                Uuid = "player-uuid",
                Kind = LaunchProfileKind.Offline,
            }).IsSuccess);
            AssertTrue(host.Settings.SetValue("LaunchArgumentWindowWidth", 1280).IsSuccess);
            AssertTrue(host.Settings.SetValue("LaunchArgumentWindowHeight", 720).IsSuccess);

            JavaRuntimeCandidate candidate = new(new JavaInstallation(
                javaHome,
                javaExecutable,
                null,
                new Version(17, 0, 10),
                JavaBrand.EclipseTemurin,
                JavaArchitecture.X64,
                is64Bit: true,
                isJre: false));
            InMemoryJavaLocator locator = new([candidate]);
            NeverJavaInstaller installer = new();
            MinecraftInstanceDiscovery instances = new(
                new MinecraftVersionDiscovery(),
                metadataStore);
            MinecraftProcessService processes = new(hostStore: host.StateStore);
            MinecraftLaunchCoordinator coordinator = new(
                root,
                Path.Combine(root, "runtime"),
                instances,
                host.Accounts,
                host.Settings,
                new JavaSelectionService(locator),
                installer,
                new MinecraftLaunchExecutor(processes),
                new MinecraftLaunchPlatform(
                    MinecraftLibraryOperatingSystem.Win32,
                    "10.0.26100",
                    Is64BitArchitecture: true,
                    IsArm64Architecture: false));

            XsrResult<MinecraftLaunchPreparation> result = await coordinator.PrepareAsync(
                "fabric-loader",
                accountIndex: 0);
            AssertTrue(result.IsSuccess);
            MinecraftLaunchRequest request = result.Value.Request;
            AssertEqual("fabric-loader-0.15.11-1.20.1", request.VersionId);
            AssertEqual(1, request.InheritedVersionJsons.Count);
            AssertEqual("1.20.1", request.InheritedVersionJsons[0]["id"]!.ToString());
            AssertEqual(MinecraftLibraryOperatingSystem.Win32, request.OperatingSystem);
            AssertEqual("10.0.26100", request.OperatingSystemVersion);
            AssertTrue(request.Is64BitArchitecture);
            AssertFalse(request.IsArm64Architecture);
            AssertEqual(javaExecutable, request.JavaExecutablePath);
            AssertEqual(17, request.JavaMajorVersion);
            AssertEqual(new Version(17, 0), result.Value.JavaRequirement.Range.Minimum);
            AssertEqual(1280, request.Width);
            AssertEqual(720, request.Height);
            AssertTrue(request.IsolatedGameDirectory);
            AssertTrue(request.UseSystemGlfw);
            AssertEqual("-Dinstance=true", request.CustomJvmArguments);
            AssertEqual("--demo-flag", request.CustomGameArguments);
            AssertEqual(2, request.ClasspathHeadEntries.Count);
            AssertEqual("example.invalid:25565", request.Server);
            AssertEqual("Player", request.PlayerName);
            AssertEqual("player-uuid", request.PlayerUuid);
            AssertEqual(MinecraftLaunchIdentityMode.Offline, request.IdentityMode);
            AssertEqual(0, installer.Calls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task LaunchCoordinatorRejectsIncompleteInheritance()
    {
        string root = CreateTempDirectory();
        try
        {
            string loaderDirectory = CreateVersionDirectory(root, "broken-loader", new JsonObject
            {
                ["id"] = "broken-loader",
                ["inheritsFrom"] = "missing-base",
                ["mainClass"] = "example.Main",
            });
            MinecraftInstanceMetadataStore metadataStore = new();
            await metadataStore.SaveAsync(loaderDirectory, new MinecraftInstanceMetadata());
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
            MinecraftProcessService processes = new(hostStore: host.StateStore);
            MinecraftLaunchCoordinator coordinator = new(
                root,
                Path.Combine(root, "runtime"),
                new MinecraftInstanceDiscovery(new MinecraftVersionDiscovery(), metadataStore),
                host.Accounts,
                host.Settings,
                new JavaSelectionService(new InMemoryJavaLocator([])),
                new NeverJavaInstaller(),
                new MinecraftLaunchExecutor(processes),
                new MinecraftLaunchPlatform(
                    MinecraftLibraryOperatingSystem.Linux,
                    "6.12",
                    Is64BitArchitecture: true,
                    IsArm64Architecture: false));

            XsrResult<MinecraftLaunchPreparation> result = await coordinator.PrepareAsync(
                "broken-loader",
                accountIndex: 0);
            AssertFalse(result.IsSuccess);
            AssertEqual(MinecraftErrors.LaunchPreparationFailedCode, result.Error!.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ProductionMinecraftRuntimeRegistersStartRoute()
    {
        string root = CreateTempDirectory();
        try
        {
            SettingsSchema schema = LauncherDefaults.CreateSchema();
            FoundationHost host = FoundationComposer.Compose(
                new InMemorySettingsPort(),
                schema,
                new LaunchProfileFilePort(Path.Combine(root, "profiles.json")));
            using MinecraftRuntime runtime = MinecraftRuntimeComposer.Compose(
                host,
                root,
                javaLocator: new InMemoryJavaLocator([]),
                javaInstaller: new NeverJavaInstaller(),
                platform: new MinecraftLaunchPlatform(
                    MinecraftLibraryOperatingSystem.Linux,
                    "6.12",
                    Is64BitArchitecture: true,
                    IsArm64Architecture: false));
            AssertTrue(runtime.LaunchCoordinator is not null);
            AssertTrue(runtime.Commands.TryResolve(MinecraftRouteIds.Start, out _));
            AssertTrue(runtime.Commands.TryResolve(MinecraftRouteIds.Launch, out _));
            AssertEqual(3, runtime.Commands.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateVersionDirectory(string root, string directoryName, JsonObject manifest)
    {
        string directory = Path.Combine(root, "versions", directoryName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, directoryName + ".json"), manifest.ToJsonString());
        return directory;
    }

    private sealed class NeverJavaInstaller : IJavaRuntimeInstaller
    {
        public int Calls { get; private set; }

        public Task<string> InstallAsync(
            string requestedComponent,
            string runtimeRootDirectory,
            IProgress<JavaRuntimeInstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("The test does not expect Java acquisition.");
        }
    }
}
