using System.Collections.Frozen;

namespace Nexa.Services.Capabilities;

public sealed record ResourceObservationSample(
    string InstanceKey,
    string Loader,
    int JavaMajor,
    long ModCount,
    long ResourceWeight,
    long HeapPeakMiB,
    long NativePeakMiB,
    long PhysicalPeakMiB,
    long CommitPeakMiB,
    long GpuPeakMiB,
    long LaunchDurationMilliseconds,
    DateTimeOffset Timestamp)
{
    public string? ModFingerprint { get; init; }
}

public sealed class ResourceObservationHistory(int capacity = 128)
{
    private readonly object _gate = new();
    private readonly Queue<ResourceObservationSample> _samples = new();
    private readonly int _capacity = Math.Max(8, capacity);

    public void Record(ResourceObservationSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (_gate)
        {
            while (_samples.Count >= _capacity) _samples.Dequeue();
            _samples.Enqueue(sample);
        }
    }

    public IReadOnlyList<ResourceObservationSample> Snapshot()
    {
        lock (_gate) return Array.AsReadOnly(_samples.ToArray());
    }
}

internal sealed record ResourceEstimateComputation(
    ResourceEstimateStatus Status,
    CapabilityConfidence Confidence,
    double ConfidenceScore,
    double HistoricalWeight,
    IReadOnlyDictionary<string, long> LongValues,
    IReadOnlyDictionary<string, bool> BooleanValues,
    IReadOnlyDictionary<string, double> ScoreValues,
    IReadOnlyList<string> Reasons);

internal static class ResourceEstimateModel
{
    public static ResourceEstimateComputation Calculate(IEnumerable<ICapability> source,
        ResourceEstimatorProfile profile, ResourceObservationHistory history, string? instanceDirectory = null)
    {
        Dictionary<string, ICapability> values = source.ToDictionary(static value => value.Id, StringComparer.Ordinal);
        // The inventory total includes disabled files, which do not participate in the next launch.
        long modCount = ReadLong(values, Available(values, "mod.enabled") ? "mod.enabled" : "mod.count");
        long classCount = ReadLong(values, "mod.class.count");
        long resourceHeap = (long)Math.Ceiling(BytesToMiB(ReadLong(values, "resource.derived.steady_memory"))
            * profile.Coefficients.GetValueOrDefault("resource_weight", 1));
        long resourcePeak = BytesToMiB(ReadLong(values, "resource.derived.load_peak"));
        long sound = BytesToMiB(ReadLong(values, "resource.derived.sound_memory"));
        long models = BytesToMiB(ReadLong(values, "resource.derived.model_heap") + ReadLong(values, "resource.derived.blockstate_heap"));
        string loader = ReadString(values, "loader.type") ?? "Vanilla";
        string family = (ReadString(values, "minecraft.version.family") ?? "modern").ToLowerInvariant();
        long baseHeap = profile.MinecraftBaselineMiB.GetValueOrDefault(family,
            profile.MinecraftBaselineMiB.GetValueOrDefault("default", profile.HeapRuntimeMiB));
        long loaderHeap = profile.LoaderBaselineMiB.GetValueOrDefault(loader);
        long modHeap = (long)Math.Ceiling(modCount * profile.Coefficients.GetValueOrDefault("mod_count_mib", 5.5)
            + classCount * profile.Coefficients.GetValueOrDefault("class_count_mib", 0.012));
        long heapRuntime = Math.Max(profile.HeapRuntimeMiB, baseHeap + loaderHeap + modHeap + resourceHeap);
        long heapLaunch = Math.Max(heapRuntime, heapRuntime + (long)Math.Ceiling(resourcePeak
            * profile.Coefficients.GetValueOrDefault("resource_peak_launch", 0.5)));
        long heapHardMinimum = Math.Max(512, baseHeap / 2);
        long safeHeap = BytesToMiB(ReadLong(values, "memory.derived.safe_heap_max"));
        long physicalAvailable = BytesToMiB(ReadLong(values, "memory.physical.available"));
        if (safeHeap == 0)
            safeHeap = physicalAvailable > 0 ? Math.Max(heapHardMinimum,
                physicalAvailable - profile.SafetyMarginsMiB.GetValueOrDefault("system", 1024)) : heapLaunch;

        long metaspace = Math.Max(128, 96 + (long)Math.Ceiling(classCount
            * profile.Coefficients.GetValueOrDefault("metaspace_class_mib", 0.0125)));
        long codeCache = 128;
        long threads = Math.Max(64, ReadLong(values, "java.jvm.thread.estimated_count") * 2);
        long direct = Math.Max(128, BytesToMiB(ReadLong(values, "java.jvm.direct_buffer.estimate")));
        long gc = Math.Max(96, (long)Math.Ceiling(heapRuntime
            * profile.Coefficients.GetValueOrDefault("gc_heap_ratio", 0.0625)));
        long nativeMods = BytesToMiB(ReadLong(values, "java.jvm.native_mod.estimate"));
        long nativeRuntime = Math.Max(profile.NativeRuntimeMiB, metaspace + codeCache + threads + direct + gc + nativeMods);
        long nativeLaunch = Math.Max(profile.NativeLaunchMiB,
            nativeRuntime + profile.SafetyMarginsMiB.GetValueOrDefault("native_launch", 128));

        long texture = BytesToMiB(ReadLong(values, "resource.derived.texture_gpu_memory"));
        long shader = BytesToMiB(ReadLong(values, "resource.shader.estimated_gpu_memory"));
        long graphicsModels = Math.Max(64, (long)Math.Ceiling(models
            * profile.Coefficients.GetValueOrDefault("graphics_model_ratio", 0.5)));
        long renderer = profile.SafetyMarginsMiB.GetValueOrDefault("renderer", 256);
        long graphicsMargin = profile.SafetyMarginsMiB.GetValueOrDefault("graphics", 256);
        bool uma = ReadBool(values, "machine.gpu.unified") || ReadBool(values, "gpu.derived.uma");
        bool emulated = ReadBool(values, "platform.emulation.active");
        double hardwareCorrection = (uma ? profile.HardwareCorrections.GetValueOrDefault("uma", 1) : 1)
            * (emulated ? profile.HardwareCorrections.GetValueOrDefault("emulated", 1) : 1);
        long graphicsTotal = (long)Math.Ceiling((texture + shader + graphicsModels + renderer + graphicsMargin)
            * hardwareCorrection);
        long sharedGraphics = uma ? graphicsTotal : 0;
        long dedicatedAvailable = BytesToMiB(ReadLong(values, "gpu.memory.dedicated.available_budget"));
        long spill = dedicatedAvailable > 0 ? Math.Max(0, graphicsTotal - dedicatedAvailable) : sharedGraphics;

        long systemReserve = Math.Max(profile.SafetyMarginsMiB.GetValueOrDefault("system", 1024), profile.SafetyMarginMiB);
        long physicalLaunch = heapLaunch + nativeLaunch + resourcePeak + sharedGraphics + systemReserve;
        long physicalRuntime = heapRuntime + nativeRuntime + resourceHeap + sharedGraphics + systemReserve;
        long commitReserve = Math.Max(profile.SafetyMarginsMiB.GetValueOrDefault("commit", 512), profile.SafetyMarginMiB);
        long commitLaunch = heapLaunch + nativeLaunch + resourcePeak + commitReserve;
        long commitRuntime = heapRuntime + nativeRuntime + resourceHeap + commitReserve;

        string? scope = NormalizeInstance(instanceDirectory);
        string? fingerprint = ReadString(values, "mod.metadata.fingerprint");
        ResourceObservationSample[] samples = scope is null || string.IsNullOrEmpty(fingerprint) ? [] : history.Snapshot()
            .Where(item => string.Equals(item.ModFingerprint, fingerprint, StringComparison.Ordinal))
            .Where(item => string.Equals(NormalizeInstance(item.InstanceKey), scope,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToArray();
        double loaderSimilarity = samples.Length == 0 ? 0 : samples.Count(item => string.Equals(item.Loader, loader, StringComparison.OrdinalIgnoreCase)) / (double)samples.Length;
        double modSimilarity = samples.Length == 0 ? 0 : samples.Average(item => Similarity(modCount, item.ModCount));
        double resourceSimilarity = samples.Length == 0 ? 0 : samples.Average(item => Similarity(resourceHeap, item.ResourceWeight));
        double javaSimilarity = samples.Length == 0 ? 0 : samples.Average(item => item.JavaMajor == ReadLong(values, "java.runtime.major") ? 1d : 0.5d);
        double totalSimilarity = (loaderSimilarity + modSimilarity + resourceSimilarity + javaSimilarity) / 4;
        double historyWeight = Math.Clamp(samples.Length / 12d * totalSimilarity, 0, 0.75);
        long heapP95 = Percentile(samples.Select(static item => item.HeapPeakMiB), profile.QuantileTarget);
        long nativeP95 = Percentile(samples.Select(static item => item.NativePeakMiB), profile.QuantileTarget);
        long physicalP95 = Percentile(samples.Select(static item => item.PhysicalPeakMiB), profile.QuantileTarget);
        long gpuP95 = Percentile(samples.Select(static item => item.GpuPeakMiB), profile.QuantileTarget);
        long durationP95 = Percentile(samples.Select(static item => item.LaunchDurationMilliseconds), profile.QuantileTarget);
        long calibratedHeap = Blend(heapRuntime, heapP95, historyWeight);
        long calibratedNative = Blend(nativeRuntime, nativeP95, historyWeight);
        long calibratedGraphics = Blend(graphicsTotal, gpuP95, historyWeight);
        long calibratedPhysical = Blend(physicalRuntime, physicalP95, historyWeight);
        long calibratedCommit = Blend(commitRuntime,
            Percentile(samples.Select(static item => item.CommitPeakMiB), profile.QuantileTarget), historyWeight);

        bool unknownMods = !Available(values, "mod.runtime_profile.known") || modCount > 0 && !ReadBool(values, "mod.runtime_profile.known");
        bool modifiedCore = ReadBool(values, "minecraft.core.modified");
        bool unreadableSettings = !Available(values, "minecraft.settings.readable") || !ReadBool(values, "minecraft.settings.readable");
        bool missingHardware = !Available(values, "memory.physical.available") || !Available(values, "gpu.memory.dedicated.available_budget");
        bool insufficientHistory = samples.Length < 5;
        int penalties = new[] { unknownMods, modifiedCore, unreadableSettings, missingHardware, insufficientHistory }.Count(static value => value);
        double confidenceScore = Math.Clamp(1 - penalties * 0.16 + historyWeight * 0.3, 0.1, 1);
        CapabilityConfidence confidence = confidenceScore >= 0.8 ? CapabilityConfidence.High
            : confidenceScore >= 0.55 ? CapabilityConfidence.Medium : CapabilityConfidence.Low;
        List<string> reasons = [];
        if (unknownMods) reasons.Add("存在未知模组画像");
        if (modifiedCore) reasons.Add("游戏核心已修改");
        if (unreadableSettings) reasons.Add("无法读取游戏设置");
        if (missingHardware) reasons.Add("缺少硬件预算指标");
        if (insufficientHistory) reasons.Add("历史运行样本不足");
        if (reasons.Count == 0) reasons.Add("实例特征与历史样本完整");

        Dictionary<string, long> longs = new(StringComparer.Ordinal)
        {
            ["estimate.heap.base"] = baseHeap,
            ["estimate.heap.mods"] = modHeap,
            ["estimate.heap.mod_count"] = modCount,
            ["estimate.heap.resources"] = resourceHeap,
            ["estimate.heap.launch"] = heapLaunch,
            ["estimate.heap.runtime"] = heapRuntime,
            ["estimate.heap.hard_minimum"] = heapHardMinimum,
            ["estimate.heap.estimated_minimum"] = Math.Max(heapHardMinimum, (long)Math.Ceiling(heapRuntime
                * profile.Coefficients.GetValueOrDefault("heap_estimated_minimum_ratio", 0.75))),
            ["estimate.heap.recommended"] = calibratedHeap,
            ["estimate.heap.useful_maximum"] = (long)Math.Ceiling(heapRuntime
                * profile.Coefficients.GetValueOrDefault("heap_useful_maximum_ratio", 1.5)),
            ["estimate.heap.safe_maximum"] = Math.Max(heapHardMinimum, safeHeap),
            ["estimate.native.metaspace"] = metaspace,
            ["estimate.native.code_cache"] = codeCache,
            ["estimate.native.thread"] = threads,
            ["estimate.native.direct"] = direct,
            ["estimate.native.gc"] = gc,
            ["estimate.native.mods"] = nativeMods,
            ["estimate.native.launch"] = nativeLaunch,
            ["estimate.native.runtime"] = nativeRuntime,
            ["estimate.resource.heap"] = resourceHeap,
            ["estimate.resource.load_peak"] = resourcePeak,
            ["estimate.resource.runtime"] = resourceHeap,
            ["estimate.resource.sound"] = sound,
            ["estimate.resource.models"] = models,
            ["estimate.graphics.texture"] = texture,
            ["estimate.graphics.models"] = graphicsModels,
            ["estimate.graphics.shader"] = shader,
            ["estimate.graphics.renderer"] = renderer,
            ["estimate.graphics.margin"] = graphicsMargin,
            ["estimate.graphics.total"] = graphicsTotal,
            ["estimate.graphics.shared_system"] = sharedGraphics,
            ["estimate.graphics.vram_spill"] = spill,
            ["estimate.physical.launch"] = physicalLaunch,
            ["estimate.physical.runtime"] = physicalRuntime,
            ["estimate.physical.system_reserve"] = systemReserve,
            ["estimate.physical.launch_margin"] = Math.Max(0, BytesToMiB(ReadLong(values, "memory.physical.available")) - physicalLaunch),
            ["estimate.physical.runtime_margin"] = Math.Max(0, BytesToMiB(ReadLong(values, "memory.physical.available")) - physicalRuntime),
            ["estimate.commit.heap.launch"] = heapLaunch,
            ["estimate.commit.heap.runtime"] = heapRuntime,
            ["estimate.commit.nonheap.launch"] = nativeLaunch + resourcePeak,
            ["estimate.commit.nonheap.runtime"] = nativeRuntime + resourceHeap,
            ["estimate.commit.launch"] = commitLaunch,
            ["estimate.commit.runtime"] = commitRuntime,
            ["estimate.commit.reserve"] = commitReserve,
            ["estimate.commit.launch_margin"] = Math.Max(0, BytesToMiB(ReadLong(values, "memory.commit.available")) - commitLaunch),
            ["estimate.commit.runtime_margin"] = Math.Max(0, BytesToMiB(ReadLong(values, "memory.commit.available")) - commitRuntime),
            ["estimate.history.sample_count"] = samples.Length,
            ["estimate.history.heap.p95"] = heapP95,
            ["estimate.history.native.p95"] = nativeP95,
            ["estimate.history.physical.p95"] = physicalP95,
            ["estimate.history.gpu.p95"] = gpuP95,
            ["estimate.history.launch_time.p95"] = durationP95,
            ["estimate.calibrated.heap"] = calibratedHeap,
            ["estimate.calibrated.native"] = calibratedNative,
            ["estimate.calibrated.graphics"] = calibratedGraphics,
            ["estimate.calibrated.physical"] = calibratedPhysical,
            ["estimate.calibrated.commit"] = calibratedCommit,
        };
        Dictionary<string, bool> booleans = new(StringComparer.Ordinal)
        {
            ["estimate.confidence.reason.unknown_mods"] = unknownMods,
            ["estimate.confidence.reason.modified_core"] = modifiedCore,
            ["estimate.confidence.reason.unreadable_settings"] = unreadableSettings,
            ["estimate.confidence.reason.missing_hardware_metric"] = missingHardware,
            ["estimate.confidence.reason.insufficient_history"] = insufficientHistory,
            ["estimate.history.available"] = samples.Length > 0,
        };
        Dictionary<string, double> scores = new(StringComparer.Ordinal)
        {
            ["estimate.history.similarity.total"] = totalSimilarity,
            ["estimate.history.similarity.mods"] = modSimilarity,
            ["estimate.history.similarity.resources"] = resourceSimilarity,
            ["estimate.history.similarity.loader"] = loaderSimilarity,
            ["estimate.history.similarity.java"] = javaSimilarity,
            ["estimate.history.weight"] = historyWeight,
        };
        return new(ResourceEstimateStatus.Completed, confidence, confidenceScore, historyWeight,
            longs.ToFrozenDictionary(StringComparer.Ordinal), booleans.ToFrozenDictionary(StringComparer.Ordinal),
            scores.ToFrozenDictionary(StringComparer.Ordinal), reasons.AsReadOnly());
    }

    private static string? NormalizeInstance(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static bool Available(Dictionary<string, ICapability> values, string id) =>
        values.TryGetValue(id, out ICapability? value) && value.Availability == CapabilityAvailability.Available;
    private static long ReadLong(Dictionary<string, ICapability> values, string id) =>
        values.TryGetValue(id, out ICapability? value) && value.Availability == CapabilityAvailability.Available
            ? value switch { Capability<long> number => number.Value, Capability<int> number => number.Value, _ => 0 } : 0;
    private static bool ReadBool(Dictionary<string, ICapability> values, string id) =>
        values.TryGetValue(id, out ICapability? value) && value is Capability<bool> { Availability: CapabilityAvailability.Available, Value: true };
    private static string? ReadString(Dictionary<string, ICapability> values, string id) =>
        values.TryGetValue(id, out ICapability? value) && value is Capability<string> { Availability: CapabilityAvailability.Available } text ? text.Value : null;
    private static long BytesToMiB(long bytes) => Math.Max(0, bytes / (1024 * 1024));
    private static double Similarity(long left, long right) => left < 0 || right < 0 ? 0 : left == right ? 1 : 1 - Math.Min(1, Math.Abs(left - right) / (double)Math.Max(1, Math.Max(left, right)));
    private static long Blend(long model, long history, double weight) => history <= 0 ? model : (long)Math.Ceiling(model * (1 - weight) + history * weight);
    private static long Percentile(IEnumerable<long> source, double quantile)
    {
        // Zero denotes an unavailable host metric; it must not calibrate a real estimate down.
        long[] values = source.Where(static value => value > 0).Order().ToArray();
        double bounded = Math.Clamp(quantile, 0.5, 1);
        return values.Length == 0 ? 0 : values[(int)Math.Ceiling(values.Length * bounded) - 1];
    }
}
