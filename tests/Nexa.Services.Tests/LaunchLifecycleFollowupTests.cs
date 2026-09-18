using System.Diagnostics;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Crash;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.ModLoaders;
using Nexa.Services.Minecraft.Process;
using Nexa.Xsr;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask JavaMajorChoiceUsesInstalledOrDownloads()
    {
        foreach (bool installed in new[] { false, true })
        {
            var installer = new RecordingStubInstaller();
            var candidates = installed ? await ComposeWorkingJavaLocator().FindAllAsync() : [];
            var port = new LongLivedProcessPort();
            var (coordinator, host, _, root) = ComposeAcquisitionCoordinator(installer, processPort: port,
                javaLocator: new AppearingJavaLocator(candidates));
            try
            {
                var launch = Task.Run(() => coordinator.StartAsync("1.20.1", 0).AsTask());
                var store = host.StateStore;
                AssertTrue(SpinWait.SpinUntil(() => store.ReadAppliedValue(store.Resolve(MinecraftLaunchProgressState.AcquirePendingKey)) is true, TimeSpan.FromSeconds(5)));
                AssertFalse((await coordinator.SelectJavaVersionAsync(8)).IsSuccess);
                AssertTrue((await coordinator.SelectJavaVersionAsync(17)).IsSuccess);
                var result = await launch.WaitAsync(TimeSpan.FromSeconds(10));
                AssertTrue(result.IsSuccess, "Java choice launch failed: " + result.Error?.Message);
                AssertEqual(installed ? 0 : 1, installer.Calls);
                AssertFalse((await coordinator.SelectJavaVersionAsync(17)).IsSuccess);
            }
            finally
            {
                if (port.LastProcess is { } process)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                Directory.Delete(root, true);
            }
        }
    }

    private sealed class AppearingJavaLocator(IReadOnlyList<JavaRuntimeCandidate> candidates) : IJavaRuntimeLocator
    {
        private int _calls;
        public ValueTask<IReadOnlyList<JavaRuntimeCandidate>> FindAllAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<JavaRuntimeCandidate>>(Interlocked.Increment(ref _calls) == 1 ? [] : candidates);
        public ValueTask<JavaRuntimeCandidate?> InspectAsync(string javaExecutablePath, CancellationToken cancellationToken = default) => ValueTask.FromResult<JavaRuntimeCandidate?>(null);
    }

    private static async ValueTask ProcessOutputDrainsAndAbnormalExitIsAnalyzed()
    {
        using LibraryFixture fixture = new();
        await using var service = new MinecraftProcessService(new NoisyExitPort(), fixture.Host.StateStore);
        var plan = new MinecraftLaunchPlan("ignored", Path.GetTempPath(), [], [], [],
            new MinecraftModLoaderDescriptor(MinecraftModLoaderKind.Vanilla, null, null, []));
        var session = await service.StartAsync(plan, "noisy-game");
        AssertEqual(7, await session.WaitForExitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)));
        var key = fixture.Host.StateStore.Resolve(MinecraftProcessStateComposition.FailuresKey);
        AssertTrue(SpinWait.SpinUntil(() => fixture.Host.StateStore.ReadCollection<MinecraftProcessFailure>(key).Items.Count == 1, TimeSpan.FromSeconds(5)));
        var failure = fixture.Host.StateStore.ReadCollection<MinecraftProcessFailure>(key).Items[0];
        AssertEqual(session.Snapshot.SessionId, failure.SessionId);
        AssertEqual(MinecraftLaunchFaultCode.OutOfMemory, failure.Report.Code);
        AssertTrue(failure.Report.Evidence.Count <= 200);
        AssertTrue(failure.Report.Evidence.All(line => line.Length <= 2048));
        AssertFalse(failure.Report.Evidence.Any(line => line.Contains("secret-test-token", StringComparison.Ordinal)));
    }

    private sealed class NoisyExitPort : IMinecraftProcessPort
    {
        public ValueTask<System.Diagnostics.Process> StartAsync(ProcessStartInfo _, CancellationToken cancellationToken = default)
        {
            var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (OperatingSystem.IsWindows())
            { info.ArgumentList.Add("/c"); info.ArgumentList.Add("for /L %i in (1,1,8000) do @echo output-output-output-output-output-output-output & (echo java.lang.OutOfMemoryError --accessToken secret-test-token 1>&2) & exit /b 7"); }
            else
            { info.ArgumentList.Add("-c"); info.ArgumentList.Add("i=0; while [ $i -lt 8000 ]; do echo output-output-output-output-output-output-output; i=$((i+1)); done; echo 'java.lang.OutOfMemoryError --accessToken secret-test-token' >&2; exit 7"); }
            return ValueTask.FromResult(System.Diagnostics.Process.Start(info)!);
        }
    }

    private static async ValueTask LibraryDeleteIsContainedAndRecoverable()
    {
        using LibraryFixture fixture = new();
        string root = fixture.AddRoot("delete");
        string version = CreateVersionDirectory(root, "test", new System.Text.Json.Nodes.JsonObject { ["id"] = "test", ["type"] = "release" });
        File.WriteAllText(Path.Combine(version, "keep.txt"), "world");
        using var library = new MinecraftLibraryService(fixture.Host.Settings, root, new MinecraftInstanceDiscovery());
        AssertTrue((await library.RefreshAsync()).IsSuccess);
        AssertFalse((await library.DeleteInstanceAsync(root, "..")).IsSuccess);
        AssertTrue((await library.DeleteInstanceAsync(root, "test")).IsSuccess);
        AssertFalse(Directory.Exists(version));
        AssertEqual("world", File.ReadAllText(Path.Combine(Directory.GetDirectories(Path.Combine(root, ".recycle")).Single(), "keep.txt")));
        AssertEqual(0, fixture.Snapshot.Instances.Count);
    }
}
