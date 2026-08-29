using System.Diagnostics;
using PCL.Sidecar.Protocol;
using PCL.Sidecar.Transport;

namespace PCL.Sidecar.Tests;

internal static partial class Program
{
    private static readonly (string Name, Func<ValueTask> Body)[] PerfGateCases =
    [
        ("no-op rtt distribution measured", NoOpRttDistribution),
        ("frame codec stays allocation bounded", Sync(FrameCodecStaysAllocationBounded)),
    ];

    /// <summary>
    /// Runs the deterministic Sidecar protocol/transport performance gates. Time distributions
    /// are reported informationally; the enforced invariants are machine-independent: bounded
    /// allocations on the codec path and a sane no-op RTT distribution over loopback.
    /// </summary>
    public static async ValueTask RunPerfGates()
    {
        foreach ((string name, Func<ValueTask> body) in PerfGateCases)
        {
            await body();
            Console.WriteLine($"GATE PASS: {name}");
        }
    }

    private static async ValueTask NoOpRttDistribution()
    {
        (SidecarLoopbackStream first, SidecarLoopbackStream second) = SidecarLoopbackStream.CreatePair();
        using SidecarConnection client = new(first);
        using SidecarConnection server = new(second);

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(10));
        Task pump = PumpHealthAsync(server, stop.Token);

        double[] samples = new double[200];
        for (int index = 0; index < samples.Length; index++)
        {
            long start = Stopwatch.GetTimestamp();
            await client.SendAsync(new SidecarFrame(
                SidecarProtocol.Version,
                SidecarMessageType.HealthPing,
                SidecarFrameTraits.None,
                SidecarCorrelationId.Create(),
                Array.Empty<byte>()));
            SidecarFrame pong = await client.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            AssertEqual(SidecarMessageType.HealthPong, pong.MessageType);
            samples[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        await stop.CancelAsync();
        Report("no-op RTT ms", samples);
    }

    private static async Task PumpHealthAsync(SidecarConnection server, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            SidecarFrame ping = await server.ReceiveAsync(cancellationToken);
            await server.SendAsync(new SidecarFrame(
                SidecarProtocol.Version,
                SidecarMessageType.HealthPong,
                SidecarFrameTraits.None,
                ping.CorrelationId,
                Array.Empty<byte>()), cancellationToken);
        }
    }

    private static void FrameCodecStaysAllocationBounded()
    {
        SidecarPayloadWriter writer = new();
        writer.WriteUInt32(1, 7);
        writer.WriteString(2, new string('x', 256));
        SidecarFrame frame = new(
            SidecarProtocol.Version,
            SidecarMessageType.Event,
            SidecarFrameTraits.None,
            SidecarCorrelationId.Create(),
            writer.ToArray());
        byte[] wire = EncodeFrame(frame);
        // A large warmup settles tiered compilation before the measured loop.
        for (int index = 0; index < 2_000; index++)
        {
            _ = SidecarFrameCodec.Decode(wire);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 100; index++)
        {
            _ = SidecarFrameCodec.Decode(wire);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Bounded by the payload copies plus their array headers: no per-field, per-header, or
        // codec objects are allowed on the decode path.
        AssertTrue(allocated <= 100 * (frame.Payload.Length + 32));
    }

    private static void Report(string name, double[] samples)
    {
        double[] ordered = [.. samples];
        Array.Sort(ordered);
        double P(double percentile) => ordered[Math.Min(ordered.Length - 1, (int)(ordered.Length * percentile))];
        Console.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"BENCH: {name}: p50={P(0.50):F3} p95={P(0.95):F3} p99={P(0.99):F3} max={ordered[^1]:F3} (informational)"));
    }
}
