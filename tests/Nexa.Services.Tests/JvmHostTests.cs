using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.ModLoaders;
using Nexa.Services.Minecraft.Process;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void HostHistoryAdmissionPreservesPeakSemantics()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MinecraftLaunchPlan plan = new("java", "root", [], [], [],
            new MinecraftModLoaderDescriptor(MinecraftModLoaderKind.Vanilla, null, "example.Main", []))
        { JavaMajorVersion = 21 };
        MinecraftProcessSnapshot snapshot = new(Guid.NewGuid(), "fixture", 1, MinecraftProcessState.Exited,
            0, now.AddMinutes(-2), now);
        JvmHostObservation observation = new(snapshot.SessionId, "fixture", snapshot.StartedAt, now,
            100, 2048L * 1024 * 1024, 3000L * 1024 * 1024, 1, 1,
            0, 0, 0, 0, 0, 0, 0, null, null, 0, [], [])
        { RuntimePhysicalP95Bytes = 1024L * 1024 * 1024 };
        var accepted = JvmHostService.CreateHistorySample(plan, snapshot, observation, 120000, 240, true, true);
        AssertTrue(accepted is not null);
        AssertEqual(2048L, accepted!.PhysicalPeakMiB);
        AssertEqual(0L, accepted.NativePeakMiB);
        AssertEqual(0L, accepted.HeapPeakMiB);
        AssertEqual(0L, accepted.CommitPeakMiB);
        foreach (MinecraftProcessState state in new[] { MinecraftProcessState.Created, MinecraftProcessState.Running,
            MinecraftProcessState.Failed, MinecraftProcessState.Cancelled })
            AssertTrue(JvmHostService.CreateHistorySample(plan, snapshot with { State = state }, observation,
                120000, 240, true, true) is null);
        foreach (var invalid in new[] { snapshot with { ExitCode = 1 }, snapshot with { ExitCode = null }, snapshot with { EndedAt = null } })
            AssertTrue(JvmHostService.CreateHistorySample(plan, invalid, observation, 120000, 240, true, true) is null);
        foreach (var invalid in new[] { observation with { ExitCode = 1 }, observation with { PeakWorkingSetBytes = 0 },
            observation with { CrashReportPath = "crash.txt" }, observation with { HsErrPath = "hs_err.log" } })
            AssertTrue(JvmHostService.CreateHistorySample(plan, snapshot, invalid, 120000, 240, true, true) is null);
        AssertTrue(JvmHostService.CreateHistorySample(plan, snapshot, observation, 59999, 240, true, true) is null);
        AssertTrue(JvmHostService.CreateHistorySample(plan, snapshot, observation, 120000, 29, true, true) is null);
        AssertTrue(JvmHostService.CreateHistorySample(plan, snapshot, observation, 120000, 240, false, true) is null);
        AssertTrue(JvmHostService.CreateHistorySample(plan, snapshot, observation, 120000, 240, true, false) is null);
    }

    private static async Task<int> ReceiveJvmHostFixture()
    {
        try
        {
            using Stream input = Console.OpenStandardInput();
            var request = await JvmHostBootstrap.ReadAsync(input);
            return request.MainClass == "fixture.Main" && request.GameArguments.Count == 2
                && request.GameArguments[0].Length == 0 && request.GameArguments[1] == "private-token" ? 0 : 8;
        }
        catch (EndOfStreamException) { return 9; }
    }

    private static async ValueTask JvmHostTransportOwnsBootstrapAndCancellation()
    {
        string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Nexa.Services.Tests.exe" : "Nexa.Services.Tests");
        MinecraftLaunchPlan plan = new("java", Path.GetTempPath(), ["-cp", "fixture", "fixture.Main", "", "private-token"],
            ["fixture"], [], new MinecraftModLoaderDescriptor(MinecraftModLoaderKind.Vanilla, null, "fixture.Main", []))
        { MainClassIndex = 2 };
        await using var service = new MinecraftProcessService(jvmHostExecutable: executable);
        var session = await service.StartAsync(plan, "host-fixture");
        AssertEqual(0, await session.WaitForExitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)));
        AssertEqual("--jvm-host", session.Process.StartInfo.ArgumentList.Single());
        AssertEqual(Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.InstanceDirectory)), session.Snapshot.InstanceDirectory);
        AssertEqual(Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.GameDirectory)), session.Snapshot.GameDirectory);

        using CancellationTokenSource cancellation = new();
        var port = new CancellingHostPort(cancellation);
        await using var cancelledService = new MinecraftProcessService(port, jvmHostExecutable: executable);
        try { await cancelledService.StartAsync(plan, "cancelled-host", cancellation.Token); throw new InvalidOperationException("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        AssertTrue(port.Child is not null);
        await port.Child!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        AssertTrue(cancelledService.ListSessions().All(static item => item.State is not (MinecraftProcessState.Created or MinecraftProcessState.Running)));
    }

    private sealed class CancellingHostPort(CancellationTokenSource cancellation) : IMinecraftProcessPort
    {
        public System.Diagnostics.Process? Child { get; private set; }
        public ValueTask<System.Diagnostics.Process> StartAsync(System.Diagnostics.ProcessStartInfo info, CancellationToken token = default)
        {
            Child = System.Diagnostics.Process.Start(info)!;
            cancellation.Cancel();
            return ValueTask.FromResult(Child);
        }
    }

    private static async ValueTask JvmBootstrapPreservesBoundaryAndRejectsMalformedFrames()
    {
        MinecraftLaunchPlan plan = new("java", "游戏目录", ["-cp", "earlier", "-Dtest=yes", "-cp", "final", "example.Main", "", "用户名", "--token", "fixture"],
            ["final"], [], new MinecraftModLoaderDescriptor(MinecraftModLoaderKind.Vanilla, null, "example.Main", []))
        { MainClassIndex = 5 };
        JvmHostEnvironment environment = new JvmHostService(new MinecraftProcessService()).Describe(plan);
        AssertEqual("example.Main", environment.MainClass!);
        AssertEqual(5, environment.JvmArguments.Count);
        using MemoryStream stream = new();
        await JvmHostBootstrap.WriteAsync(stream, plan);
        byte[] valid = stream.ToArray();
        stream.Position = 0;
        JvmHostBootstrapRequest request = await JvmHostBootstrap.ReadAsync(stream);
        AssertEqual("游戏目录", request.WorkingDirectory);
        AssertEqual("example.Main", request.MainClass);
        AssertTrue(request.JvmArguments.SequenceEqual(plan.Arguments.Take(5)));
        AssertTrue(request.GameArguments.SequenceEqual(plan.Arguments.Skip(6)));

        for (int length = 0; length < valid.Length; length++)
            await Reject(valid[..length]);
        byte[] oversized = (byte[])valid.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(oversized, int.MaxValue);
        await Reject(oversized);
        byte[] wrongVersion = (byte[])valid.Clone();
        wrongVersion[8] = 99;
        await Reject(wrongVersion);
        byte[] invalidString = (byte[])valid.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(invalidString.AsSpan(12), int.MaxValue);
        await Reject(invalidString);
        byte[] invalidUtf8 = (byte[])valid.Clone();
        invalidUtf8[16] = 0xFF;
        await Reject(invalidUtf8);
        byte[] trailing = [.. valid, 42];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(trailing, trailing.Length - 4);
        await Reject(trailing);
        await RejectPlan(plan with { MainClassIndex = null });
        await RejectPlan(plan with { Arguments = [new string('x', 1024 * 1024 + 1)], MainClassIndex = 0 });
        await RejectPlan(plan with { Arguments = ["bad\0main"], MainClassIndex = 0 });
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        try { await JvmHostBootstrap.ReadAsync(new MemoryStream(valid), cancelled.Token); throw new InvalidOperationException("Cancellation ignored."); }
        catch (OperationCanceledException) { }

        static async ValueTask Reject(byte[] bytes)
        {
            using MemoryStream input = new(bytes);
            try { await JvmHostBootstrap.ReadAsync(input); }
            catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or System.Text.DecoderFallbackException) { return; }
            throw new InvalidOperationException("Malformed bootstrap accepted.");
        }

        static async ValueTask RejectPlan(MinecraftLaunchPlan invalid)
        {
            using MemoryStream output = new();
            try { await JvmHostBootstrap.WriteAsync(output, invalid); }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            { AssertEqual(0L, output.Length); return; }
            throw new InvalidOperationException("Invalid plan accepted.");
        }
    }

    private static void JvmHostDescribesTheProcessBoundary()
    {
        MinecraftLaunchPlan plan = new("java", "root", ["-Xmx2g", "-cp", "a;b", "example.Main", "--demo"],
            ["a", "b"], [], new MinecraftModLoaderDescriptor(MinecraftModLoaderKind.Vanilla, null, "example.Main", []))
        { NativesDirectory = "native", JavaMajorVersion = 21 };
        JvmHostService host = new(new MinecraftProcessService());
        JvmHostEnvironment environment = host.Describe(plan);
        AssertEqual("example.Main", plan.Arguments[3]);
        AssertEqual(3, environment.JvmArguments.Count);
        AssertEqual("--demo", environment.GameArguments.Single());
        AssertEqual("native", environment.NativePath);
        IReadOnlyList<Nexa.Services.Capabilities.ICapability> capabilities = JvmHostService.DescribeCapabilities(plan);
        AssertTrue(capabilities.Any(static item => item.Id == "jvmhost.process.spawn"));
        AssertTrue(capabilities.Any(static item => item.Id == "jvmhost.environment.game_args"));
        AssertTrue(capabilities.Any(static item => item.Id == "jvmhost.process.cpu_sets"));
        AssertTrue(capabilities.Any(static item => item.Id == "jvmhost.metric.process_tree"));
        AssertTrue(capabilities.Any(static item => item.Id == "jvmhost.crash.stdout_tail"));
        AssertEqual(37, capabilities.Count);
        AssertEqual(Nexa.Services.Capabilities.CapabilityAvailability.DependencyMissing,
            capabilities.Single(static item => item.Id == "jvmhost.metric.gpu").Availability);
        AssertEqual(Nexa.Services.Capabilities.CapabilityAvailability.DependencyMissing,
            capabilities.Single(static item => item.Id == "jvmhost.process.cpu_sets").Availability);
        AssertEqual(Nexa.Services.Capabilities.CapabilityAvailability.DependencyMissing,
            capabilities.Single(static item => item.Id == "jvmhost.metric.commit").Availability);

        JvmHostObservation unavailableMetrics = new(Guid.NewGuid(), "fixture", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, 1, 1024, 512, 1, 1, 0, 0, 0, 0, 0, 0, 0, null, null, 0, [], []);
        IReadOnlyList<Nexa.Services.Capabilities.ICapability> observations =
            ObservationCapabilityCatalog.Project(unavailableMetrics, DateTimeOffset.UtcNow);
        AssertEqual(Nexa.Services.Capabilities.CapabilityAvailability.DependencyMissing,
            observations.Single(static item => item.Id == "observation.launch.heap_peak").Availability);
        AssertEqual(Nexa.Services.Capabilities.CapabilityAvailability.DependencyMissing,
            observations.Single(static item => item.Id == "observation.runtime.gpu_p95").Availability);
        AssertEqual(Nexa.Services.Capabilities.CapabilityAvailability.DependencyMissing,
            observations.Single(static item => item.Id == "observation.launch.commit_peak").Availability);

        JvmHostObservation sampledMetrics = unavailableMetrics with
        {
            CpuPeakPercent = 84,
            RuntimePhysicalP95Bytes = 900,
            RuntimeCommitP95Bytes = 700,
            RuntimeCpuP95Percent = 62,
        };
        IReadOnlyList<Nexa.Services.Capabilities.ICapability> sampled =
            ObservationCapabilityCatalog.Project(sampledMetrics, DateTimeOffset.UtcNow);
        AssertEqual(900L, ((Nexa.Services.Capabilities.Capability<long>)sampled
            .Single(static item => item.Id == "observation.runtime.physical_p95")).Value);
        AssertEqual(62L, ((Nexa.Services.Capabilities.Capability<long>)sampled
            .Single(static item => item.Id == "observation.runtime.cpu_p95")).Value);
    }
}
