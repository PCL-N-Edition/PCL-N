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
    private readonly IJavaRuntimeLocator _javaLocator = javaLocator ?? new LocalJavaRuntimeLocator();
    private readonly string? _minecraftRootDirectory = minecraftRootDirectory;

    public string Id => MachineInstanceCatalog.JavaProviderId;

    public async ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<ICapability> facts = [.. await MachineInstanceCatalog.CollectJavaAsync(
            _javaLocator, timestamp, cancellationToken).ConfigureAwait(false)];
        facts.AddRange(await JavaCompatibilityProjection.CollectAsync(
            _javaLocator, _minecraftRootDirectory, timestamp, cancellationToken).ConfigureAwait(false));
        return Array.AsReadOnly<ICapability>([.. facts]);
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

        facts.AddRange(await CollectSettingsAsync(timestamp, cancellationToken).ConfigureAwait(false));
        return Array.AsReadOnly<ICapability>([.. facts]);
    }

    private async ValueTask<IReadOnlyList<ICapability>> CollectSettingsAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        const string source = "options.txt";
        if (string.IsNullOrWhiteSpace(_minecraftRootDirectory)
            || await MinecraftPrimaryInstanceScope.ResolveAsync(_minecraftRootDirectory, cancellationToken).ConfigureAwait(false) is not { } primary
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
            MachineInstanceCatalog.MinecraftSettingsResourcePacks.Observe(options.ResourcePacks, timestamp, source),
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
