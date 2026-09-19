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
public sealed class JavaEnvironmentCapabilityProvider(IJavaRuntimeLocator? javaLocator = null) : IMachineCapabilityProvider
{
    private readonly IJavaRuntimeLocator _javaLocator = javaLocator ?? new LocalJavaRuntimeLocator();

    public string Id => MachineInstanceCatalog.JavaProviderId;

    public ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken) =>
        MachineInstanceCatalog.CollectJavaAsync(_javaLocator, timestamp, cancellationToken);
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
    private readonly Func<IReadOnlyList<MinecraftExpectedFile>> _expectedFiles = expectedFiles ?? CreateDefaultPlan(minecraftRootDirectory);

    public string Id => MachineInstanceCatalog.MinecraftProviderId;

    public async ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<MinecraftExpectedFile> expected = _expectedFiles();
        if (expected.Count > 0)
        {
            return await MachineInstanceCatalog.CollectMinecraftFilesAsync(
                expected, timestamp, cancellationToken).ConfigureAwait(false);
        }

        return Array.AsReadOnly(new ICapability[]
        {
            MachineInstanceCatalog.MinecraftFilesRequired.Unavailable(
                CapabilityAvailability.TemporarilyUnavailable, timestamp, "尚未解析实例文件计划"),
            MachineInstanceCatalog.MinecraftFilesMissing.Unavailable(
                CapabilityAvailability.TemporarilyUnavailable, timestamp, "尚未解析实例文件计划"),
        });
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
