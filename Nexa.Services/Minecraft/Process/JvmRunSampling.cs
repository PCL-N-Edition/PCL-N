using System.Globalization;

namespace Nexa.Services.Minecraft.Process;

/// <summary>Persisted options, not a claim that the running game applied them.</summary>
public sealed record JvmRunSettings(int RenderDistance = -1, int SimulationDistance = -1,
    int MaxFps = -1, int Graphics = -1, int Vsync = -1, int Fullscreen = -1)
{
    public static JvmRunSettings Read(string directory)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(directory, "options.txt"), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Bound actual bytes, including a file growing while it is read.
            byte[] bytes = new byte[65537];
            int length = 0, read;
            while (length < bytes.Length && (read = stream.Read(bytes.AsSpan(length))) > 0) length += read;
            return length > 65536 ? new() : Parse(System.Text.Encoding.UTF8.GetString(bytes, 0, length));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(); }
    }
    internal static JvmRunSettings Parse(string text)
    {
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n'))
        {
            int separator = line.IndexOf(':');
            if (separator < 1) continue;
            string key = line[..separator];
            if (key is not ("renderDistance" or "simulationDistance" or "maxFps" or "graphicsMode" or "enableVsync" or "fullscreen")) continue;
            string value = line[(separator + 1)..].Trim();
            int maximum = key is "enableVsync" or "fullscreen" ? 1 : key == "graphicsMode" ? 2 : key == "maxFps" ? 10000 : 256;
            int number = value == "true" ? 1 : value == "false" ? 0 : int.TryParse(value, CultureInfo.InvariantCulture, out int n) ? n : -1;
            values[key] = number >= 0 && number <= maximum ? number : -1;
        }
        return new(values.GetValueOrDefault("renderDistance", -1), values.GetValueOrDefault("simulationDistance", -1),
            values.GetValueOrDefault("maxFps", -1), values.GetValueOrDefault("graphicsMode", -1),
            values.GetValueOrDefault("enableVsync", -1), values.GetValueOrDefault("fullscreen", -1));
    }
}

public sealed record JvmRunSample(Guid SessionId, long Sequence, long ElapsedMilliseconds, long WindowMilliseconds,
    int ConfigurationEpoch, JvmRunSettings Settings, int JavaMajor, string Loader, int ClasspathCount,
    int HeapLimitMiB, long SampleCount, double WorkingMeanMiB, double WorkingPeakMiB,
    double PrivateMeanMiB, double CpuMeanPercent, int ThreadsPeak, bool Ended, int? ExitCode)
{
    public string Key => $"{SessionId:N}:{Sequence}";
}

/// <summary>Constant memory, whole-run histogram. P95 is an approximate bin upper bound.</summary>
internal sealed class RunResourceHistogram
{
    private readonly long[] _bins = new long[2048];
    private long _count;
    public long Peak { get; private set; }
    public void Add(long bytes)
    {
        if (bytes < 0) return;
        Peak = Math.Max(Peak, bytes);
        _bins[(int)Math.Min(bytes / (16L * 1024 * 1024), _bins.Length - 1)]++;
        _count++;
    }
    public long P95()
    {
        if (_count == 0) return 0;
        long seen = 0, target = (long)Math.Ceiling(_count * .95);
        for (int i = 0; i < _bins.Length; i++)
        {
            seen += _bins[i];
            if (seen >= target) return Math.Min(Peak, (i + 1L) * 16 * 1024 * 1024 >= 32768L * 1024 * 1024 ? Peak : (i + 1L) * 16 * 1024 * 1024);
        }
        return Peak;
    }
}

internal sealed class JvmRunWindow
{
    private long _count, _cpuCount;
    private double _working, _private, _cpu;
    private long _peak;
    private int _threads;
    public void Add(long working, long privateBytes, double? cpu, int threads)
    {
        _count++; _working += working / 1048576d; _private += privateBytes / 1048576d;
        _peak = Math.Max(_peak, working); _threads = Math.Max(_threads, threads);
        if (cpu is { } measured) { _cpu += measured; _cpuCount++; }
    }
    public JvmRunSample Finish(Guid session, long sequence, long elapsed, long duration, int epoch, JvmRunSettings settings,
        int java, string loader, int classpath, int heap, bool ended, int? exit)
    {
        var sample = new JvmRunSample(session, sequence, elapsed, duration, epoch, settings, java, loader, classpath, heap,
            _count, _count == 0 ? -1 : _working / _count, _count == 0 ? -1 : _peak / 1048576d,
            _count == 0 ? -1 : _private / _count, _cpuCount == 0 ? -1 : _cpu / _cpuCount,
            _count == 0 ? -1 : _threads, ended, exit);
        _count = _cpuCount = _peak = _threads = 0; _working = _private = _cpu = 0;
        return sample;
    }
}
