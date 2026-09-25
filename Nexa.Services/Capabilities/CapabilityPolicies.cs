using System.Collections.Frozen;

namespace Nexa.Services.Capabilities;

public static class LaunchPolicyCatalog
{
    private const string Provider = "nexa.policy";
    public static readonly CapabilityDefinition<long> MinecraftMemory = new(
        "policy.minecraft.memory", "Minecraft 内存策略", "策略", Provider, CapabilityKind.Policy,
        CapabilityStability.Dynamic, unit: "mib");
    private static readonly string[] StringIds =
    [
        "policy.minecraft.java", "policy.minecraft.cpu", "policy.minecraft.gpu",
        "policy.minecraft.priority", "policy.minecraft.display", "policy.minecraft.large_pages",
        "policy.minecraft.prewarm", "policy.minecraft.resource_pack", "policy.minecraft.shader",
        "policy.minecraft.render_distance", "policy.nexa.memory", "policy.nexa.cpu",
        "policy.nexa.background", "policy.nexa.cloud", "policy.nexa.indexing", "policy.nexa.sidecar",
        "policy.storage.scan", "policy.storage.clone", "policy.storage.snapshot", "policy.storage.cleanup",
        "policy.network.download", "policy.network.sync", "policy.ai.accelerator",
    ];
    internal static readonly FrozenDictionary<string, CapabilityDefinition<string>> StringDefinitions = StringIds
        .ToFrozenDictionary(static id => id, static id => new CapabilityDefinition<string>(id, id, "策略", Provider,
            CapabilityKind.Policy, CapabilityStability.Dynamic), StringComparer.Ordinal);

    public static IReadOnlyList<ICapabilityDefinition> Definitions() => [MinecraftMemory, .. StringDefinitions.Values];
}

public sealed class LaunchPolicyProjection : ICapabilityProjection
{
    public IReadOnlyList<ICapability> Project(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp)
        => Project(values, timestamp, new MachineCapabilityQuery());

    public IReadOnlyList<ICapability> Project(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp,
        MachineCapabilityQuery query)
    {
        const string source = "Nexa capability policy resolver";
        long recommended = ReadLong(values, "estimate.heap.recommended");
        long safe = ReadLong(values, "estimate.heap.safe_maximum");
        long memory = Math.Max(512, safe > 0 ? Math.Min(recommended, safe) : recommended);
        List<ICapability> result = [query.PlannedHeapMiB is { } planned
            ? planned > 0 ? LaunchPolicyCatalog.MinecraftMemory.Observe(planned, timestamp, "本次启动的堆内存配置")
                : LaunchPolicyCatalog.MinecraftMemory.Unavailable(CapabilityAvailability.Unknown, timestamp, "自定义参数覆盖了堆内存配置")
            : LaunchPolicyCatalog.MinecraftMemory.Observe(memory, timestamp, source)];
        foreach ((string id, CapabilityDefinition<string> definition) in LaunchPolicyCatalog.StringDefinitions)
        {
            string value = id switch
            {
                "policy.minecraft.java" => ReadString(values, "java.runtime.path") ?? "Auto",
                "policy.minecraft.gpu" => ReadBool(values, "machine.gpu.high_performance_adapter_available") ? "HighPerformance" : "Auto",
                "policy.minecraft.priority" => ReadBool(values, "power.profile.performance") ? "AboveNormal" : "Normal",
                "policy.minecraft.prewarm" => ReadBool(values, "thermal.derived.pause_background") ? "Disabled" : "Auto",
                "policy.storage.clone" => ReadBool(values, "filesystem.clone.native") ? "Native" : "Copy",
                _ => "Auto",
            };
            result.Add(definition.Observe(value, timestamp, source, CapabilityConfidence.Medium));
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static long ReadLong(IReadOnlyDictionary<string, ICapability> values, string id) =>
        values.TryGetValue(id, out ICapability? item) && item is Capability<long> { Availability: CapabilityAvailability.Available } value ? value.Value : 0;
    private static string? ReadString(IReadOnlyDictionary<string, ICapability> values, string id) =>
        values.TryGetValue(id, out ICapability? item) && item is Capability<string> { Availability: CapabilityAvailability.Available } value ? value.Value : null;
    private static bool ReadBool(IReadOnlyDictionary<string, ICapability> values, string id) =>
        values.TryGetValue(id, out ICapability? item) && item is Capability<bool> { Availability: CapabilityAvailability.Available, Value: true };
}
