using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Nexa.Services.Capabilities;

public static class MachineCapabilityCatalog
{
    public const string RuntimeProviderId = "nexa.runtime";
    public const string MemoryProviderId = "nexa.memory";
    public static readonly CapabilityDefinition<string> Os = new("platform.os", "操作系统", "系统", RuntimeProviderId);
    public static readonly CapabilityDefinition<string> OsVersion = new("platform.os.version", "系统版本", "系统", RuntimeProviderId);
    public static readonly CapabilityDefinition<string> NativeArch = new("platform.arch.native", "系统架构", "系统", RuntimeProviderId);
    public static readonly CapabilityDefinition<string> ProcessArch = new("platform.arch.process", "进程架构", "系统", RuntimeProviderId);
    public static readonly CapabilityDefinition<string> Runtime = new("runtime.framework", "运行时", "运行环境", RuntimeProviderId);
    public static readonly CapabilityDefinition<bool> DynamicCode = new("runtime.dynamic_code", "动态代码", "运行环境", RuntimeProviderId);
    public static readonly CapabilityDefinition<int> LogicalProcessors = new("cpu.topology.logical_processors", "进程可用逻辑处理器", "处理器", RuntimeProviderId);
    public static readonly CapabilityDefinition<bool> Sse2 = new("cpu.isa.sse2", "SSE2", "处理器", RuntimeProviderId);
    public static readonly CapabilityDefinition<bool> Avx2 = new("cpu.isa.avx2", "AVX2", "处理器", RuntimeProviderId);
    public static readonly CapabilityDefinition<bool> Neon = new("cpu.isa.arm.neon", "ARM Neon", "处理器", RuntimeProviderId);
    public static readonly CapabilityDefinition<long> PhysicalUsable = Memory("memory.physical.usable", "可用物理内存总量");
    public static readonly CapabilityDefinition<long> PhysicalAvailable = Memory("memory.physical.available", "当前可用物理内存");
    public static readonly CapabilityDefinition<long> CommitTotal = Memory("memory.commit.total", "已提交内存");
    public static readonly CapabilityDefinition<long> CommitLimit = Memory("memory.commit.limit", "提交上限");
    public static readonly CapabilityDefinition<long> CommitAvailable = new("memory.commit.available", "剩余提交预算", "内存", MemoryProviderId,
        CapabilityKind.Derived, CapabilityStability.Dynamic, ["memory.commit.total", "memory.commit.limit"], "bytes");
    private static CapabilityDefinition<long> Memory(string id, string label) => new(id, label, "内存", MemoryProviderId, CapabilityKind.Metric, CapabilityStability.Dynamic, unit: "bytes");
    public static CapabilityRegistry CreateRegistry() => new([
        Os, OsVersion, NativeArch, ProcessArch, Runtime, DynamicCode, LogicalProcessors, Sse2, Avx2, Neon,
        PhysicalUsable, PhysicalAvailable, CommitTotal, CommitLimit, CommitAvailable,
    ]);
    public static IReadOnlyList<IMachineCapabilityProvider> CreateProviders() => Array.AsReadOnly<IMachineCapabilityProvider>([new RuntimeCapabilityProvider(), new MemoryCapabilityProvider()]);
}

internal sealed class RuntimeCapabilityProvider : IMachineCapabilityProvider
{
    public string Id => MachineCapabilityCatalog.RuntimeProviderId;
    public ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string source = ".NET RuntimeInformation / Environment";
        const string isa = ".NET Intrinsics（当前进程可用指令集）";
        return ValueTask.FromResult<IReadOnlyList<ICapability>>(Array.AsReadOnly<ICapability>([
            MachineCapabilityCatalog.Os.Observe(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "Other", timestamp, source),
            MachineCapabilityCatalog.OsVersion.Observe(RuntimeInformation.OSDescription, timestamp, source),
            MachineCapabilityCatalog.NativeArch.Observe(RuntimeInformation.OSArchitecture.ToString(), timestamp, source),
            MachineCapabilityCatalog.ProcessArch.Observe(RuntimeInformation.ProcessArchitecture.ToString(), timestamp, source),
            MachineCapabilityCatalog.Runtime.Observe(RuntimeInformation.FrameworkDescription, timestamp, source),
            MachineCapabilityCatalog.DynamicCode.Observe(RuntimeFeature.IsDynamicCodeSupported, timestamp, source),
            MachineCapabilityCatalog.LogicalProcessors.Observe(Environment.ProcessorCount, timestamp, source),
            MachineCapabilityCatalog.Sse2.Observe(Sse2.IsSupported, timestamp, isa),
            MachineCapabilityCatalog.Avx2.Observe(Avx2.IsSupported, timestamp, isa),
            MachineCapabilityCatalog.Neon.Observe(AdvSimd.IsSupported, timestamp, isa),
        ]));
    }
}

internal sealed partial class MemoryCapabilityProvider : IMachineCapabilityProvider
{
    public string Id => MachineCapabilityCatalog.MemoryProviderId;
    public async ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            if (GetPerformanceInfo(out var info, (uint)Marshal.SizeOf<PerformanceInformation>()) == 0) throw new InvalidOperationException("Memory probe failed.");
            long page = checked((long)info.PageSize);
            return Values(checked((long)info.PhysicalTotal * page), checked((long)info.PhysicalAvailable * page),
                checked((long)info.CommitTotal * page), checked((long)info.CommitLimit * page), timestamp, "Windows GetPerformanceInfo");
        }
        if (OperatingSystem.IsLinux())
        {
            string text = await File.ReadAllTextAsync("/proc/meminfo", cancellationToken).ConfigureAwait(false);
            return ParseLinux(text, timestamp);
        }
        return Array.AsReadOnly(MachineCapabilityCatalog.CreateRegistry().Definitions.Where(item => item.Provider == Id)
            .Select(item => item.Unavailable(CapabilityAvailability.NotImplemented, timestamp, "此平台的内存检测尚未接入")).ToArray());
    }
    internal static IReadOnlyList<ICapability> ParseLinux(string text, DateTimeOffset timestamp)
    {
        var fields = text.Split('\n').Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], StringComparer.Ordinal);
        long Read(string name) => checked(long.Parse(fields[name], CultureInfo.InvariantCulture) * 1024);
        return Values(Read("MemTotal"), Read("MemAvailable"), Read("Committed_AS"), Read("CommitLimit"), timestamp,
            "Linux /proc/meminfo（提交上限受 overcommit 策略影响）");
    }
    private static System.Collections.ObjectModel.ReadOnlyCollection<ICapability> Values(long physical, long available, long committed, long limit, DateTimeOffset timestamp, string source) =>
        Array.AsReadOnly<ICapability>([
            MachineCapabilityCatalog.PhysicalUsable.Observe(physical, timestamp, source),
            MachineCapabilityCatalog.PhysicalAvailable.Observe(available, timestamp, source),
            MachineCapabilityCatalog.CommitTotal.Observe(committed, timestamp, source),
            MachineCapabilityCatalog.CommitLimit.Observe(limit, timestamp, source),
            MachineCapabilityCatalog.CommitAvailable.Observe(Math.Max(0, limit - committed), timestamp, source),
        ]);
    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
            KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }
    [LibraryImport("psapi.dll", SetLastError = true)]
    private static partial int GetPerformanceInfo(out PerformanceInformation information, uint size);
}
