using Nexa.Services.Minecraft.Downloads;
using Nexa.Services.Minecraft.Java;

namespace Nexa.Services.Capabilities;

/// <summary>
/// One provider for the Minecraft environment namespaces: java.* from the production runtime
/// locator and minecraft.files.* from the shared verifier. Registration (the constructor)
/// never probes — the locator and file walks run inside CollectAsync on the broker's worker.
/// File integrity facts scope to the active root's known plan files; providers without a
/// resolved plan report the facts as unavailable instead of inventing a count.
/// </summary>
/// <summary>java.* facts from the production locator (ownership: nexa.java).</summary>
public sealed class JavaEnvironmentCapabilityProvider(
    IJavaRuntimeLocator? javaLocator = null,
    string? minecraftRootDirectory = null) : IMachineCapabilityProvider
{
    private readonly object _cacheGate = new();
    private readonly IJavaRuntimeLocator _javaLocator = javaLocator ?? new LocalJavaRuntimeLocator();
    private readonly string? _minecraftRootDirectory = minecraftRootDirectory;
    private (DateTimeOffset Timestamp, IReadOnlyList<JavaRuntimeCandidate> Runtimes)? _runtimeCache;

    public string Id => MachineInstanceCatalog.JavaProviderId;

    public async ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
        => await CollectAsync(timestamp, new MachineCapabilityQuery(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, MachineCapabilityQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<JavaRuntimeCandidate> runtimes;
        if (query.HasInstanceScope && query.JavaExecutablePath is { } selectedPath)
        {
            if (OperatingSystem.IsWindows() && Path.GetFileName(selectedPath).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
            {
                string console = Path.Combine(Path.GetDirectoryName(selectedPath)!, "java.exe");
                if (File.Exists(console)) selectedPath = console;
            }
            var selected = await _javaLocator.InspectAsync(selectedPath, cancellationToken).ConfigureAwait(false);
            if (selected is null)
                return [MachineInstanceCatalog.JavaInstalled.Unavailable(CapabilityAvailability.TemporarilyUnavailable, timestamp, "本次 Java 的版本探测未完成"),
                    MachineInstanceCatalog.JavaCompatibilityHard.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "本次 Java 的版本信息不可用")];
            runtimes = [selected];
        }
        else runtimes = await FindRuntimesAsync(timestamp, cancellationToken).ConfigureAwait(false);
        List<ICapability> facts = [.. MachineInstanceCatalog.CollectJava(runtimes, timestamp)];
        facts.AddRange(await JavaCompatibilityProjection.CollectAsync(
            runtimes, _minecraftRootDirectory, query, timestamp, cancellationToken).ConfigureAwait(false));
        return Array.AsReadOnly<ICapability>([.. facts]);
    }

    private async ValueTask<IReadOnlyList<JavaRuntimeCandidate>> FindRuntimesAsync(
        DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        lock (_cacheGate)
        {
            if (_runtimeCache is { } cached && timestamp - cached.Timestamp < TimeSpan.FromSeconds(30))
            {
                return cached.Runtimes;
            }
        }

        IReadOnlyList<JavaRuntimeCandidate> runtimes = await _javaLocator.FindAllAsync(cancellationToken)
            .ConfigureAwait(false);
        lock (_cacheGate)
        {
            _runtimeCache = (timestamp, runtimes);
        }

        return runtimes;
    }
}

/// <summary>minecraft.files.* facts from the shared verifier (ownership: nexa.minecraft).</summary>
/// <remarks>
/// Without an explicit plan the default scope is every discovered instance's client jar —
/// the one file each version cannot launch without. A missing jar is the exact corruption
/// that used to surface as a JVM dying before its window appeared.
/// </remarks>
public sealed class MinecraftEnvironmentCapabilityProvider(
    string? minecraftRootDirectory = null,
    Func<IReadOnlyList<MinecraftExpectedFile>>? expectedFiles = null) : IMachineCapabilityProvider
{
    private readonly string? _minecraftRootDirectory = minecraftRootDirectory;

    private readonly Func<IReadOnlyList<MinecraftExpectedFile>> _expectedFiles = expectedFiles ?? CreateDefaultPlan(minecraftRootDirectory);

    public string Id => MachineInstanceCatalog.MinecraftProviderId;

    public async ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
        => await CollectAsync(timestamp, new MachineCapabilityQuery(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, MachineCapabilityQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<ICapability> facts = [];
        IReadOnlyList<MinecraftExpectedFile> expected = _expectedFiles();
        if (expected.Count > 0)
        {
            facts.AddRange(await MachineInstanceCatalog.CollectMinecraftFilesAsync(
                expected, timestamp, cancellationToken).ConfigureAwait(false));
        }
        else
        {
            facts.Add(MachineInstanceCatalog.MinecraftFilesRequired.Unavailable(
                CapabilityAvailability.TemporarilyUnavailable, timestamp, "尚未解析实例文件计划"));
            facts.Add(MachineInstanceCatalog.MinecraftFilesMissing.Unavailable(
                CapabilityAvailability.TemporarilyUnavailable, timestamp, "尚未解析实例文件计划"));
        }

        facts.AddRange(await CollectSettingsAsync(timestamp, query, cancellationToken).ConfigureAwait(false));
        return Array.AsReadOnly<ICapability>([.. facts]);
    }

    private async ValueTask<IReadOnlyList<ICapability>> CollectSettingsAsync(DateTimeOffset timestamp,
        MachineCapabilityQuery query, CancellationToken cancellationToken)
    {
        const string source = "options.txt";
        if (await MinecraftPrimaryInstanceScope.ResolveAsync(_minecraftRootDirectory, query, cancellationToken)
            .ConfigureAwait(false) is not { } primary
            || primary.Options is not { Readable: true } options)
        {
            return
            [
                MachineInstanceCatalog.MinecraftSettingsReadable.Observe(false, timestamp, source),
                MachineInstanceCatalog.MinecraftSettingsRenderDistance.Unavailable(
                    CapabilityAvailability.DependencyMissing, timestamp, "options.txt 不存在或不可读"),
                MachineInstanceCatalog.MinecraftSettingsSimulationDistance.Unavailable(
                    CapabilityAvailability.DependencyMissing, timestamp, "options.txt 不存在或不可读"),
                MachineInstanceCatalog.MinecraftSettingsMipmapLevels.Unavailable(
                    CapabilityAvailability.DependencyMissing, timestamp, "options.txt 不存在或不可读"),
                MachineInstanceCatalog.MinecraftSettingsGraphicsMode.Unavailable(
                    CapabilityAvailability.DependencyMissing, timestamp, "options.txt 不存在或不可读"),
                MachineInstanceCatalog.MinecraftSettingsFullscreen.Unavailable(
                    CapabilityAvailability.DependencyMissing, timestamp, "options.txt 不存在或不可读"),
                MachineInstanceCatalog.MinecraftSettingsResourcePacks.Unavailable(
                    CapabilityAvailability.DependencyMissing, timestamp, "options.txt 不存在或不可读"),
            ];
        }

        return
        [
            MachineInstanceCatalog.MinecraftSettingsReadable.Observe(true, timestamp, source),
            MachineInstanceCatalog.MinecraftSettingsRenderDistance.Observe(options.RenderDistance, timestamp, source),
            MachineInstanceCatalog.MinecraftSettingsSimulationDistance.Observe(options.SimulationDistance, timestamp, source),
            MachineInstanceCatalog.MinecraftSettingsMipmapLevels.Observe(options.MipmapLevels, timestamp, source),
            MachineInstanceCatalog.MinecraftSettingsGraphicsMode.Observe(options.GraphicsMode, timestamp, source),
            MachineInstanceCatalog.MinecraftSettingsFullscreen.Observe(options.Fullscreen, timestamp, source),
            MachineInstanceCatalog.MinecraftSettingsResourcePacks.Observe(
                LoaderVersionProjections.DescribeResourcePacks(options.ResourcePacks), timestamp, source),
        ];
    }

    internal static Func<IReadOnlyList<MinecraftExpectedFile>> CreateDefaultPlan(string? minecraftRootDirectory)
    {
        if (string.IsNullOrWhiteSpace(minecraftRootDirectory))
        {
            return () => [];
        }

        string root = minecraftRootDirectory;
        return () =>
        {
            List<MinecraftExpectedFile> plan = [];
            string versionsRoot = Path.Combine(root, "versions");
            if (!Directory.Exists(versionsRoot))
            {
                return plan;
            }

            foreach (string directory in Directory.EnumerateDirectories(versionsRoot))
            {
                string id = Path.GetFileName(directory);
                string jar = Path.Combine(directory, id + ".jar");
                if (File.Exists(jar))
                {
                    plan.Add(new MinecraftExpectedFile(jar, null, null));
                }
            }

            return plan;
        };
    }
}
