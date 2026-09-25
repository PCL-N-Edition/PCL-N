using System.Collections.Frozen;

namespace Nexa.Services.Capabilities;

public enum ResourceEstimateStatus { NotStarted, Pending, Completed, Failed }

public sealed record ResourceEstimatorProfile(
    string Version,
    long HeapLaunchMiB,
    long HeapRuntimeMiB,
    long NativeLaunchMiB,
    long NativeRuntimeMiB,
    long SafetyMarginMiB)
{
    public IReadOnlyDictionary<string, long> MinecraftBaselineMiB { get; init; } =
        new Dictionary<string, long>(StringComparer.Ordinal)
        { ["default"] = 1024, ["legacy"] = 768, ["modern"] = 1536 };
    public IReadOnlyDictionary<string, long> LoaderBaselineMiB { get; init; } =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        { ["Vanilla"] = 0, ["Fabric"] = 192, ["Quilt"] = 224, ["Forge"] = 384, ["NeoForge"] = 384 };
    public IReadOnlyDictionary<string, double> Coefficients { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["mod_count_mib"] = 5.5,
            ["class_count_mib"] = 0.012,
            ["resource_weight"] = 1.0,
            ["resource_peak_launch"] = 0.5,
            ["metaspace_class_mib"] = 0.0125,
            ["gc_heap_ratio"] = 0.0625,
            ["graphics_model_ratio"] = 0.5,
            ["heap_estimated_minimum_ratio"] = 0.75,
            ["heap_useful_maximum_ratio"] = 1.5,
        };
    public IReadOnlyDictionary<string, long> SafetyMarginsMiB { get; init; } =
        new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["heap"] = 256,
            ["native"] = 192,
            ["graphics"] = 256,
            ["system"] = 1024,
            ["commit"] = 512,
            ["native_launch"] = 128,
            ["renderer"] = 256,
        };
    public double QuantileTarget { get; init; } = 0.95;
    public IReadOnlyDictionary<string, double> HardwareCorrections { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal) { ["uma"] = 1.15, ["emulated"] = 1.10 };

    public static ResourceEstimatorProfile Default { get; } = new("1.1.0", 2048, 2048, 512, 768, 512);
}

/// <summary>Immutable §56 provenance. All values remain MiB until presentation.</summary>
public sealed record EstimateResult(
    long ValueMiB,
    CapabilityConfidence Confidence,
    string ModelVersion,
    string ProfileVersion,
    IReadOnlyList<string> Inputs,
    double HistoricalWeight,
    long SafetyMarginMiB,
    IReadOnlyList<string> Reasons);

public sealed record ResourceEstimateSnapshot(
    ResourceEstimateStatus Status,
    CapabilityConfidence Confidence,
    EstimateResult HeapLaunch,
    EstimateResult HeapRuntime,
    EstimateResult NativeLaunch,
    EstimateResult NativeRuntime,
    EstimateResult PhysicalLaunch,
    EstimateResult PhysicalRuntime,
    EstimateResult CommitLaunch,
    EstimateResult CommitRuntime);

public static class ResourceEstimateCatalog
{
    public const string ProviderId = "nexa.estimator";
    public static readonly CapabilityDefinition<string> Status = Estimate<string>("estimate.status", "估算状态");
    public static readonly CapabilityDefinition<string> Confidence = Estimate<string>("estimate.confidence", "估算置信度");
    public static readonly CapabilityDefinition<double> ConfidenceScore = Estimate<double>("estimate.confidence.score", "估算置信分");
    public static readonly CapabilityDefinition<long> HeapLaunch = Estimate<long>("estimate.heap.launch", "启动堆内存", "mib");
    public static readonly CapabilityDefinition<long> HeapRuntime = Estimate<long>("estimate.heap.runtime", "运行堆内存", "mib");
    public static readonly CapabilityDefinition<long> NativeLaunch = Estimate<long>("estimate.native.launch", "启动本机内存", "mib");
    public static readonly CapabilityDefinition<long> NativeRuntime = Estimate<long>("estimate.native.runtime", "运行本机内存", "mib");
    public static readonly CapabilityDefinition<long> PhysicalLaunch = Estimate<long>("estimate.physical.launch", "启动物理内存", "mib");
    public static readonly CapabilityDefinition<long> PhysicalRuntime = Estimate<long>("estimate.physical.runtime", "运行物理内存", "mib");
    public static readonly CapabilityDefinition<long> CommitLaunch = Estimate<long>("estimate.commit.launch", "启动提交量", "mib");
    public static readonly CapabilityDefinition<long> CommitRuntime = Estimate<long>("estimate.commit.runtime", "运行提交量", "mib");

    private static readonly string[] MiBIds =
    [
        "estimate.heap.base", "estimate.heap.mods", "estimate.heap.resources",
        "estimate.heap.hard_minimum", "estimate.heap.estimated_minimum", "estimate.heap.recommended",
        "estimate.heap.useful_maximum", "estimate.heap.safe_maximum", "estimate.native.metaspace",
        "estimate.native.code_cache", "estimate.native.thread", "estimate.native.direct", "estimate.native.gc",
        "estimate.native.mods", "estimate.resource.heap", "estimate.resource.load_peak", "estimate.resource.runtime",
        "estimate.resource.sound", "estimate.resource.models", "estimate.graphics.texture", "estimate.graphics.models",
        "estimate.graphics.shader", "estimate.graphics.renderer", "estimate.graphics.margin", "estimate.graphics.total",
        "estimate.graphics.shared_system", "estimate.graphics.vram_spill", "estimate.physical.system_reserve",
        "estimate.physical.launch_margin", "estimate.physical.runtime_margin", "estimate.commit.heap.launch",
        "estimate.commit.heap.runtime", "estimate.commit.nonheap.launch", "estimate.commit.nonheap.runtime",
        "estimate.commit.reserve", "estimate.commit.launch_margin", "estimate.commit.runtime_margin",
        "estimate.history.heap.p95", "estimate.history.native.p95",
        "estimate.history.physical.p95", "estimate.history.gpu.p95",
        "estimate.calibrated.heap", "estimate.calibrated.native", "estimate.calibrated.graphics",
        "estimate.calibrated.physical", "estimate.calibrated.commit",
    ];
    private static readonly string[] BooleanIds =
    [
        "estimate.confidence.reason.unknown_mods", "estimate.confidence.reason.modified_core",
        "estimate.confidence.reason.unreadable_settings", "estimate.confidence.reason.missing_hardware_metric",
        "estimate.confidence.reason.insufficient_history", "estimate.history.available",
    ];
    private static readonly string[] ScoreIds =
    [
        "estimate.history.similarity.total", "estimate.history.similarity.mods",
        "estimate.history.similarity.resources", "estimate.history.similarity.loader",
        "estimate.history.similarity.java", "estimate.history.weight",
    ];
    internal static readonly FrozenDictionary<string, CapabilityDefinition<long>> LongDefinitions = MiBIds
        .ToDictionary(static id => id, static id => Estimate<long>(id, CapabilityPresentationCatalog.EstimateLabel(id), "mib"), StringComparer.Ordinal)
        .Append(new KeyValuePair<string, CapabilityDefinition<long>>(
            "estimate.heap.mod_count", Estimate<long>("estimate.heap.mod_count", "模组数量", "count")))
        .Append(new KeyValuePair<string, CapabilityDefinition<long>>(
            "estimate.history.sample_count", Estimate<long>("estimate.history.sample_count", "历史样本数", "count")))
        .Append(new KeyValuePair<string, CapabilityDefinition<long>>(
            "estimate.history.launch_time.p95", Estimate<long>("estimate.history.launch_time.p95", "历史启动时间 P95", "ms")))
        .ToFrozenDictionary(StringComparer.Ordinal);
    internal static readonly FrozenDictionary<string, CapabilityDefinition<bool>> BooleanDefinitions = BooleanIds
        .ToFrozenDictionary(static id => id, static id => Estimate<bool>(id, CapabilityPresentationCatalog.EstimateLabel(id)), StringComparer.Ordinal);
    internal static readonly FrozenDictionary<string, CapabilityDefinition<double>> ScoreDefinitions = ScoreIds
        .ToFrozenDictionary(static id => id, static id => Estimate<double>(id, CapabilityPresentationCatalog.EstimateLabel(id)), StringComparer.Ordinal);

    public static IReadOnlyList<ICapabilityDefinition> Definitions() =>
    [Status, Confidence, ConfidenceScore, HeapLaunch, HeapRuntime, NativeLaunch, NativeRuntime,
        PhysicalLaunch, PhysicalRuntime, CommitLaunch, CommitRuntime,
        .. LongDefinitions.Values, .. BooleanDefinitions.Values, .. ScoreDefinitions.Values];

    private static CapabilityDefinition<T> Estimate<T>(string id, string label, string unit = "") =>
        new(id, label, "资源估算", ProviderId, CapabilityKind.Estimate, CapabilityStability.Dynamic, unit: unit);
}

public sealed class ResourceEstimator(ResourceEstimatorProfile? profile = null, ResourceObservationHistory? history = null)
{
    private const string ModelVersion = "resource-1";
    private readonly ResourceEstimatorProfile _profile = profile ?? ResourceEstimatorProfile.Default;
    private readonly ResourceObservationHistory _history = history ?? new ResourceObservationHistory();

    public ResourceEstimateSnapshot Estimate(MachineCapabilitySnapshot snapshot) => Estimate(snapshot.Values);

    public ResourceEstimateSnapshot Estimate(MachineCapabilitySnapshot snapshot, MachineCapabilityQuery query)
        => Estimate(snapshot.Values, query.InstanceDirectory);

    internal ResourceEstimateSnapshot Estimate(IEnumerable<ICapability> values, string? instanceDirectory = null)
    {
        ResourceEstimateComputation computation = ResourceEstimateModel.Calculate(values, _profile, _history, instanceDirectory);
        string[] inputs = values.Where(static value => value.Availability == CapabilityAvailability.Available)
            .Select(static value => value.Id).Where(static id => id.StartsWith("minecraft.", StringComparison.Ordinal)
                || id.StartsWith("loader.", StringComparison.Ordinal)
                || id.StartsWith("mod.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        string[] reasons = computation.Reasons.ToArray();
        EstimateResult Result(long value, long margin = 0) => new(value, computation.Confidence, ModelVersion,
            _profile.Version, inputs, computation.HistoricalWeight, margin, reasons);
        return new(computation.Status, computation.Confidence,
            Result(computation.LongValues[ResourceEstimateCatalog.HeapLaunch.Id]), Result(computation.LongValues[ResourceEstimateCatalog.HeapRuntime.Id]),
            Result(computation.LongValues[ResourceEstimateCatalog.NativeLaunch.Id]), Result(computation.LongValues[ResourceEstimateCatalog.NativeRuntime.Id]),
            Result(computation.LongValues[ResourceEstimateCatalog.PhysicalLaunch.Id], _profile.SafetyMarginMiB),
            Result(computation.LongValues[ResourceEstimateCatalog.PhysicalRuntime.Id], _profile.SafetyMarginMiB),
            Result(computation.LongValues[ResourceEstimateCatalog.CommitLaunch.Id], _profile.SafetyMarginMiB),
            Result(computation.LongValues[ResourceEstimateCatalog.CommitRuntime.Id], _profile.SafetyMarginMiB));
    }
}

public sealed class ResourceEstimatorProjection(ResourceEstimatorProfile? profile = null, ResourceObservationHistory? history = null) : ICapabilityProjection
{
    private readonly ResourceEstimatorProfile _profile = profile ?? ResourceEstimatorProfile.Default;
    private readonly ResourceObservationHistory _history = history ?? new ResourceObservationHistory();

    public IReadOnlyList<ICapability> Project(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp)
        => Project(values, timestamp, new MachineCapabilityQuery());

    public IReadOnlyList<ICapability> Project(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp,
        MachineCapabilityQuery query)
    {
        ResourceEstimateComputation estimate = ResourceEstimateModel.Calculate(values.Values, _profile, _history, query.InstanceDirectory);
        const string source = "Nexa 资源估算模型 resource-1";
        List<ICapability> projected =
        [
            ResourceEstimateCatalog.Status.Observe(estimate.Status.ToString(), timestamp, source, estimate.Confidence),
            ResourceEstimateCatalog.Confidence.Observe(estimate.Confidence.ToString(), timestamp, source, estimate.Confidence),
            ResourceEstimateCatalog.ConfidenceScore.Observe(estimate.ConfidenceScore, timestamp, source, estimate.Confidence),
            ResourceEstimateCatalog.HeapLaunch.Observe(estimate.LongValues[ResourceEstimateCatalog.HeapLaunch.Id], timestamp, source, estimate.Confidence),
            ResourceEstimateCatalog.HeapRuntime.Observe(estimate.LongValues[ResourceEstimateCatalog.HeapRuntime.Id], timestamp, source, estimate.Confidence),
            ResourceEstimateCatalog.NativeLaunch.Observe(estimate.LongValues[ResourceEstimateCatalog.NativeLaunch.Id], timestamp, source, estimate.Confidence),
            ResourceEstimateCatalog.NativeRuntime.Observe(estimate.LongValues[ResourceEstimateCatalog.NativeRuntime.Id], timestamp, source, estimate.Confidence),
            ResourceEstimateCatalog.PhysicalLaunch.Observe(estimate.LongValues[ResourceEstimateCatalog.PhysicalLaunch.Id], timestamp, source, estimate.Confidence),
            ResourceEstimateCatalog.PhysicalRuntime.Observe(estimate.LongValues[ResourceEstimateCatalog.PhysicalRuntime.Id], timestamp, source, estimate.Confidence),
            ResourceEstimateCatalog.CommitLaunch.Observe(estimate.LongValues[ResourceEstimateCatalog.CommitLaunch.Id], timestamp, source, estimate.Confidence),
            ResourceEstimateCatalog.CommitRuntime.Observe(estimate.LongValues[ResourceEstimateCatalog.CommitRuntime.Id], timestamp, source, estimate.Confidence),
        ];
        projected.AddRange(ResourceEstimateCatalog.LongDefinitions.Select(pair => pair.Value.Observe(estimate.LongValues.GetValueOrDefault(pair.Key), timestamp, source, estimate.Confidence)));
        projected.AddRange(ResourceEstimateCatalog.BooleanDefinitions.Select(pair => pair.Value.Observe(estimate.BooleanValues.GetValueOrDefault(pair.Key), timestamp, source, estimate.Confidence)));
        projected.AddRange(ResourceEstimateCatalog.ScoreDefinitions.Select(pair => pair.Value.Observe(estimate.ScoreValues.GetValueOrDefault(pair.Key), timestamp, source, estimate.Confidence)));
        return Array.AsReadOnly(projected.ToArray());
    }
}

public sealed record CapabilityPreflightReport(
    IReadOnlyList<CapabilityPreflightIssue> Issues,
    IReadOnlyList<CapabilityPreflightIssue> CollapsedIssues,
    PreflightSeverity OverallSeverity);

public sealed class CapabilityPreflightEngine
{
    public static CapabilityPreflightReport Evaluate(MachineCapabilitySnapshot snapshot)
    {
        List<CapabilityPreflightIssue> collected = [];
        if (IsTrue(snapshot, "java.derived.missing"))
            collected.Add(new("JAVA_MISSING", PreflightSeverity.Blocked, "java", PreflightCertainty.Verified, true,
                ["java.derived.missing"], remediations: ["remediation.java.download", "remediation.java.select"]));
        if (IsTrue(snapshot, "java.derived.hard_incompatible"))
            collected.Add(new("JAVA_HARD_INCOMPATIBLE", PreflightSeverity.Blocked, "java", PreflightCertainty.Verified, true,
                ["java.derived.hard_incompatible"], remediations: ["remediation.java.select", "remediation.java.download"]));

        AddBoolean(snapshot, collected, "java.derived.non_recommended", "JAVA_NON_RECOMMENDED", PreflightSeverity.Warning,
            "java", ["remediation.java.switch_recommended"]);
        AddBoolean(snapshot, collected, "java.derived.arch_incompatible", "JAVA_ARCH_INCOMPATIBLE", PreflightSeverity.Blocked,
            "java", ["remediation.java.download"], hard: true);
        AddBoolean(snapshot, collected, "java.derived.emulated", "JAVA_EMULATED", PreflightSeverity.Warning, "java", []);
        AddBoolean(snapshot, collected, "loader.derived.missing", "LOADER_MISSING", PreflightSeverity.Critical,
            "loader", ["remediation.loader.switch"]);
        AddBoolean(snapshot, collected, "loader.derived.incompatible", "LOADER_INCOMPATIBLE", PreflightSeverity.Blocked,
            "loader", ["remediation.loader.repair", "remediation.loader.switch"], hard: true);
        AddBoolean(snapshot, collected, "mod.derived.required_dependency_missing", "MOD_REQUIRED_DEPENDENCY_MISSING",
            PreflightSeverity.Blocked, "mod", ["remediation.mod.install_dependency"], hard: true);
        AddBoolean(snapshot, collected, "mod.derived.hard_conflict", "MOD_HARD_CONFLICT", PreflightSeverity.Blocked,
            "mod", ["remediation.mod.resolve_conflict"], hard: true);
        AddBoolean(snapshot, collected, "mod.derived.loader_incompatible", "MOD_LOADER_INCOMPATIBLE",
            PreflightSeverity.Blocked, "mod", ["remediation.loader.switch"], hard: true);
        AddBoolean(snapshot, collected, "mod.derived.minecraft_incompatible", "MOD_MC_VERSION_INCOMPATIBLE",
            PreflightSeverity.Blocked, "mod", ["remediation.mod.resolve_conflict"], hard: true);
        AddBoolean(snapshot, collected, "mod.derived.compatibility_warning", "MOD_COMPAT_WARNING",
            PreflightSeverity.Warning, "mod", ["remediation.mod.resolve_conflict"]);
        AddBoolean(snapshot, collected, "mod.derived.compatibility_critical", "MOD_COMPAT_CRITICAL",
            PreflightSeverity.Critical, "mod", ["remediation.mod.resolve_conflict"]);

        AddEstimateStateRules(snapshot, collected);
        AddMemoryRules(snapshot, collected);
        AddInputRules(snapshot, collected);
        AddBoolean(snapshot, collected, "gpu.derived.low_performance_selected", "GPU_LOW_PERFORMANCE_ADAPTER",
            PreflightSeverity.Warning, "gpu", ["remediation.gpu.select_high_performance"]);
        AddBoolean(snapshot, collected, "gpu.derived.vram_pressure", "GPU_VRAM_RUNTIME_LOW",
            PreflightSeverity.Critical, "gpu", ["remediation.gpu.reduce_resource_settings"]);
        long gpuBudget = BytesToMiB(ReadAvailableLong(snapshot, "gpu.memory.dedicated.available_budget"));
        long graphicsLaunch = ReadAvailableLong(snapshot, "estimate.graphics.total");
        AddEstimatedGpuLimit(collected, gpuBudget > 0 && graphicsLaunch > gpuBudget);
        AddBoolean(snapshot, collected, "thermal.derived.gpu_high", "GPU_TEMPERATURE_HIGH",
            PreflightSeverity.Warning, "gpu", []);
        AddBoolean(snapshot, collected, "thermal.derived.gpu_near_limit", "GPU_TEMPERATURE_NEAR_LIMIT",
            PreflightSeverity.Critical, "gpu", []);
        AddBoolean(snapshot, collected, "thermal.derived.cpu_high", "CPU_TEMPERATURE_HIGH",
            PreflightSeverity.Warning, "cpu", []);
        AddBoolean(snapshot, collected, "thermal.derived.cpu_near_limit", "CPU_TEMPERATURE_NEAR_LIMIT",
            PreflightSeverity.Critical, "cpu", []);
        AddFileAndEnvironmentRules(snapshot, collected);

        IReadOnlyList<CapabilityPreflightIssue> normalized = NormalizeAndDedupe(collected);
        IReadOnlyList<CapabilityPreflightIssue> collapsed = CollapseCausalGraph(normalized);
        return new(normalized, collapsed, CapabilityPreflightIssue.OverallSeverity(collapsed));
    }

    private static bool IsTrue(MachineCapabilitySnapshot snapshot, string id) =>
        snapshot.Get<bool>(id) is { Availability: CapabilityAvailability.Available, Value: true };

    private static bool IsFalse(MachineCapabilitySnapshot snapshot, string id) =>
        snapshot.Get<bool>(id) is { Availability: CapabilityAvailability.Available, Value: false };

    private static long ReadAvailableLong(MachineCapabilitySnapshot snapshot, string id) =>
        snapshot.Get<long>(id) is { Availability: CapabilityAvailability.Available } value ? value.Value : 0;

    private static void AddEstimatedGpuLimit(List<CapabilityPreflightIssue> target, bool condition)
    {
        if (!condition) return;
        target.Add(new("GPU_VRAM_LAUNCH_CRITICAL", PreflightSeverity.Critical, "gpu", PreflightCertainty.Estimated,
            false, ["estimate.graphics.total", "gpu.memory.dedicated.available_budget"],
            remediations: ["remediation.gpu.reduce_resource_settings"], canBypass: true));
    }

    private static void AddBoolean(MachineCapabilitySnapshot snapshot, List<CapabilityPreflightIssue> target,
        string evidence, string code, PreflightSeverity severity, string category, string[] remediations,
        bool hard = false, PreflightCertainty certainty = PreflightCertainty.Verified)
    {
        if (!IsTrue(snapshot, evidence)) return;
        target.Add(new(code, severity, category, certainty, hard, [evidence], remediations: remediations,
            canBypass: !hard && severity is PreflightSeverity.Warning or PreflightSeverity.Critical));
    }

    private static void AddEstimateStateRules(MachineCapabilitySnapshot snapshot, List<CapabilityPreflightIssue> target)
    {
        string? status = snapshot.Get<string>("estimate.status")?.Value;
        if (status == ResourceEstimateStatus.Pending.ToString())
            target.Add(new("MEM_ESTIMATE_PENDING", PreflightSeverity.Warning, "estimator", PreflightCertainty.Verified,
                false, ["estimate.status"], canBypass: true));
        if (status == ResourceEstimateStatus.Failed.ToString())
            target.Add(new("MEM_ESTIMATE_FAILED", PreflightSeverity.Critical, "estimator", PreflightCertainty.Verified,
                false, ["estimate.status"], canBypass: true));
        if (snapshot.Get<string>("estimate.confidence")?.Value == CapabilityConfidence.Low.ToString())
            target.Add(new("MEM_ESTIMATE_LOW_CONFIDENCE", PreflightSeverity.Warning, "estimator", PreflightCertainty.Estimated,
                false, ["estimate.confidence"], canBypass: true, suppressible: true));
        AddBoolean(snapshot, target, "estimate.confidence.reason.modified_core", "ESTIMATE_MODIFIED_CORE",
            PreflightSeverity.Warning, "estimator", [], certainty: PreflightCertainty.Estimated);
        AddBoolean(snapshot, target, "estimate.confidence.reason.unknown_mods", "ESTIMATE_UNKNOWN_MOD_PROFILE",
            PreflightSeverity.Warning, "estimator", [], certainty: PreflightCertainty.Estimated);
        AddBoolean(snapshot, target, "estimate.confidence.reason.unreadable_settings", "ESTIMATE_SETTINGS_UNREADABLE",
            PreflightSeverity.Warning, "estimator", [], certainty: PreflightCertainty.Estimated);
    }

    private static void AddMemoryRules(MachineCapabilitySnapshot snapshot, List<CapabilityPreflightIssue> target)
    {
        long available = BytesToMiB(snapshot.Get<long>("memory.physical.available")?.Value ?? 0);
        long commit = BytesToMiB(snapshot.Get<long>("memory.commit.available")?.Value ?? 0);
        long heap = snapshot.Get<long>("policy.minecraft.memory")?.Value ?? 0;
        long heapRuntime = snapshot.Get<long>("estimate.heap.runtime")?.Value ?? 0;
        long heapLaunch = snapshot.Get<long>("estimate.heap.launch")?.Value ?? 0;
        long heapMinimum = snapshot.Get<long>("estimate.heap.hard_minimum")?.Value ?? 0;
        long physicalLaunch = snapshot.Get<long>("estimate.physical.launch")?.Value ?? 0;
        long physicalRuntime = snapshot.Get<long>("estimate.physical.runtime")?.Value ?? 0;
        long commitLaunch = snapshot.Get<long>("estimate.commit.launch")?.Value ?? 0;
        long commitRuntime = snapshot.Get<long>("estimate.commit.runtime")?.Value ?? 0;
        bool pagefileCanGrow = IsTrue(snapshot, "memory.pagefile.growth_possible");
        bool pagefileCannotGrow = IsFalse(snapshot, "memory.pagefile.growth_possible");
        AddEstimatedLimit(target, heap > 0 && heapRuntime > heap, "MEM_HEAP_RUNTIME_LOW",
            ["policy.minecraft.memory", "estimate.heap.runtime"], "remediation.memory.adjust_heap");
        AddEstimatedLimit(target, heap > 0 && heapLaunch > heap, "MEM_HEAP_LAUNCH_LOW",
            ["policy.minecraft.memory", "estimate.heap.launch"], "remediation.memory.adjust_heap");
        AddEstimatedLimit(target, available > 0 && heap > available, "MEM_HEAP_ABOVE_PHYSICAL_AVAILABLE",
            ["policy.minecraft.memory", "memory.physical.available"], "remediation.memory.adjust_heap");
        if (heap > 0 && heapMinimum > 0 && heap < heapMinimum)
            target.Add(new("MEM_HEAP_BELOW_HARD_MINIMUM", PreflightSeverity.Blocked, "memory",
                PreflightCertainty.Verified, true, ["policy.minecraft.memory", "estimate.heap.hard_minimum"],
                remediations: ["remediation.memory.adjust_heap"]));
        AddEstimatedLimit(target, available > 0 && physicalLaunch > available, "MEM_PHYSICAL_LAUNCH_LOW",
            ["estimate.physical.launch", "memory.physical.available"], "remediation.memory.release_background");
        AddEstimatedLimit(target, available > 0 && physicalRuntime > available, "MEM_PHYSICAL_RUNTIME_LOW",
            ["estimate.physical.runtime", "memory.physical.available"], "remediation.memory.adjust_heap");
        AddEstimatedLimit(target, available > 0 && physicalLaunch > available && physicalRuntime > available,
            "MEM_PHYSICAL_SEVERE", ["estimate.physical.launch", "estimate.physical.runtime", "memory.physical.available"],
            "remediation.memory.release_background");
        AddEstimatedLimit(target, commit > 0 && commitLaunch > commit, "MEM_COMMIT_LAUNCH_LOW",
            ["estimate.commit.launch", "memory.commit.available"], "remediation.memory.inspect_commit");
        AddEstimatedLimit(target, commit > 0 && commitRuntime > commit, "MEM_COMMIT_RUNTIME_LOW",
            ["estimate.commit.runtime", "memory.commit.available"], "remediation.memory.inspect_commit");
        AddEstimatedLimit(target, commit > 0 && commitLaunch > commit && pagefileCanGrow, "MEM_COMMIT_GROWTH_REQUIRED",
            ["estimate.commit.launch", "memory.commit.available", "memory.pagefile.growth_possible"],
            "remediation.memory.open_pagefile_settings");
        AddEstimatedLimit(target, commit > 0 && commitLaunch > commit && pagefileCannotGrow, "MEM_COMMIT_HARD_LIMIT",
            ["estimate.commit.launch", "memory.commit.available", "memory.pagefile.growth_possible"],
            "remediation.memory.inspect_commit");
        AddBoolean(snapshot, target, "memory.derived.commit_near_limit", "MEM_COMMIT_NEAR_LIMIT",
            PreflightSeverity.Critical, "memory", ["remediation.memory.inspect_commit"]);
        AddBoolean(snapshot, target, "memory.derived.pagefile_disk_low", "MEM_PAGEFILE_DISK_LOW",
            PreflightSeverity.Critical, "memory", ["remediation.memory.open_pagefile_settings"]);
        AddBoolean(snapshot, target, "memory.derived.pagefile_fixed_max", "MEM_PAGEFILE_FIXED_MAX",
            PreflightSeverity.Warning, "memory", ["remediation.memory.open_pagefile_settings"]);
    }

    private static void AddEstimatedLimit(List<CapabilityPreflightIssue> target, bool condition, string code,
        string[] evidence, string remediation)
    {
        if (condition) target.Add(new(code, PreflightSeverity.Critical, "memory", PreflightCertainty.Estimated,
            false, evidence, remediations: [remediation], canBypass: true));
    }

    private static void AddInputRules(MachineCapabilitySnapshot snapshot, List<CapabilityPreflightIssue> target)
    {
        string? primary = snapshot.Get<string>("input.usage.primary")?.Value;
        if (primary == InputUsageKind.Touch.ToString() && IsFalse(snapshot, "minecraft.input.touch_support"))
            target.Add(new("INPUT_TOUCH_SUPPORT_MISSING", PreflightSeverity.Warning, "input", PreflightCertainty.Verified,
                false, ["input.usage.primary", "minecraft.input.touch_support"], canBypass: true, suppressible: true));
        if (primary == InputUsageKind.Controller.ToString() && IsFalse(snapshot, "minecraft.input.controller_support"))
            target.Add(new("INPUT_CONTROLLER_SUPPORT_MISSING", PreflightSeverity.Warning, "input", PreflightCertainty.Verified,
                false, ["input.usage.primary", "minecraft.input.controller_support"], canBypass: true, suppressible: true));
    }

    private static void AddFileAndEnvironmentRules(MachineCapabilitySnapshot snapshot, List<CapabilityPreflightIssue> target)
    {
        if ((snapshot.Get<int>("minecraft.files.missing")?.Value ?? 0) > 0)
            target.Add(new("GAME_FILES_MISSING", PreflightSeverity.Critical, "game", PreflightCertainty.Verified,
                false, ["minecraft.files.missing"], remediations: ["remediation.game.repair_files"], canBypass: true));
        AddBoolean(snapshot, target, "minecraft.files.corrupted", "GAME_FILES_CORRUPTED", PreflightSeverity.Critical,
            "game", ["remediation.game.repair_files"]);
        AddBoolean(snapshot, target, "minecraft.files.repair_failed", "GAME_REPAIR_FAILED", PreflightSeverity.Critical,
            "game", ["remediation.game.repair_version"]);
        AddBoolean(snapshot, target, "minecraft.metadata.invalid", "GAME_METADATA_INVALID", PreflightSeverity.Blocked,
            "game", ["remediation.game.repair_version"], hard: true);
        AddBoolean(snapshot, target, "minecraft.main_class.missing", "GAME_MAIN_CLASS_MISSING", PreflightSeverity.Blocked,
            "game", ["remediation.game.repair_version"], hard: true);
        AddBoolean(snapshot, target, "minecraft.classpath.unresolved", "GAME_CLASSPATH_UNRESOLVED", PreflightSeverity.Blocked,
            "game", ["remediation.game.repair_files"], hard: true);
        AddBoolean(snapshot, target, "minecraft.native.incompatible", "GAME_NATIVE_INCOMPATIBLE", PreflightSeverity.Blocked,
            "game", ["remediation.game.repair_version"], hard: true);
        AddBoolean(snapshot, target, "platform.compatibility.unsupported", "OS_UNSUPPORTED", PreflightSeverity.Blocked,
            "game", [], hard: true);
        if (IsFalse(snapshot, "instance.path.exists"))
            target.Add(new("INSTANCE_PATH_UNAVAILABLE", PreflightSeverity.Blocked, "instance", PreflightCertainty.Verified,
                true, ["instance.path.exists"], remediations: ["remediation.instance.move"]));
        if (IsFalse(snapshot, "instance.path.writable"))
            target.Add(new("INSTANCE_PATH_NOT_WRITABLE", PreflightSeverity.Blocked, "instance", PreflightCertainty.Verified,
                true, ["instance.path.writable"], remediations: ["remediation.instance.recheck_permissions", "remediation.instance.move"]));
        AddBoolean(snapshot, target, "storage.derived.space_low", "STORAGE_SPACE_LOW", PreflightSeverity.Warning,
            "storage", ["remediation.instance.move"]);
        AddBoolean(snapshot, target, "storage.derived.space_critical", "STORAGE_SPACE_CRITICAL", PreflightSeverity.Critical,
            "storage", ["remediation.instance.move"]);
        AddBoolean(snapshot, target, "account.derived.required_missing", "ACCOUNT_REQUIRED", PreflightSeverity.Blocked,
            "account", [], hard: true);
        if (IsTrue(snapshot, "account.authentication.required") && IsFalse(snapshot, "account.authentication.valid"))
            target.Add(new("ACCOUNT_AUTH_FAILED", PreflightSeverity.Blocked, "account", PreflightCertainty.Verified,
                true, ["account.authentication.required", "account.authentication.valid"]));
        AddBoolean(snapshot, target, "hook.derived.required_failed", "HOOK_REQUIRED_FAILED", PreflightSeverity.Blocked,
            "hook", [], hard: true);
        AddBoolean(snapshot, target, "telemetry.runtime.collection", "UX_DATA_COLLECTION", PreflightSeverity.Information,
            "privacy", [], certainty: PreflightCertainty.Verified);
    }

    private static long BytesToMiB(long bytes) => Math.Max(0, bytes / (1024 * 1024));

    internal static IReadOnlyList<CapabilityPreflightIssue> NormalizeAndDedupe(IEnumerable<CapabilityPreflightIssue> issues) =>
        Array.AsReadOnly(issues.GroupBy(static issue => issue.Code.Trim().ToUpperInvariant(), StringComparer.Ordinal)
            .Select(static group => group.OrderByDescending(static issue => issue.Severity)
                .ThenByDescending(static issue => issue.Certainty).First())
            .OrderByDescending(static issue => issue.Severity).ThenBy(static issue => issue.Code, StringComparer.Ordinal).ToArray());

    internal static IReadOnlyList<CapabilityPreflightIssue> CollapseCausalGraph(IReadOnlyList<CapabilityPreflightIssue> issues)
    {
        HashSet<string> codes = issues.Select(static issue => issue.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> caused = issues.SelectMany(static issue => issue.Causes)
            .Where(codes.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Array.AsReadOnly(issues.Where(issue => !caused.Contains(issue.Code)).ToArray());
    }
}

public static class PreflightCatalog
{
    private const string Provider = "nexa.preflight";
    public static readonly CapabilityDefinition<bool> Available = Definition<bool>("preflight.available", "预检可用");
    public static readonly CapabilityDefinition<bool> Running = Definition<bool>("preflight.running", "预检运行中");
    public static readonly CapabilityDefinition<bool> Completed = Definition<bool>("preflight.completed", "预检完成");
    public static readonly CapabilityDefinition<int> IssueCount = Definition<int>("preflight.issue_count", "问题数");
    public static readonly CapabilityDefinition<int> WarningCount = Definition<int>("preflight.warning_count", "警告数");
    public static readonly CapabilityDefinition<int> CriticalCount = Definition<int>("preflight.critical_count", "严重问题数");
    public static readonly CapabilityDefinition<int> BlockedCount = Definition<int>("preflight.blocked_count", "阻止问题数");
    public static readonly CapabilityDefinition<string> OverallSeverity = Definition<string>("preflight.overall_severity", "总体严重度");
    internal static readonly FrozenDictionary<string, string> RuleCodes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["preflight.memory.heap.runtime_low"] = "MEM_HEAP_RUNTIME_LOW",
        ["preflight.memory.heap.launch_low"] = "MEM_HEAP_LAUNCH_LOW",
        ["preflight.memory.heap.above_physical_available"] = "MEM_HEAP_ABOVE_PHYSICAL_AVAILABLE",
        ["preflight.memory.heap.below_hard_minimum"] = "MEM_HEAP_BELOW_HARD_MINIMUM",
        ["preflight.memory.physical.runtime_low"] = "MEM_PHYSICAL_RUNTIME_LOW",
        ["preflight.memory.physical.launch_low"] = "MEM_PHYSICAL_LAUNCH_LOW",
        ["preflight.memory.physical.severe"] = "MEM_PHYSICAL_SEVERE",
        ["preflight.memory.commit.runtime_low"] = "MEM_COMMIT_RUNTIME_LOW",
        ["preflight.memory.commit.launch_low"] = "MEM_COMMIT_LAUNCH_LOW",
        ["preflight.memory.commit.growth_required"] = "MEM_COMMIT_GROWTH_REQUIRED",
        ["preflight.memory.commit.near_limit"] = "MEM_COMMIT_NEAR_LIMIT",
        ["preflight.memory.commit.hard_limit"] = "MEM_COMMIT_HARD_LIMIT",
        ["preflight.memory.pagefile.disk_low"] = "MEM_PAGEFILE_DISK_LOW",
        ["preflight.memory.pagefile.fixed_max"] = "MEM_PAGEFILE_FIXED_MAX",
        ["preflight.estimator.pending"] = "MEM_ESTIMATE_PENDING",
        ["preflight.estimator.failed"] = "MEM_ESTIMATE_FAILED",
        ["preflight.estimator.low_confidence"] = "MEM_ESTIMATE_LOW_CONFIDENCE",
        ["preflight.estimator.modified_core"] = "ESTIMATE_MODIFIED_CORE",
        ["preflight.estimator.unknown_mod_profile"] = "ESTIMATE_UNKNOWN_MOD_PROFILE",
        ["preflight.estimator.settings_unreadable"] = "ESTIMATE_SETTINGS_UNREADABLE",
        ["preflight.gpu.low_performance_adapter"] = "GPU_LOW_PERFORMANCE_ADAPTER",
        ["preflight.gpu.vram.runtime_low"] = "GPU_VRAM_RUNTIME_LOW",
        ["preflight.gpu.vram.launch_critical"] = "GPU_VRAM_LAUNCH_CRITICAL",
        ["preflight.gpu.temperature.high"] = "GPU_TEMPERATURE_HIGH",
        ["preflight.gpu.temperature.near_limit"] = "GPU_TEMPERATURE_NEAR_LIMIT",
        ["preflight.cpu.temperature.high"] = "CPU_TEMPERATURE_HIGH",
        ["preflight.cpu.temperature.near_limit"] = "CPU_TEMPERATURE_NEAR_LIMIT",
        ["preflight.input.touch_support_missing"] = "INPUT_TOUCH_SUPPORT_MISSING",
        ["preflight.input.controller_support_missing"] = "INPUT_CONTROLLER_SUPPORT_MISSING",
        ["preflight.mod.compatibility.warning"] = "MOD_COMPAT_WARNING",
        ["preflight.mod.compatibility.critical"] = "MOD_COMPAT_CRITICAL",
        ["preflight.mod.required_dependency_missing"] = "MOD_REQUIRED_DEPENDENCY_MISSING",
        ["preflight.mod.hard_conflict"] = "MOD_HARD_CONFLICT",
        ["preflight.mod.loader_incompatible"] = "MOD_LOADER_INCOMPATIBLE",
        ["preflight.mod.minecraft_incompatible"] = "MOD_MC_VERSION_INCOMPATIBLE",
        ["preflight.java.missing"] = "JAVA_MISSING",
        ["preflight.java.non_recommended"] = "JAVA_NON_RECOMMENDED",
        ["preflight.java.hard_incompatible"] = "JAVA_HARD_INCOMPATIBLE",
        ["preflight.java.arch_incompatible"] = "JAVA_ARCH_INCOMPATIBLE",
        ["preflight.java.emulated"] = "JAVA_EMULATED",
        ["preflight.loader.missing"] = "LOADER_MISSING",
        ["preflight.loader.incompatible"] = "LOADER_INCOMPATIBLE",
        ["preflight.game.files_missing"] = "GAME_FILES_MISSING",
        ["preflight.game.files_corrupted"] = "GAME_FILES_CORRUPTED",
        ["preflight.game.repair_failed"] = "GAME_REPAIR_FAILED",
        ["preflight.game.metadata_invalid"] = "GAME_METADATA_INVALID",
        ["preflight.game.main_class_missing"] = "GAME_MAIN_CLASS_MISSING",
        ["preflight.game.classpath_unresolved"] = "GAME_CLASSPATH_UNRESOLVED",
        ["preflight.game.native_incompatible"] = "GAME_NATIVE_INCOMPATIBLE",
        ["preflight.game.os_unsupported"] = "OS_UNSUPPORTED",
        ["preflight.storage.space_low"] = "STORAGE_SPACE_LOW",
        ["preflight.storage.space_critical"] = "STORAGE_SPACE_CRITICAL",
        ["preflight.instance.path_unavailable"] = "INSTANCE_PATH_UNAVAILABLE",
        ["preflight.instance.path_not_writable"] = "INSTANCE_PATH_NOT_WRITABLE",
        ["preflight.account.required"] = "ACCOUNT_REQUIRED",
        ["preflight.account.auth_failed"] = "ACCOUNT_AUTH_FAILED",
        ["preflight.hook.required_failed"] = "HOOK_REQUIRED_FAILED",
        ["preflight.telemetry.runtime_collection"] = "UX_DATA_COLLECTION",
    }.ToFrozenDictionary(StringComparer.Ordinal);
    internal static readonly FrozenDictionary<string, CapabilityDefinition<bool>> RuleDefinitions = RuleCodes.Keys
        .ToFrozenDictionary(static id => id, static id => Definition<bool>(
            id, CapabilityPresentationCatalog.Preflight(RuleCodes[id]).Title), StringComparer.Ordinal);

    public static IReadOnlyList<ICapabilityDefinition> Definitions() =>
        [Available, Running, Completed, IssueCount, WarningCount, CriticalCount, BlockedCount, OverallSeverity,
            .. RuleDefinitions.Values];
    private static CapabilityDefinition<T> Definition<T>(string id, string label) =>
        new(id, label, "启动预检", Provider, CapabilityKind.Derived, CapabilityStability.Dynamic);
}

public sealed class PreflightProjection : ICapabilityProjection
{
    public IReadOnlyList<ICapability> Project(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp)
    {
        CapabilityPreflightReport report = CapabilityPreflightEngine.Evaluate(new MachineCapabilitySnapshot(0, timestamp, values.Values));
        const string source = "CapabilityPreflightEngine";
        List<ICapability> projected =
        [
            PreflightCatalog.Available.Observe(true, timestamp, source),
            PreflightCatalog.Running.Observe(false, timestamp, source),
            PreflightCatalog.Completed.Observe(true, timestamp, source),
            PreflightCatalog.IssueCount.Observe(report.CollapsedIssues.Count, timestamp, source),
            PreflightCatalog.WarningCount.Observe(report.CollapsedIssues.Count(static item => item.Severity == PreflightSeverity.Warning), timestamp, source),
            PreflightCatalog.CriticalCount.Observe(report.CollapsedIssues.Count(static item => item.Severity == PreflightSeverity.Critical), timestamp, source),
            PreflightCatalog.BlockedCount.Observe(report.CollapsedIssues.Count(static item => item.Severity == PreflightSeverity.Blocked), timestamp, source),
            PreflightCatalog.OverallSeverity.Observe(report.OverallSeverity.ToString(), timestamp, source),
        ];
        HashSet<string> codes = report.Issues.Select(static issue => issue.Code).ToHashSet(StringComparer.Ordinal);
        projected.AddRange(PreflightCatalog.RuleDefinitions.Select(pair => pair.Value.Observe(
            codes.Contains(PreflightCatalog.RuleCodes[pair.Key]), timestamp, source)));
        return Array.AsReadOnly(projected.ToArray());
    }
}

public sealed record RemediationDefinition(string Id, string Label, bool RequiresConfirmation);

public static class RemediationCatalog
{
    private static readonly RemediationDefinition[] Items =
    [
        new("remediation.memory.adjust_heap", "调整堆内存", true),
        new("remediation.memory.release_background", "释放后台内存", true),
        new("remediation.memory.inspect_commit", "检查提交预算", false),
        new("remediation.memory.open_pagefile_settings", "打开页面文件设置", false),
        new("remediation.java.select", "选择 Java", false),
        new("remediation.java.download", "下载合适的 Java", true),
        new("remediation.java.switch_recommended", "切换到推荐 Java", true),
        new("remediation.gpu.select_high_performance", "选择高性能显卡", true),
        new("remediation.gpu.reduce_resource_settings", "降低资源设置", true),
        new("remediation.loader.repair", "修复加载器", true),
        new("remediation.loader.switch", "切换加载器", true),
        new("remediation.mod.install_dependency", "安装缺失依赖", true),
        new("remediation.mod.resolve_conflict", "处理模组冲突", true),
        new("remediation.game.repair_files", "修复游戏文件", true),
        new("remediation.game.repair_version", "修复游戏版本", true),
        new("remediation.instance.move", "移动实例", true),
        new("remediation.instance.recheck_permissions", "重新检查目录权限", false),
    ];
    private static readonly FrozenDictionary<string, RemediationDefinition> ById =
        Items.ToFrozenDictionary(static item => item.Id, StringComparer.Ordinal);

    public static IReadOnlyList<RemediationDefinition> All => Items;
    public static RemediationDefinition Get(string id) => ById.TryGetValue(id, out RemediationDefinition? item)
        ? item : throw new KeyNotFoundException("Unknown remediation: " + id);
    public static IReadOnlyList<RemediationDefinition> For(CapabilityPreflightIssue issue) =>
        Array.AsReadOnly(issue.Remediations.Select(Get).ToArray());

    public static IReadOnlyList<ICapabilityDefinition> Definitions() => Items.Select(static item =>
        (ICapabilityDefinition)new CapabilityDefinition<bool>(item.Id, item.Label, "修复", "nexa.remediation",
            CapabilityKind.Action, CapabilityStability.Dynamic)).ToArray();
}

public sealed record RemediationRequest(string Id, IReadOnlyDictionary<string, string>? Arguments = null,
    bool Confirmed = false);
public sealed record RemediationResult(string Id, bool Succeeded, string Code, string Message);

public interface IRemediationHandler
{
    string Id { get; }
    ValueTask<RemediationResult> ExecuteAsync(RemediationRequest request, CancellationToken cancellationToken);
}

public interface IRemediationAvailability
{
    bool Available { get; }
}

/// <summary>Typed action dispatcher. Product composition registers handlers owned by the
/// corresponding service; unknown and unconfirmed actions never fall through to a generic fix.</summary>
public sealed class RemediationService(IEnumerable<IRemediationHandler>? handlers = null)
{
    private readonly FrozenDictionary<string, IRemediationHandler> _handlers = (handlers ?? [])
        .ToFrozenDictionary(static handler => handler.Id, StringComparer.Ordinal);

    public bool CanExecute(string id) => _handlers.TryGetValue(id, out var handler)
        && (handler is not IRemediationAvailability availability || availability.Available);

    public async ValueTask<RemediationResult> ExecuteAsync(RemediationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RemediationDefinition definition = RemediationCatalog.Get(request.Id);
        if (definition.RequiresConfirmation && !request.Confirmed)
            return new(request.Id, false, "confirmation_required", "此操作需要确认。");
        if (!_handlers.TryGetValue(request.Id, out IRemediationHandler? handler) || !CanExecute(request.Id))
            return new(request.Id, false, "handler_unavailable", "当前环境没有可执行此操作的服务。");
        RemediationResult result = await handler.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(result.Id, request.Id, StringComparison.Ordinal))
            throw new InvalidOperationException("Remediation handler returned a mismatched action id.");
        return result;
    }
}
