using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Process;

namespace Nexa.Services.Capabilities;

/// <summary>mod.* capability definitions (§22 subset: enumeration facts the estimator eats).</summary>
public static class ModCatalog
{
    public const string ProviderId = "nexa.mod";

    public static readonly CapabilityDefinition<int> ModCount = new(
        "mod.count", "模组总数", "模组", ProviderId, CapabilityKind.Metric, CapabilityStability.Dynamic);
    public static readonly CapabilityDefinition<int> ModEnabled = new(
        "mod.enabled", "启用模组数", "模组", ProviderId, CapabilityKind.Metric, CapabilityStability.Dynamic);
    public static readonly CapabilityDefinition<int> ModDisabled = new(
        "mod.disabled", "禁用模组数", "模组", ProviderId, CapabilityKind.Metric, CapabilityStability.Dynamic);
    public static readonly CapabilityDefinition<long> ModJsonSize = new(
        "mod.json.size", "模组文件总量", "模组", ProviderId, CapabilityKind.Metric, CapabilityStability.Dynamic, unit: "bytes");
    public static readonly CapabilityDefinition<bool> ModRuntimeProfileKnown = new(
        "mod.runtime_profile.known", "模组画像已知", "模组", ProviderId, CapabilityKind.Fact, CapabilityStability.Dynamic);
    public static readonly CapabilityDefinition<string> ModMetadataFingerprint = new(
        "mod.metadata.fingerprint", "模组元数据指纹", "模组", ProviderId, CapabilityKind.Fact, CapabilityStability.Dynamic);

    public static IReadOnlyList<ICapabilityDefinition> Definitions() => (ICapabilityDefinition[])
    [
        ModCount, ModEnabled, ModDisabled, ModJsonSize, ModRuntimeProfileKnown, ModMetadataFingerprint,
    ];
}

/// <summary>
/// Scans the primary instance's mods directory: jar count (the estimator's mod_count input —
/// it read a fact nobody produced, which is why estimates sat at Low confidence forever),
/// enabled/disabled split (.disabled / .jar.disabled suffix), total bytes and a bounded metadata
/// fingerprint. Metadata identity is not a learned runtime resource profile.
/// </summary>
public sealed class ModCapabilityProvider(string? minecraftRootDirectory) : IMachineCapabilityProvider
{
    public string Id => ModCatalog.ProviderId;

    public ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
        => CollectAsync(timestamp, new MachineCapabilityQuery(), cancellationToken);

    public async ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, MachineCapabilityQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (await MinecraftPrimaryInstanceScope.ResolveAsync(minecraftRootDirectory, query, cancellationToken).ConfigureAwait(false) is not { } primary)
        {
            return Unavailable(timestamp, "尚未发现 Minecraft 实例");
        }

        const string source = "mods 目录扫描";
        string mods = Path.Combine(primary.GameDirectory, "mods");
        var inventory = await LaunchModInventoryReader.ReadAsync(primary.GameDirectory, cancellationToken).ConfigureAwait(false);
        string? fingerprint = ModInventoryFingerprint.Create(inventory);
        ICapability fingerprintFact = fingerprint is null
            ? ModCatalog.ModMetadataFingerprint.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "模组元数据清单不完整")
            : ModCatalog.ModMetadataFingerprint.Observe(fingerprint, timestamp, source);
        if (!Directory.Exists(mods))
        {
            return Array.AsReadOnly(new ICapability[]
            {
                ModCatalog.ModCount.Observe(0, timestamp, source + "（目录不存在）"),
                ModCatalog.ModEnabled.Observe(0, timestamp, source + "（目录不存在）"),
                ModCatalog.ModDisabled.Observe(0, timestamp, source + "（目录不存在）"),
                ModCatalog.ModJsonSize.Observe(0, timestamp, source + "（目录不存在）"),
                ModCatalog.ModRuntimeProfileKnown.Observe(false, timestamp, source),
                fingerprintFact,
            });
        }

        int enabled = 0, disabled = 0;
        long bytes = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(mods))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileName(file);
                bool isJar = name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
                bool isDisabledJar = name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);
                if (!isJar && !isDisabledJar)
                {
                    continue;
                }

                bytes += new FileInfo(file).Length;
                if (isDisabledJar)
                {
                    disabled++;
                }
                else
                {
                    enabled++;
                }
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return Unavailable(timestamp, "mods 目录不可读");
        }

        return Array.AsReadOnly(new ICapability[]
        {
            ModCatalog.ModCount.Observe(enabled + disabled, timestamp, source),
            ModCatalog.ModEnabled.Observe(enabled, timestamp, source),
            ModCatalog.ModDisabled.Observe(disabled, timestamp, source),
            ModCatalog.ModJsonSize.Observe(bytes, timestamp, source),
            // Parsed identities do not establish learned resource costs.
            ModCatalog.ModRuntimeProfileKnown.Observe(false, timestamp, source + "（模组运行资源画像尚不可用）"),
            fingerprintFact,
        });
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<ICapability> Unavailable(DateTimeOffset timestamp, string reason) => Array.AsReadOnly(
        ModCatalog.Definitions().Select(definition => definition.Unavailable(
            CapabilityAvailability.DependencyMissing, timestamp, reason)).ToArray());
}
