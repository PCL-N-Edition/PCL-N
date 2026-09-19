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
public sealed class MinecraftEnvironmentCapabilityProvider(
    IJavaRuntimeLocator? javaLocator = null,
    Func<IReadOnlyList<MinecraftExpectedFile>>? expectedFiles = null) : IMachineCapabilityProvider
{
    private readonly IJavaRuntimeLocator _javaLocator = javaLocator ?? new LocalJavaRuntimeLocator();
    private readonly Func<IReadOnlyList<MinecraftExpectedFile>> _expectedFiles = expectedFiles ?? (() => []);

    public string Id => MachineInstanceCatalog.MinecraftProviderId;

    public async ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<ICapability> facts = [.. await MachineInstanceCatalog.CollectJavaAsync(
            _javaLocator, timestamp, cancellationToken).ConfigureAwait(false)];
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

        return Array.AsReadOnly<ICapability>([.. facts]);
    }
}
