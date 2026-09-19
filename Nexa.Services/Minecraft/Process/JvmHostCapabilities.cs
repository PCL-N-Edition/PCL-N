using Nexa.Services.Capabilities;
using Nexa.Services.Minecraft.Launch;

namespace Nexa.Services.Minecraft.Process;

/// <summary>Per-launch JVM host capabilities. These do not belong in the machine snapshot.</summary>
public static class JvmHostCapabilityCatalog
{
    private const string Provider = "nexa.jvmhost";
    public static readonly CapabilityDefinition<string> JvmArguments = Fact<string>("jvmhost.environment.jvm_args", "JVM 参数");
    public static readonly CapabilityDefinition<string> Classpath = Fact<string>("jvmhost.environment.classpath", "Classpath");
    public static readonly CapabilityDefinition<string> NativePath = Fact<string>("jvmhost.environment.native_path", "本机库路径");
    public static readonly CapabilityDefinition<string> Wrapper = Fact<string>("jvmhost.environment.wrapper", "启动包装器");
    public static readonly CapabilityDefinition<bool> Spawn = Action("jvmhost.process.spawn", "启动进程");
    public static readonly CapabilityDefinition<bool> KillTree = Action("jvmhost.process.kill_tree", "结束进程树");
    public static readonly CapabilityDefinition<bool> Suspend = Action("jvmhost.process.suspend", "暂停进程");
    public static readonly CapabilityDefinition<bool> Priority = Action("jvmhost.process.priority", "进程优先级");
    public static readonly CapabilityDefinition<bool> Affinity = Action("jvmhost.process.affinity", "处理器亲和性");
    public static readonly CapabilityDefinition<bool> Qos = Action("jvmhost.process.qos", "服务质量");
    public static readonly CapabilityDefinition<bool> Stdout = Fact<bool>("jvmhost.io.stdout", "标准输出");
    public static readonly CapabilityDefinition<bool> Stderr = Fact<bool>("jvmhost.io.stderr", "标准错误");
    public static readonly CapabilityDefinition<int> RingBuffer = Fact<int>("jvmhost.io.ring_buffer", "输出环形缓冲", "lines");
    public static readonly CapabilityDefinition<bool> Timestamp = Fact<bool>("jvmhost.io.timestamp", "输出时间戳");
    public static readonly CapabilityDefinition<string> Encoding = Fact<string>("jvmhost.io.encoding", "输出编码");
    public static readonly CapabilityDefinition<bool> CpuMetric = Metric("jvmhost.metric.cpu", "CPU 观测");
    public static readonly CapabilityDefinition<bool> MemoryMetric = Metric("jvmhost.metric.memory", "内存观测");
    public static readonly CapabilityDefinition<bool> IoMetric = Metric("jvmhost.metric.io", "I/O 观测");
    public static readonly CapabilityDefinition<bool> ThreadMetric = Metric("jvmhost.metric.threads", "线程观测");
    public static readonly CapabilityDefinition<bool> GpuMetric = Metric("jvmhost.metric.gpu", "GPU 观测");
    public static readonly CapabilityDefinition<bool> ExitCode = Fact<bool>("jvmhost.crash.exit_code", "退出码");
    public static readonly CapabilityDefinition<bool> CrashReport = Fact<bool>("jvmhost.crash.crash_report", "崩溃报告");
    public static readonly CapabilityDefinition<bool> HsErr = Fact<bool>("jvmhost.crash.hs_err", "JVM 崩溃文件");
    public static readonly CapabilityDefinition<bool> StderrTail = Fact<bool>("jvmhost.crash.stderr_tail", "错误输出尾部");
    public static readonly CapabilityDefinition<bool> SystemCorrelation = Fact<bool>("jvmhost.crash.system_correlation", "系统事件关联");

    public static IReadOnlyList<ICapability> Describe(MinecraftLaunchPlan plan, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(plan);
        const string source = "JvmHostService";
        bool tunable = OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
        return Array.AsReadOnly<ICapability>(
        [
            JvmArguments.Observe(string.Join(' ', plan.Arguments), timestamp, source),
            Classpath.Observe(string.Join(Path.PathSeparator, plan.ClasspathEntries), timestamp, source),
            NativePath.Observe(plan.NativesDirectory, timestamp, source),
            Wrapper.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "未配置启动包装器"),
            Spawn.Observe(true, timestamp, source), KillTree.Observe(true, timestamp, source),
            Suspend.Observe(false, timestamp, source), Priority.Observe(tunable, timestamp, source),
            Affinity.Observe(tunable, timestamp, source), Qos.Observe(OperatingSystem.IsMacOS(), timestamp, source),
            Stdout.Observe(true, timestamp, source), Stderr.Observe(true, timestamp, source),
            RingBuffer.Observe(100, timestamp, source), Timestamp.Observe(true, timestamp, source),
            Encoding.Observe("UTF-8/system", timestamp, source), CpuMetric.Observe(true, timestamp, source),
            MemoryMetric.Observe(true, timestamp, source), IoMetric.Observe(false, timestamp, source),
            ThreadMetric.Observe(true, timestamp, source), GpuMetric.Observe(false, timestamp, source),
            ExitCode.Observe(true, timestamp, source), CrashReport.Observe(true, timestamp, source),
            HsErr.Observe(false, timestamp, source), StderrTail.Observe(true, timestamp, source),
            SystemCorrelation.Observe(false, timestamp, source),
        ]);
    }

    private static CapabilityDefinition<T> Fact<T>(string id, string label, string unit = "") =>
        new(id, label, "JVM Host", Provider, unit: unit);
    private static CapabilityDefinition<bool> Action(string id, string label) =>
        new(id, label, "JVM Host", Provider, CapabilityKind.Action);
    private static CapabilityDefinition<bool> Metric(string id, string label) =>
        new(id, label, "JVM Host", Provider, CapabilityKind.Metric, CapabilityStability.Dynamic);
}

public static class ObservationCapabilityCatalog
{
    private const string Provider = "nexa.jvmhost.observation";
    public static readonly CapabilityDefinition<long> LaunchDuration = Metric("observation.launch.duration", "启动耗时", "ms");
    public static readonly CapabilityDefinition<long> RuntimePeakWorkingSet = Metric("observation.runtime.peak_working_set", "峰值工作集", "bytes");
    public static readonly CapabilityDefinition<long> RuntimePeakPrivate = Metric("observation.runtime.peak_private", "峰值专用内存", "bytes");
    public static readonly CapabilityDefinition<long> RuntimePeakThreads = Metric("observation.runtime.peak_threads", "峰值线程数", "threads");
    public static readonly CapabilityDefinition<long> RuntimeCpu = Metric("observation.runtime.cpu", "CPU 时间", "ms");

    public static IReadOnlyList<ICapability> Project(JvmHostObservation observation, DateTimeOffset timestamp)
    {
        const string source = "JvmHostObservation";
        return Array.AsReadOnly<ICapability>(
        [
            LaunchDuration.Observe(observation.LaunchDurationMilliseconds, timestamp, source),
            RuntimePeakWorkingSet.Observe(observation.PeakWorkingSetBytes, timestamp, source),
            RuntimePeakPrivate.Observe(observation.PeakPrivateBytes, timestamp, source),
            RuntimePeakThreads.Observe(observation.PeakThreadCount, timestamp, source),
            RuntimeCpu.Observe(observation.TotalProcessorMilliseconds, timestamp, source),
        ]);
    }

    private static CapabilityDefinition<long> Metric(string id, string label, string unit) =>
        new(id, label, "运行观测", Provider, CapabilityKind.Metric, CapabilityStability.Dynamic, unit: unit);
}
