using PCL.Services.Downloads;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Services.Tests;

// XSR-504: download capability contract — failover with resume, per-destination coalescing,
// stage progress, cancellation, and the published transfer state collection. Fake in-memory
// ports keep every scenario deterministic without network.
internal static partial class Program
{
    private sealed class FakeConnection : IDownloadConnection
    {
        private readonly byte[][] _chunks;
        private readonly long _declaredLength;
        private int _chunkIndex;

        public FakeConnection(long declaredLength, params byte[][] chunks)
        {
            _declaredLength = declaredLength;
            _chunks = chunks;
        }

        public bool WasStopped { get; private set; }

        public long StartOffset { get; private set; } = -1;

        public ValueTask<DownloadConnectionInfo> StartAsync(long beginOffset, CancellationToken cancellationToken = default)
        {
            StartOffset = beginOffset;
            return ValueTask.FromResult(new DownloadConnectionInfo(_declaredLength, beginOffset, _declaredLength - 1, false));
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (_chunkIndex >= _chunks.Length)
            {
                return 0;
            }

            byte[] chunk = _chunks[_chunkIndex++];
            chunk.CopyTo(buffer);
            return chunk.Length;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            WasStopped = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FlakyConnection : IDownloadConnection
    {
        public ValueTask<DownloadConnectionInfo> StartAsync(long beginOffset, CancellationToken cancellationToken = default) =>
            throw new IOException("simulated source outage");

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(0);

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    internal static ValueTask DownloadFailsOverAcrossSources()
    {
        string directory = CreateTempDirectory();
        try
        {
            string destination = Path.Combine(directory, "file.bin");
            DownloadService service = new();
            byte[][] second = [[0xAB], [0xCD]];
            DownloadTransferResult result = service.DownloadAsync(new DownloadRequest
            {
                Sources = ["mem://bad", "mem://good"],
                DestinationPath = destination,
                ConnectionFactory = source => source == "mem://bad" ? new FlakyConnection() : new FakeConnection(2, second),
            }).GetAwaiter().GetResult();

            AssertTrue(result.Success);
            AssertEqual("mem://good", result.SuccessfulSource);
            AssertEqual(2, result.TotalBytes);
            AssertEqual(1, result.Errors.Count);
            AssertEqual("mem://bad", result.Errors[0].Source);
            AssertTrue(File.ReadAllBytes(destination) is [0xAB, 0xCD]);
            AssertFalse(File.Exists(destination + ".PCLDownloading"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask DownloadFailsWhenEverySourceFails()
    {
        string directory = CreateTempDirectory();
        try
        {
            string destination = Path.Combine(directory, "file.bin");
            DownloadService service = new();
            DownloadTransferResult result = service.DownloadAsync(new DownloadRequest
            {
                Sources = ["mem://a", "mem://b"],
                DestinationPath = destination,
                ConnectionFactory = _ => new FlakyConnection(),
            }).GetAwaiter().GetResult();

            AssertFalse(result.Success);
            AssertNull(result.SuccessfulSource);
            AssertEqual(2, result.Errors.Count);
            AssertFalse(File.Exists(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask WriterTempFileSurvivesForResume()
    {
        string directory = CreateTempDirectory();
        try
        {
            string destination = Path.Combine(directory, "file.bin");
            string tempPath = destination + ".PCLDownloading";
            File.WriteAllBytes(tempPath, [0x11, 0x22, 0x33]);

            DownloadService service = new();
            FakeConnection connection = new(
                6,
                [0x44, 0x55, 0x66]);
            DownloadTransferResult result = service.DownloadAsync(new DownloadRequest
            {
                Sources = ["mem://a"],
                DestinationPath = destination,
                ConnectionFactory = _ => connection,
            }).GetAwaiter().GetResult();

            AssertTrue(result.Success);
            AssertEqual(3, connection.StartOffset);
            AssertEqual(6, result.TotalBytes);
            AssertTrue(File.ReadAllBytes(destination) is [0x11, 0x22, 0x33, 0x44, 0x55, 0x66]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask ConcurrentDownloadersShareOneTransfer()
    {
        string directory = CreateTempDirectory();
        try
        {
            string destination = Path.Combine(directory, "file.bin");
            DownloadService service = new();
            FakeConnection connection = new(2, [0x01], [0x02]);
            List<DownloadProgress> first = [];
            List<DownloadProgress> second = [];
            Task<DownloadTransferResult> taskA = service.DownloadAsync(new DownloadRequest
            {
                Sources = ["mem://a"],
                DestinationPath = destination,
                ConnectionFactory = _ => connection,
            }, first.Add);
            Task<DownloadTransferResult> taskB = service.DownloadAsync(new DownloadRequest
            {
                Sources = ["mem://ignored"],
                DestinationPath = destination,
                ConnectionFactory = _ => throw new InvalidOperationException("second caller must not open a connection"),
            }, second.Add);

            Task.WaitAll([taskA, taskB]);
            AssertTrue(taskA.Result.Success && taskB.Result.Success);
            AssertEqual("mem://a", taskB.Result.SuccessfulSource);
            AssertTrue(first.Count > 0 && second.Count > 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask ProgressStagesFlowInOrder()
    {
        string directory = CreateTempDirectory();
        try
        {
            string destination = Path.Combine(directory, "file.bin");
            DownloadService service = new();
            List<DownloadStage> stages = [];
            service.DownloadAsync(new DownloadRequest
            {
                Sources = ["mem://a"],
                DestinationPath = destination,
                ConnectionFactory = _ => new FakeConnection(2, [0x01], [0x02]),
            }, progress =>
            {
                if (stages.Count == 0 || stages[^1] != progress.Stage)
                {
                    stages.Add(progress.Stage);
                }
            }).GetAwaiter().GetResult();

            AssertTrue(stages.SequenceEqual(
                [DownloadStage.Connecting, DownloadStage.Reading, DownloadStage.Downloading, DownloadStage.Committing, DownloadStage.Completed]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask ThrowingProgressHandlerKeepsTransferAlive()
    {
        string directory = CreateTempDirectory();
        try
        {
            string destination = Path.Combine(directory, "file.bin");
            DownloadService service = new();
            DownloadTransferResult result = service.DownloadAsync(new DownloadRequest
            {
                Sources = ["mem://a"],
                DestinationPath = destination,
                ConnectionFactory = _ => new FakeConnection(1, [0x09]),
            }, _ => throw new InvalidOperationException("hostile progress handler")).GetAwaiter().GetResult();

            AssertTrue(result.Success);
            AssertTrue(File.ReadAllBytes(destination) is [0x09]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static async ValueTask CancellationRejectsTheTransfer()
    {
        string directory = CreateTempDirectory();
        try
        {
            string destination = Path.Combine(directory, "file.bin");
            DownloadService service = new();
            using CancellationTokenSource cancellation = new();
            Task<DownloadTransferResult> task = service.DownloadAsync(new DownloadRequest
            {
                Sources = ["mem://a"],
                DestinationPath = destination,
                ConnectionFactory = _ => new SlowConnection(),
            }, cancellationToken: cancellation.Token);

            cancellation.Cancel();
            bool cancelled = false;
            try
            {
                await task;
            }
            catch (TaskCanceledException)
            {
                cancelled = true;
            }

            AssertTrue(cancelled);
            AssertFalse(File.Exists(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static ValueTask SegmentedDownloadAssemblesParallelParts()
    {
        string directory = CreateTempDirectory();
        try
        {
            string destination = Path.Combine(directory, "file.bin");
            byte[] payload = new byte[100];
            for (int index = 0; index < payload.Length; index++)
            {
                payload[index] = (byte)(index * 3 + 1);
            }

            // 16-byte minimum segments turn 100 bytes into 7 logical segments capped at 4 parallel.
            DownloadService service = new(minimumSegmentBytes: 16);
            DownloadTransferResult result = service.DownloadAsync(new DownloadRequest
            {
                Sources = ["mem://fast"],
                DestinationPath = destination,
                ConnectionFactory = _ => new FakeSegmentedConnection(payload),
                MaxParallelSegments = 4,
            }).GetAwaiter().GetResult();

            AssertTrue(result.Success);
            AssertEqual("mem://fast", result.SuccessfulSource);
            AssertEqual(100, result.TotalBytes);
            AssertTrue(File.ReadAllBytes(destination).SequenceEqual(payload));
            AssertTrue(Directory.GetFiles(directory, "*.PCLSegment.*").Length == 0);
            AssertFalse(File.Exists(destination + ".PCLDownloading"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask SegmentedFallsBackToSingleStreamWhenUnsupported()
    {
        string directory = CreateTempDirectory();
        try
        {
            string destination = Path.Combine(directory, "file.bin");
            DownloadService service = new(minimumSegmentBytes: 16);
            DownloadTransferResult result = service.DownloadAsync(new DownloadRequest
            {
                Sources = ["mem://plain"],
                DestinationPath = destination,
                ConnectionFactory = _ => new FakeConnection(3, [0x01], [0x02], [0x03]),
                MaxParallelSegments = 4,
            }).GetAwaiter().GetResult();

            AssertTrue(result.Success);
            AssertTrue(File.ReadAllBytes(destination) is [0x01, 0x02, 0x03]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask SegmentedFallsBackForFilesBelowTheSegmentFloor()
    {
        string directory = CreateTempDirectory();
        try
        {
            string destination = Path.Combine(directory, "file.bin");
            // Segmented-capable connection, but the payload is far below the segment floor
            // configured here, so the engine must use the single-stream path.
            DownloadService service = new(minimumSegmentBytes: 1024 * 1024);
            DownloadTransferResult result = service.DownloadAsync(new DownloadRequest
            {
                Sources = ["mem://seg"],
                DestinationPath = destination,
                ConnectionFactory = _ => new FakeSegmentedConnection([0x0A, 0x0B, 0x0C]),
                MaxParallelSegments = 4,
            }).GetAwaiter().GetResult();

            AssertTrue(result.Success);
            AssertTrue(File.ReadAllBytes(destination) is [0x0A, 0x0B, 0x0C]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask SegmentedRangeMismatchFailsOverToNextSource()
    {
        string directory = CreateTempDirectory();
        try
        {
            string destination = Path.Combine(directory, "file.bin");
            byte[] payload = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88];
            DownloadService service = new(minimumSegmentBytes: 4);
            DownloadTransferResult result = service.DownloadAsync(new DownloadRequest
            {
                Sources = ["mem://liar", "mem://honest"],
                DestinationPath = destination,
                ConnectionFactory = source => source == "mem://liar"
                    ? new LyingSegmentedConnection(payload)
                    : new FakeSegmentedConnection(payload),
                MaxParallelSegments = 4,
            }).GetAwaiter().GetResult();

            AssertTrue(result.Success);
            AssertEqual("mem://honest", result.SuccessfulSource);
            AssertTrue(File.ReadAllBytes(destination).SequenceEqual(payload));
            AssertTrue(Directory.GetFiles(directory, "*.PCLSegment.*").Length == 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask SegmentedTruncatedSourceFailsOverToNextSource()
    {
        string directory = CreateTempDirectory();
        try
        {
            string destination = Path.Combine(directory, "file.bin");
            byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04];
            DownloadService service = new(minimumSegmentBytes: 4);
            DownloadTransferResult result = service.DownloadAsync(new DownloadRequest
            {
                Sources = ["mem://short", "mem://whole"],
                DestinationPath = destination,
                ConnectionFactory = source => source == "mem://short"
                    ? new TruncatedSegmentedConnection(payload)
                    : new FakeSegmentedConnection(payload),
                MaxParallelSegments = 4,
            }).GetAwaiter().GetResult();

            AssertTrue(result.Success);
            AssertEqual("mem://whole", result.SuccessfulSource);
            AssertEqual(1, result.Errors.Count);
            AssertEqual("mem://short", result.Errors[0].Source);
            AssertTrue(File.ReadAllBytes(destination).SequenceEqual(payload));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask SegmentedProgressReachesCompletedAtFullLength()
    {
        string directory = CreateTempDirectory();
        try
        {
            string destination = Path.Combine(directory, "file.bin");
            byte[] payload = new byte[64];
            DownloadService service = new(minimumSegmentBytes: 16);
            DownloadProgress? completed = null;
            List<DownloadProgress> downloading = [];
            service.DownloadAsync(new DownloadRequest
            {
                Sources = ["mem://fast"],
                DestinationPath = destination,
                ConnectionFactory = _ => new FakeSegmentedConnection(payload),
                MaxParallelSegments = 4,
            }, progress =>
            {
                if (progress.Stage == DownloadStage.Downloading)
                {
                    downloading.Add(progress);
                }
                else if (progress.Stage == DownloadStage.Completed)
                {
                    completed = progress;
                }
            }).GetAwaiter().GetResult();

            AssertTrue(completed is not null);
            AssertEqual(64, completed!.Value.DownloadedBytes);
            AssertEqual(64, completed.Value.TotalBytes);
            AssertTrue(downloading.All(progress => progress.TotalBytes == 64 && progress.DownloadedBytes <= 64));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private sealed class FakeSegmentedConnection(byte[] data) : ISegmentedDownloadConnection
    {
        private long _begin;
        private long _end = data.Length - 1;
        private long _served;

        public ValueTask<DownloadConnectionInfo> StartAsync(long beginOffset, CancellationToken cancellationToken = default)
        {
            _begin = beginOffset;
            _end = data.Length - 1;
            _served = 0;
            return ValueTask.FromResult(new DownloadConnectionInfo(data.Length, beginOffset, data.Length - 1, true));
        }

        public ValueTask<DownloadConnectionInfo> StartSegmentAsync(long beginOffset, long endOffset, CancellationToken cancellationToken = default)
        {
            _begin = beginOffset;
            _end = endOffset;
            _served = 0;
            return ValueTask.FromResult(new DownloadConnectionInfo(data.Length, beginOffset, endOffset, true));
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            long remaining = _end - _begin + 1 - _served;
            if (remaining <= 0)
            {
                return 0;
            }

            int take = (int)Math.Min(buffer.Length, Math.Min(remaining, 3));
            data.AsSpan((int)(_begin + _served), take).CopyTo(buffer.Span);
            _served += take;
            return take;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class LyingSegmentedConnection(byte[] data) : ISegmentedDownloadConnection
    {
        public ValueTask<DownloadConnectionInfo> StartAsync(long beginOffset, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DownloadConnectionInfo(data.Length, beginOffset, data.Length - 1, true));

        public ValueTask<DownloadConnectionInfo> StartSegmentAsync(long beginOffset, long endOffset, CancellationToken cancellationToken = default)
        {
            // Probe requests look healthy; only real segment requests report a wrong range.
            long offset = beginOffset == 0 && endOffset == 0 ? 0 : beginOffset + 1;
            return ValueTask.FromResult(new DownloadConnectionInfo(data.Length, offset, endOffset, true));
        }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(0);

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class TruncatedSegmentedConnection(byte[] data) : ISegmentedDownloadConnection
    {
        private long _end = -1;
        private long _served;

        public ValueTask<DownloadConnectionInfo> StartAsync(long beginOffset, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DownloadConnectionInfo(data.Length, beginOffset, data.Length - 1, true));

        public ValueTask<DownloadConnectionInfo> StartSegmentAsync(long beginOffset, long endOffset, CancellationToken cancellationToken = default)
        {
            _end = endOffset;
            _served = 0;
            return ValueTask.FromResult(new DownloadConnectionInfo(data.Length, beginOffset, endOffset, true));
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            // Serve at most half of each segment, then end the stream early.
            long expected = _end + 1;
            if (_served >= expected / 2 || buffer.Length == 0)
            {
                return 0;
            }

            buffer.Span[0] = 0xFF;
            _served++;
            return 1;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    internal static ValueTask TransferStateMirrorsActiveDownloads()
    {
        string directory = CreateTempDirectory();
        try
        {
            string destination = Path.Combine(directory, "file.bin");
            string other = Path.Combine(directory, "other.bin");
            DownloadService service = new();
            SlowConnection slowConnection = new([0x01, 0x02]);
            Task<DownloadTransferResult> slow = service.DownloadAsync(new DownloadRequest
            {
                Sources = ["mem://slow"],
                DestinationPath = destination,
                ConnectionFactory = _ => slowConnection,
            });
            DownloadTransferResult done = service.DownloadAsync(new DownloadRequest
            {
                Sources = ["mem://a"],
                DestinationPath = other,
                ConnectionFactory = _ => new FakeConnection(1, [0x05]),
            }).GetAwaiter().GetResult();
            AssertTrue(done.Success);

            WaitForActiveView(service, destination);
            XsrCollectionSnapshot<DownloadTransferView> state = service.StateStore.ReadCollection<DownloadTransferView>(
                service.StateStore.Resolve(DownloadService.TransfersKey));
            AssertTrue(state.Items.Any(view => view.DestinationPath == Path.GetFullPath(destination)));
            AssertTrue(state.Items.All(view => view.Stage
                is DownloadStage.Connecting or DownloadStage.Reading or DownloadStage.Downloading));

            slowConnection.Release();
            AssertTrue(slow.GetAwaiter().GetResult().Success);
            WaitForDrainedState(service);
            XsrCollectionSnapshot<DownloadTransferView> drained = service.StateStore.ReadCollection<DownloadTransferView>(
                service.StateStore.Resolve(DownloadService.TransfersKey));
            AssertEqual(0, drained.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask FileDownloadWriterRejectsShortResume()
    {
        string directory = CreateTempDirectory();
        try
        {
            string destination = Path.Combine(directory, "file.bin");
            FileDownloadWriter writer = new(destination);
            File.WriteAllBytes(writer.TempPath, [0x01]);
            bool threw = false;
            try
            {
                writer.CreateStreamAsync(5).AsTask().GetAwaiter().GetResult();
            }
            catch (IOException)
            {
                threw = true;
            }

            AssertTrue(threw);

            FileDownloadWriter restart = new(destination);
            AssertEqual(1, restart.ExistingLength);
            using (Stream stream = restart.CreateStreamAsync(1).AsTask().GetAwaiter().GetResult())
            {
                stream.Write(new byte[] { 0x02 });
            }

            restart.FinishAsync().AsTask().GetAwaiter().GetResult();
            AssertTrue(File.ReadAllBytes(destination) is [0x01, 0x02]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private sealed class SlowConnection : IDownloadConnection
    {
        private readonly byte[] _bytes;
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        public SlowConnection(byte[]? bytes = null)
        {
            _bytes = bytes ?? [0x01];
        }

        public ValueTask<DownloadConnectionInfo> StartAsync(long beginOffset, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DownloadConnectionInfo(_bytes.Length, beginOffset, _bytes.Length - 1, false));

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= _bytes.Length)
            {
                return 0;
            }

            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            buffer.Span[0] = _bytes[_position++];
            return 1;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            _gate.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public void Release() => _gate.TrySetResult();
    }

    private static void AssertNull(object? value)
    {
        if (value is not null)
        {
            throw new InvalidOperationException("Expected null but received a value.");
        }
    }

    private static void WaitForActiveView(DownloadService service, string destination)
    {
        XsrStateId id = service.StateStore.Resolve(DownloadService.TransfersKey);
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (service.StateStore.ReadCollection<DownloadTransferView>(id).Items.Any(
                view => view.DestinationPath == Path.GetFullPath(destination)))
            {
                return;
            }

            Thread.Sleep(10);
        }

        throw new InvalidOperationException("The active transfer view never appeared.");
    }

    private static void WaitForDrainedState(DownloadService service)
    {
        XsrStateId id = service.StateStore.Resolve(DownloadService.TransfersKey);
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (service.StateStore.ReadCollection<DownloadTransferView>(id).Count == 0)
            {
                return;
            }

            Thread.Sleep(10);
        }

        throw new InvalidOperationException("Terminal transfer views were never removed.");
    }
}
