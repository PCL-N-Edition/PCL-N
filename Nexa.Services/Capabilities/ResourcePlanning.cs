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
    public static ResourceEstimatorProfile Default { get; } = new("1.0.0", 2048, 2048, 512, 768, 512);
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
    public static readonly CapabilityDefinition<long> HeapLaunch = Estimate<long>("estimate.heap.launch", "启动堆内存", "mib");
    public static readonly CapabilityDefinition<long> HeapRuntime = Estimate<long>("estimate.heap.runtime", "运行堆内存", "mib");
    public static readonly CapabilityDefinition<long> NativeLaunch = Estimate<long>("estimate.native.launch", "启动本机内存", "mib");
    public static readonly CapabilityDefinition<long> NativeRuntime = Estimate<long>("estimate.native.runtime", "运行本机内存", "mib");
    public static readonly CapabilityDefinition<long> PhysicalLaunch = Estimate<long>("estimate.physical.launch", "启动物理内存", "mib");
    public static readonly CapabilityDefinition<long> PhysicalRuntime = Estimate<long>("estimate.physical.runtime", "运行物理内存", "mib");
    public static readonly CapabilityDefinition<long> CommitLaunch = Estimate<long>("estimate.commit.launch", "启动提交量", "mib");
    public static readonly CapabilityDefinition<long> CommitRuntime = Estimate<long>("estimate.commit.runtime", "运行提交量", "mib");

    public static IReadOnlyList<ICapabilityDefinition> Definitions() =>
    [Status, Confidence, HeapLaunch, HeapRuntime, NativeLaunch, NativeRuntime,
        PhysicalLaunch, PhysicalRuntime, CommitLaunch, CommitRuntime];

    private static CapabilityDefinition<T> Estimate<T>(string id, string label, string unit = "") =>
        new(id, label, "资源估算", ProviderId, CapabilityKind.Estimate, CapabilityStability.Dynamic, unit: unit);
}

public sealed class ResourceEstimator(ResourceEstimatorProfile? profile = null)
{
    private const string ModelVersion = "baseline-1";
    private readonly ResourceEstimatorProfile _profile = profile ?? ResourceEstimatorProfile.Default;

    public ResourceEstimateSnapshot Estimate(MachineCapabilitySnapshot snapshot) => Estimate(snapshot.Values);

    internal ResourceEstimateSnapshot Estimate(IEnumerable<ICapability> values)
    {
        string[] inputs = values.Where(static value => value.Availability == CapabilityAvailability.Available)
            .Select(static value => value.Id).Where(static id => id.StartsWith("minecraft.", StringComparison.Ordinal)
                || id.StartsWith("loader.", StringComparison.Ordinal)
                || id.StartsWith("mod.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        string[] reasons = inputs.Length == 0
            ? ["没有实例、模组或历史观测数据，使用保守基线"]
            : ["尚无足够历史观测，使用实例特征与保守基线"];
        EstimateResult Result(long value, long margin = 0) => new(value, CapabilityConfidence.Low, ModelVersion,
            _profile.Version, inputs, 0, margin, reasons);
        long physicalLaunch = _profile.HeapLaunchMiB + _profile.NativeLaunchMiB + _profile.SafetyMarginMiB;
        long physicalRuntime = _profile.HeapRuntimeMiB + _profile.NativeRuntimeMiB + _profile.SafetyMarginMiB;
        return new(ResourceEstimateStatus.Completed, CapabilityConfidence.Low,
            Result(_profile.HeapLaunchMiB), Result(_profile.HeapRuntimeMiB),
            Result(_profile.NativeLaunchMiB), Result(_profile.NativeRuntimeMiB),
            Result(physicalLaunch, _profile.SafetyMarginMiB), Result(physicalRuntime, _profile.SafetyMarginMiB),
            Result(physicalLaunch + 256, _profile.SafetyMarginMiB), Result(physicalRuntime + 256, _profile.SafetyMarginMiB));
    }
}

public sealed class ResourceEstimatorProjection(ResourceEstimator? estimator = null) : ICapabilityProjection
{
    private readonly ResourceEstimator _estimator = estimator ?? new ResourceEstimator();

    public IReadOnlyList<ICapability> Project(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp)
    {
        ResourceEstimateSnapshot estimate = _estimator.Estimate(values.Values);
        const string source = "Nexa 资源估算模型 baseline-1";
        return Array.AsReadOnly<ICapability>(
        [
            ResourceEstimateCatalog.Status.Observe(estimate.Status.ToString(), timestamp, source, CapabilityConfidence.Low),
            ResourceEstimateCatalog.Confidence.Observe(estimate.Confidence.ToString(), timestamp, source, CapabilityConfidence.Low),
            ResourceEstimateCatalog.HeapLaunch.Observe(estimate.HeapLaunch.ValueMiB, timestamp, source, CapabilityConfidence.Low),
            ResourceEstimateCatalog.HeapRuntime.Observe(estimate.HeapRuntime.ValueMiB, timestamp, source, CapabilityConfidence.Low),
            ResourceEstimateCatalog.NativeLaunch.Observe(estimate.NativeLaunch.ValueMiB, timestamp, source, CapabilityConfidence.Low),
            ResourceEstimateCatalog.NativeRuntime.Observe(estimate.NativeRuntime.ValueMiB, timestamp, source, CapabilityConfidence.Low),
            ResourceEstimateCatalog.PhysicalLaunch.Observe(estimate.PhysicalLaunch.ValueMiB, timestamp, source, CapabilityConfidence.Low),
            ResourceEstimateCatalog.PhysicalRuntime.Observe(estimate.PhysicalRuntime.ValueMiB, timestamp, source, CapabilityConfidence.Low),
            ResourceEstimateCatalog.CommitLaunch.Observe(estimate.CommitLaunch.ValueMiB, timestamp, source, CapabilityConfidence.Low),
            ResourceEstimateCatalog.CommitRuntime.Observe(estimate.CommitRuntime.ValueMiB, timestamp, source, CapabilityConfidence.Low),
        ]);
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

        long? estimateMiB = snapshot.Get<long>(ResourceEstimateCatalog.PhysicalLaunch.Id)?.Value;
        long? availableBytes = snapshot.Get<long>("memory.physical.available")?.Value;
        if (estimateMiB is > 0 && availableBytes is >= 0 && estimateMiB.Value * 1024 * 1024 > availableBytes.Value)
            collected.Add(new("MEM_HEAP_LAUNCH_LOW", PreflightSeverity.Critical, "memory", PreflightCertainty.Estimated, false,
                [ResourceEstimateCatalog.PhysicalLaunch.Id, "memory.physical.available"],
                remediations: ["remediation.memory.reduce_heap", "remediation.background.close"], canBypass: true));

        IReadOnlyList<CapabilityPreflightIssue> normalized = NormalizeAndDedupe(collected);
        IReadOnlyList<CapabilityPreflightIssue> collapsed = CollapseCausalGraph(normalized);
        return new(normalized, collapsed, CapabilityPreflightIssue.OverallSeverity(collapsed));
    }

    private static bool IsTrue(MachineCapabilitySnapshot snapshot, string id) =>
        snapshot.Get<bool>(id) is { Availability: CapabilityAvailability.Available, Value: true };

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

public sealed record RemediationDefinition(string Id, string Label, bool RequiresConfirmation);

public static class RemediationCatalog
{
    private static readonly RemediationDefinition[] Items =
    [
        new("remediation.java.download", "下载合适的 Java", true),
        new("remediation.java.select", "选择 Java", false),
        new("remediation.memory.reduce_heap", "降低堆内存", true),
        new("remediation.background.close", "关闭后台任务", true),
        new("remediation.loader.reinstall", "重新安装加载器", true),
        new("remediation.files.repair", "修复游戏文件", true),
        new("remediation.mods.disable_conflict", "停用冲突模组", true),
        new("remediation.account.sign_in", "登录账户", false),
        new("remediation.network.retry", "重试网络连接", false),
        new("remediation.storage.free_space", "释放磁盘空间", false),
        new("remediation.graphics.reduce", "降低图形设置", true),
        new("remediation.power.performance", "切换性能模式", true),
        new("remediation.instance.change_path", "更改实例目录", false),
        new("remediation.hook.disable", "停用启动钩子", true),
        new("remediation.report.open", "查看诊断报告", false),
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
