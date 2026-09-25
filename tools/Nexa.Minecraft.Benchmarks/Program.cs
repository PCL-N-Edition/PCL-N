using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Nexa.Services.Downloads;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Tasks;
using Nexa.Xsr.State;

namespace Nexa.Minecraft.Benchmarks;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine("Benchmark stage: process entry.");
        if (args is ["--help"] or [])
        {
            Console.WriteLine("Nexa.Minecraft.Benchmarks <archive> <sha256> <java> <Jvm.Host> <new-output-directory> <heap-MiB> <seconds:60..1800>");
            Console.WriteLine("Runs a pinned client pack in isolation. No world phase is confirmed; artifacts are NOT eligible for training.");
            return 0;
        }
        try
        {
            if (args.Length != 7 || args[1].Length != 64 || !args[1].All(char.IsAsciiHexDigit)
                || !int.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out int heap) || heap is < 512 or > 12288
                || !int.TryParse(args[6], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds is < 60 or > 1800)
                throw new ArgumentException("Invalid benchmark arguments. Use --help.");
            string archive = Path.GetFullPath(args[0]), java = Path.GetFullPath(args[2]), host = Path.GetFullPath(args[3]);
            string output = Path.GetFullPath(args[4]);
            if (!File.Exists(archive) || !File.Exists(java) || !File.Exists(host)) throw new FileNotFoundException("An input file is missing.");
            if (Path.Exists(output)) throw new IOException("Output directory must not exist. User game directories are never reused.");
            Console.WriteLine("Benchmark stage: inputs validated; registering cancellation.");
            using var stop = new CancellationTokenSource(TimeSpan.FromHours(1));
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += cancel;
            try { return await RunAsync(archive, args[1], java, host, output, heap, seconds, stop.Token).ConfigureAwait(false); }
            finally { Console.CancelKeyPress -= cancel; }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            // No credentials, complete JVM command lines, or local paths in the benchmark log.
            Console.Error.WriteLine($"Benchmark failed: {error.GetType().Name}. No training sample was published.");
            return 1;
        }
    }

    private static async Task<int> RunAsync(string archive, string hash, string java, string host,
        string output, int heap, int seconds, CancellationToken token)
    {
        Console.WriteLine("Benchmark stage: archive verification.");
        var preview = await MinecraftModpackArchive.InspectAsync(archive, token).ConfigureAwait(false);
        if (!string.Equals(preview.Sha256, hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Pack hash mismatch.");
        Console.WriteLine("Benchmark stage: Java inspection.");
        var runtime = await new LocalJavaRuntimeLocator().InspectAsync(java, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Java runtime cannot be inspected.");
        Directory.CreateDirectory(output);
        string root = Path.Combine(output, "game");
        // Import validates the complete existing root before it creates transaction staging.
        Directory.CreateDirectory(root);
        var builder = new XsrStateStoreBuilder();
        DownloadService.DeclareState(builder); TaskCenterStateContract.DeclareState(builder); MinecraftProcessStateComposition.DeclareState(builder);
        var store = builder.Build();
        var downloads = new DownloadService(store);
        using var installer = new MinecraftInstallService(new TaskCenterService(store), downloads);
        Console.WriteLine("Installing pinned pack through Nexa Services.");
        var installed = await installer.InstallModpackAsync(new(preview, root), token).ConfigureAwait(false);
        if (!installed.IsSuccess) throw new InvalidDataException("Pack installation failed.");
        var instances = await new MinecraftInstanceDiscovery().DiscoverAsync(root, token).ConfigureAwait(false);
        var instance = instances.Single(item => item.Id == installed.Value.InstanceId);
        Console.WriteLine("Benchmark stage: manifest resolution and launch file completion.");
        var manifests = await MinecraftVersionJsonReader.ResolveAsync(instance, root, token).ConfigureAwait(false);
        var platform = MinecraftLaunchPlatform.Detect();
        using var completion = new MinecraftLaunchFileCompletion(downloads);
        await completion.CompleteAsync(root, instance, manifests, platform, "official", null, token).ConfigureAwait(false);
        var plan = MinecraftLaunchPlanner.CreatePlan(new MinecraftLaunchRequest
        {
            VersionJson = manifests.Current,
            InheritedVersionJsons = manifests.Inherited,
            VersionId = instance.VersionId,
            InstanceDirectory = instance.DirectoryPath,
            MinecraftRootDirectory = root,
            PlayerName = "NexaBenchmark",
            PlayerUuid = "ef217bd253803c74bd610793496efc08",
            IdentityMode = MinecraftLaunchIdentityMode.Offline,
            JavaExecutablePath = java,
            JavaMajorVersion = runtime.Installation.MajorVersion,
            MemoryMegabytes = heap,
            IsolatedGameDirectory = true,
            OperatingSystem = platform.OperatingSystem,
            OperatingSystemVersion = platform.OperatingSystemVersion,
            Is64BitArchitecture = platform.Is64BitArchitecture,
            IsArm64Architecture = platform.IsArm64Architecture
        });
        await using var processes = new MinecraftProcessService(hostStore: store, jvmHostExecutable: host);
        var executor = new MinecraftLaunchExecutor(new JvmHostService(processes));
        await using var samples = new StreamWriter(new FileStream(Path.Combine(output, "samples.csv"), FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        await samples.WriteLineAsync("sequence,elapsed_ms,window_ms,epoch,samples,working_mean_mib,working_peak_mib,private_mean_mib,cpu_mean_percent,threads_peak,ended,exit_code").ConfigureAwait(false);
        Console.WriteLine("Starting Minecraft through JNI host; game phase is unverified.");
        var session = await executor.ExecuteAsync(plan, instance.Id, cancellationToken: token).ConfigureAwait(false);
        var sampleId = store.Resolve(JvmHostStateContract.SamplesKey);
        long last = -1, count = 0;
        bool contiguous = true, terminal = false, forced = false;
        async Task DrainAsync()
        {
            foreach (var sample in store.ReadCollection<JvmRunSample>(sampleId, CancellationToken.None).Items
                .Where(item => item.SessionId == session.Snapshot.SessionId && item.Sequence > last).OrderBy(item => item.Sequence))
            {
                contiguous &= sample.Sequence == last + 1;
                last = sample.Sequence; count++; terminal |= sample.Ended;
                await samples.WriteLineAsync(FormattableString.Invariant($"{sample.Sequence},{sample.ElapsedMilliseconds},{sample.WindowMilliseconds},{sample.ConfigurationEpoch},{sample.SampleCount},{sample.WorkingMeanMiB:R},{sample.WorkingPeakMiB:R},{sample.PrivateMeanMiB:R},{sample.CpuMeanPercent:R},{sample.ThreadsPeak},{(sample.Ended ? 1 : 0)},{sample.ExitCode}")).ConfigureAwait(false);
            }
            await samples.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        var elapsed = Stopwatch.StartNew();
        try
        {
            while (session.Snapshot.State is MinecraftProcessState.Created or MinecraftProcessState.Running)
            {
                await DrainAsync().ConfigureAwait(false);
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(seconds)) { forced = true; session.Cancel(); break; }
                await Task.Delay(250, token).ConfigureAwait(false);
            }
        }
        finally
        {
            if (session.Snapshot.State is MinecraftProcessState.Created or MinecraftProcessState.Running) { forced = true; session.Cancel(); }
            await session.WaitForExitAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false);
            // Final sampling follows process completion; wait for its explicit terminal record.
            var drainTime = Stopwatch.StartNew();
            do { await DrainAsync().ConfigureAwait(false); if (terminal) break; await Task.Delay(100, CancellationToken.None).ConfigureAwait(false); }
            while (drainTime.Elapsed < TimeSpan.FromSeconds(5));
            await using var artifact = new FileStream(Path.Combine(output, "run.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var json = new Utf8JsonWriter(artifact, new JsonWriterOptions { Indented = true });
            json.WriteStartObject(); json.WriteNumber("schema", 1); json.WriteString("source", "controlled-benchmark");
            json.WriteString("packSha256", preview.Sha256); json.WriteString("minecraft", preview.Game);
            json.WriteString("loader", plan.ModLoader.Kind.ToString()); json.WriteNumber("javaMajor", plan.JavaMajorVersion);
            json.WriteNumber("heapMiB", plan.HeapLimitMiB); json.WriteNumber("classpathCount", plan.ClasspathEntries.Count);
            json.WriteString("os", platform.OperatingSystem.ToString()); json.WriteString("architecture", RuntimeInformation.OSArchitecture.ToString());
            json.WriteNumber("logicalProcessors", Environment.ProcessorCount); json.WriteNumber("sampleWindows", count);
            json.WriteBoolean("contiguous", contiguous); json.WriteBoolean("terminalSample", terminal); json.WriteBoolean("forcedStop", forced);
            json.WriteNumber("exitCode", session.Snapshot.ExitCode ?? -1);
            json.WriteString("scenario", "unverified-client"); json.WriteBoolean("trainingEligible", false);
            json.WriteString("exclusionReason", "World phases and hardware provenance have not been verified.");
            json.WriteEndObject(); await json.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            var context = store.ReadCollection<JvmRunContext>(store.Resolve(JvmHostStateContract.ContextsKey), CancellationToken.None)
                .Items.SingleOrDefault(item => item.SessionId == session.Snapshot.SessionId);
            await WriteContextAsync(Path.Combine(output, "context.json"), context).ConfigureAwait(false);
        }
        Console.WriteLine("Local benchmark artifacts saved; scenario is unverified and excluded from training.");
        return terminal && contiguous && count > 0 && (forced || session.Snapshot.ExitCode == 0) ? 0 : 2;
    }

    private static async Task WriteContextAsync(string path, JvmRunContext? context)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new Utf8JsonWriter(file);
        writer.WriteStartObject(); writer.WriteNumber("schema", 1); writer.WriteBoolean("available", context is not null);
        if (context is not null)
        {
            writer.WriteString("loaderVersion", context.LoaderVersion);
            writer.WriteBoolean("complete", context.Inventory.Complete); writer.WriteNumber("unknownFiles", context.Inventory.UnknownFiles);
            writer.WriteStartObject("components");
            foreach (var pair in context.Components.OrderBy(item => item.Key, StringComparer.Ordinal)) writer.WriteString(pair.Key, pair.Value);
            writer.WriteEndObject(); writer.WriteStartArray("mods");
            foreach (var mod in context.Inventory.Mods.OrderBy(item => item.Id, StringComparer.Ordinal).ThenBy(item => item.Version, StringComparer.Ordinal))
            {
                writer.WriteStartObject(); writer.WriteString("id", mod.Id); writer.WriteString("version", mod.Version);
                writer.WriteString("format", mod.Format); writer.WriteBoolean("enabled", mod.Enabled);
                writer.WriteBoolean("dependenciesComplete", mod.DependenciesComplete); writer.WriteStartObject("dependencies");
                foreach (var pair in mod.Dependencies.OrderBy(item => item.Key, StringComparer.Ordinal)) writer.WriteString(pair.Key, pair.Value);
                writer.WriteEndObject(); writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject(); await writer.FlushAsync().ConfigureAwait(false);
    }
}
