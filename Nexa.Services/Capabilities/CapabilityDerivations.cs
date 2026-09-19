using Nexa.Xsr;

namespace Nexa.Services.Capabilities;

/// <summary>
/// One broker-level derivation: computes a Derived-kind capability from other capabilities'
/// collected values AFTER providers finish. Derivations are the only sanctioned way for a
/// capability to depend on facts owned by a different provider (the broker enforces
/// per-provider ownership at collection time).
/// </summary>
public interface ICapabilityDerivation
{
    string Id { get; }

    ICapability Evaluate(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp);
}

/// <summary>
/// Shared evaluation plumbing: typed input reads with availability guards. A rule whose
/// inputs are unavailable answers DependencyMissing instead of guessing.
/// </summary>
public abstract class CapabilityDerivation<T> : ICapabilityDerivation
{
    private readonly CapabilityDefinition<T> _definition;
    private readonly string[] _inputs;

    protected CapabilityDerivation(CapabilityDefinition<T> definition, params string[] inputs)
    {
        _definition = definition;
        _inputs = inputs;
    }

    public string Id => _definition.Id;

    public ICapability Evaluate(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp)
    {
        foreach (string input in _inputs)
        {
            if (!values.TryGetValue(input, out ICapability? fact)
                || fact.Availability is not (CapabilityAvailability.Available or CapabilityAvailability.PlatformUnsupported))
            {
                return _definition.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, $"所需能力尚不可用：{input}");
            }
        }

        return Compute(values, timestamp);
    }

    protected abstract ICapability Compute(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp);

    protected Capability<TIn>? Read<TIn>(IReadOnlyDictionary<string, ICapability> values, string id) =>
        values.TryGetValue(id, out ICapability? fact) ? fact as Capability<TIn> : null;

    protected ICapability Emit(T value, DateTimeOffset timestamp, string source) =>
        _definition.Observe(value, timestamp, source);
}

/// <summary>platform.compatibility.* — architecture emulation detection (§4).</summary>
public sealed class NativeExecutionDerivation : CapabilityDerivation<bool>
{
    public static readonly CapabilityDefinition<bool> NativeExecution = new(
        "platform.compatibility.native_execution", "本机执行（无模拟）", "系统", "nexa.runtime",
        CapabilityKind.Derived, CapabilityStability.Static, ["platform.arch.native", "platform.arch.process"]);
    public static readonly CapabilityDefinition<bool> EmulatedExecution = new(
        "platform.compatibility.emulated_execution", "架构模拟运行", "系统", "nexa.runtime",
        CapabilityKind.Derived, CapabilityStability.Static, ["platform.arch.native", "platform.arch.process"]);

    public NativeExecutionDerivation() : base(NativeExecution, NativeExecution.Requirements.ToArray())
    {
    }

    protected override ICapability Compute(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp)
    {
        string? native = Read<string>(values, "platform.arch.native")?.Value;
        string? process = Read<string>(values, "platform.arch.process")?.Value;
        bool nativeExecution = string.Equals(native, process, StringComparison.OrdinalIgnoreCase);
        return Emit(nativeExecution, timestamp, "架构比对（native == process）");
    }
}

/// <summary>memory.derived.* physical/commit pressure thresholds (§8). Constants documented
/// per Registry table; they move into the versioned profile when the estimator lands.</summary>
public static class MemoryDerivedRules
{
    public static readonly CapabilityDefinition<bool> LowPhysical = new(
        "memory.derived.low_physical", "可用物理内存低", "内存", MachineCapabilityCatalog.MemoryProviderId,
        CapabilityKind.Derived, CapabilityStability.Dynamic, ["memory.physical.available"]);
    public static readonly CapabilityDefinition<bool> CommitLow = new(
        "memory.derived.commit_low", "提交预算低", "内存", MachineCapabilityCatalog.MemoryProviderId,
        CapabilityKind.Derived, CapabilityStability.Dynamic, ["memory.commit.available"]);
    public static readonly CapabilityDefinition<bool> CommitNearLimit = new(
        "memory.derived.commit_near_limit", "提交预算接近上限", "内存", MachineCapabilityCatalog.MemoryProviderId,
        CapabilityKind.Derived, CapabilityStability.Dynamic, ["memory.commit.available", "memory.commit.limit"]);

    public sealed class LowPhysicalRule : CapabilityDerivation<bool>
    {
        public LowPhysicalRule() : base(LowPhysical, LowPhysical.Requirements.ToArray()) { }
        protected override ICapability Compute(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp) =>
            Emit(Read<long>(values, LowPhysical.Requirements[0])!.Value < 2L * 1024 * 1024 * 1024,
                timestamp, "阈值：<2 GiB 可用");
    }

    public sealed class CommitLowRule : CapabilityDerivation<bool>
    {
        public CommitLowRule() : base(CommitLow, CommitLow.Requirements.ToArray()) { }
        protected override ICapability Compute(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp) =>
            Emit(Read<long>(values, CommitLow.Requirements[0])!.Value < 2L * 1024 * 1024 * 1024,
                timestamp, "阈值：<2 GiB 剩余提交");
    }

    public sealed class CommitNearLimitRule : CapabilityDerivation<bool>
    {
        public CommitNearLimitRule() : base(CommitNearLimit, CommitNearLimit.Requirements.ToArray()) { }
        protected override ICapability Compute(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp)
        {
            long available = Read<long>(values, CommitNearLimit.Requirements[0])!.Value;
            long limit = Read<long>(values, CommitNearLimit.Requirements[1])!.Value;
            return Emit(limit > 0 && available < limit * 15 / 100, timestamp, "阈值：剩余 < 15% 上限");
        }
    }
}

/// <summary>machine.memory.* coarse machine classes (§32).</summary>
public static class MachineDerivedRules
{
    public const string ProviderId = "nexa.machine";
    public static readonly CapabilityDefinition<bool> MemoryLow = new(
        "machine.memory.low", "内存吃紧", "机器画像", ProviderId,
        CapabilityKind.Derived, CapabilityStability.Dynamic, ["memory.physical.usable"]);
    public static readonly CapabilityDefinition<bool> MemoryConstrained = new(
        "machine.memory.constrained", "内存受限", "机器画像", ProviderId,
        CapabilityKind.Derived, CapabilityStability.Dynamic, ["memory.physical.usable"]);
    public static readonly CapabilityDefinition<bool> MemoryAbundant = new(
        "machine.memory.abundant", "内存充裕", "机器画像", ProviderId,
        CapabilityKind.Derived, CapabilityStability.Dynamic, ["memory.physical.usable"]);
    public static readonly CapabilityDefinition<bool> JavaDerivedMissing = new(
        "java.derived.missing", "缺少可用 Java", "Java", MachineInstanceCatalog.JavaProviderId,
        CapabilityKind.Derived, CapabilityStability.Dynamic, ["java.installed"]);
    public static readonly CapabilityDefinition<bool> JavaDerivedHardIncompatible = new(
        "java.derived.hard_incompatible", "Java 硬性不兼容", "Java", MachineInstanceCatalog.JavaProviderId,
        CapabilityKind.Derived, CapabilityStability.Session, ["java.compatibility.hard"]);

    public sealed class MemoryLowRule : CapabilityDerivation<bool>
    {
        public MemoryLowRule() : base(MemoryLow, MemoryLow.Requirements.ToArray()) { }
        protected override ICapability Compute(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp) =>
            Emit(Read<long>(values, MemoryLow.Requirements[0])!.Value < 4L * 1024 * 1024 * 1024, timestamp, "阈值：<4 GiB");
    }

    public sealed class MemoryConstrainedRule : CapabilityDerivation<bool>
    {
        public MemoryConstrainedRule() : base(MemoryConstrained, MemoryConstrained.Requirements.ToArray()) { }
        protected override ICapability Compute(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp)
        {
            long usable = Read<long>(values, MemoryConstrained.Requirements[0])!.Value;
            return Emit(usable is >= 4L * 1024 * 1024 * 1024 and < 8L * 1024 * 1024 * 1024, timestamp, "阈值：4-8 GiB");
        }
    }

    public sealed class MemoryAbundantRule : CapabilityDerivation<bool>
    {
        public MemoryAbundantRule() : base(MemoryAbundant, MemoryAbundant.Requirements.ToArray()) { }
        protected override ICapability Compute(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp) =>
            Emit(Read<long>(values, MemoryAbundant.Requirements[0])!.Value >= 16L * 1024 * 1024 * 1024, timestamp, "阈值：≥16 GiB");
    }

    public sealed class JavaMissingRule : CapabilityDerivation<bool>
    {
        public JavaMissingRule() : base(JavaDerivedMissing, JavaDerivedMissing.Requirements.ToArray()) { }
        protected override ICapability Compute(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp) =>
            Emit(Read<bool>(values, JavaDerivedMissing.Requirements[0])!.Value is false, timestamp, "java.installed == false");
    }

    public sealed class JavaHardIncompatibleRule : CapabilityDerivation<bool>
    {
        public JavaHardIncompatibleRule() : base(JavaDerivedHardIncompatible, JavaDerivedHardIncompatible.Requirements.ToArray()) { }
        protected override ICapability Compute(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp) =>
            Emit(Read<bool>(values, JavaDerivedHardIncompatible.Requirements[0])!.Value is false, timestamp, "java.compatibility.hard == false");
    }

    /// <summary>The default derivation set the composition registers.</summary>
    public static IReadOnlyList<ICapabilityDerivation> Defaults() => (ICapabilityDerivation[])
    [
            new NativeExecutionDerivation(),
            new EmulatedExecutionDerivation(),
            new MemoryDerivedRules.LowPhysicalRule(),
            new MemoryDerivedRules.CommitLowRule(),
            new MemoryDerivedRules.CommitNearLimitRule(),
            new MemoryLowRule(),
            new MemoryConstrainedRule(),
            new MemoryAbundantRule(),
            new JavaMissingRule(),
            new JavaHardIncompatibleRule(),
        ];

    /// <summary>Definitions these derivations own; merged at composition.</summary>
    public static IReadOnlyList<ICapabilityDefinition> Definitions() => (ICapabilityDefinition[])
    [
        NativeExecutionDerivation.NativeExecution,
        NativeExecutionDerivation.EmulatedExecution,
        MemoryDerivedRules.LowPhysical,
        MemoryDerivedRules.CommitLow,
        MemoryDerivedRules.CommitNearLimit,
        MemoryLow,
        MemoryConstrained,
        MemoryAbundant,
        JavaDerivedMissing,
        JavaDerivedHardIncompatible,
    ];
}

/// <summary>platform.compatibility.emulated_execution as its own rule record.</summary>
public sealed class EmulatedExecutionDerivation : ICapabilityDerivation
{
    public string Id => NativeExecutionDerivation.EmulatedExecution.Id;

    public ICapability Evaluate(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp)
    {
        if (!values.TryGetValue("platform.arch.native", out ICapability? native)
            || native is not Capability<string> nativeArch
            || !values.TryGetValue("platform.arch.process", out ICapability? process)
            || process is not Capability<string> processArch)
        {
            return NativeExecutionDerivation.EmulatedExecution.Unavailable(
                CapabilityAvailability.DependencyMissing, timestamp, "所需能力尚不可用：架构事实");
        }

        bool emulated = !string.Equals(nativeArch.Value, processArch.Value, StringComparison.OrdinalIgnoreCase);
        return NativeExecutionDerivation.EmulatedExecution.Observe(emulated, timestamp, "架构比对（native != process）");
    }
}
