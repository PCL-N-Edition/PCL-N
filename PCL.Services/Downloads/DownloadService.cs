using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using PCL.Services.Logging;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Services.Downloads;

/// <summary>
/// Coordinates failover downloads while sharing one transfer per destination. Each caller
/// keeps an independent cancellation token and progress callback; the published
/// `download.transfers` collection mirrors every active transfer for local state readers.
/// Sources fail over in order with resume support; a 416 against a resumed destination
/// discards the partial file and retries the source list from scratch semantics.
/// </summary>
public sealed class DownloadService
{
    public const string OwnerName = "PCL.Services.Downloads";
    public const string LogModuleName = "Download";

    /// <summary>
    /// The ordered collection state key: items are <see cref="DownloadTransferView"/>, keyed
    /// by destination path. Only active transfers appear; terminal entries are removed.
    /// </summary>
    public static readonly XsrSemanticId TransfersKey = XsrSemanticId.Parse("download.transfers");

    private const int DefaultBufferSize = 128 * 1024;
    private const long DefaultMinimumSegmentBytes = 8 * 1024 * 1024;
    private const int MinBytesBetweenSegmentProgress = 1024 * 1024;
    private const int MaxStateConflicts = 8;

    private readonly ConcurrentDictionary<string, Lazy<DownloadOperation>> _active = new(GetPathComparer());
    private readonly int _bufferSize;
    private readonly long _minimumSegmentBytes;
    private readonly XsrStateStore _store;
    private readonly XsrStateId _transfersId;
    private readonly object _stateGate = new();
    private readonly LogService? _log;

    /// <summary>
    /// Two-phase composition, declaration phase: registers the active-transfers collection
    /// into the shared host builder.
    /// </summary>
    public static void DeclareState(XsrStateStoreBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Collection<DownloadTransferView, string>(
            TransfersKey,
            OwnerName,
            static view => view.DestinationPath);
    }

    public DownloadService(
        XsrStateStore store,
        int bufferSize = DefaultBufferSize,
        LogService? log = null,
        long minimumSegmentBytes = DefaultMinimumSegmentBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumSegmentBytes);

        _store = store ?? throw new ArgumentNullException(nameof(store));
        _bufferSize = bufferSize;
        _minimumSegmentBytes = minimumSegmentBytes;
        _log = log;
        _transfersId = _store.Resolve(TransfersKey);
    }

    public XsrStateStore StateStore => _store;

    public Task<DownloadTransferResult> DownloadAsync(
        DownloadRequest request,
        Action<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        string destinationPath = Path.GetFullPath(request.DestinationPath);
        _log?.Info(LogModuleName, $"Download requested destination={destinationPath} sources={request.Sources.Count}");
        Lazy<DownloadOperation> lazyOperation = _active.GetOrAdd(
            destinationPath,
            static (path, state) => new Lazy<DownloadOperation>(
                () => new DownloadOperation(
                    state.Request with { DestinationPath = path },
                    operation => state.Service.ExecuteAndCompleteAsync(path, operation)),
                LazyThreadSafetyMode.ExecutionAndPublication),
            (Service: this, Request: request));
        DownloadOperation operation = lazyOperation.Value;
        return operation.WaitAsync(progress, cancellationToken);
    }

    private async Task ExecuteAndCompleteAsync(string destinationPath, DownloadOperation operation)
    {
        using LogOperation? trace = _log?.BeginOperation(LogModuleName, "Transfer", $"destination={destinationPath}");
        int lastStage = -1;
        try
        {
            void Report(DownloadProgress progress)
            {
                if (Interlocked.Exchange(ref lastStage, (int)progress.Stage) != (int)progress.Stage)
                    trace?.Stage(progress.Stage.ToString(), $"source={DescribeSource(progress.Source)}");
                operation.Report(progress);
                UpsertView(new DownloadTransferView(
                    destinationPath,
                    progress.Stage,
                    progress.Source,
                    progress.DownloadedBytes,
                    progress.TotalBytes,
                    progress.BytesPerSecond));
            }

            DownloadTransferResult result = await DownloadCoreAsync(
                operation.Request,
                destinationPath,
                Report,
                operation.CancellationToken).ConfigureAwait(false);
            if (result.Success) trace?.Complete($"bytes={result.TotalBytes} failed_sources={result.Errors.Count}");
            else trace?.Reject("download.sources_exhausted");
            operation.SetResult(result);
        }
        catch (OperationCanceledException failure)
        {
            trace?.Cancel();
            operation.SetCanceled(failure.CancellationToken);
        }
        catch (Exception failure)
        {
            trace?.Fail(failure);
            operation.SetException(failure);
        }
        finally
        {
            RemoveView(destinationPath);
            if (_active.TryGetValue(destinationPath, out Lazy<DownloadOperation>? lazyOperation)
                && lazyOperation.IsValueCreated
                && ReferenceEquals(lazyOperation.Value, operation))
            {
                _active.TryRemove(new KeyValuePair<string, Lazy<DownloadOperation>>(destinationPath, lazyOperation));
            }

            operation.Dispose();
        }
    }

    private async Task<DownloadTransferResult> DownloadCoreAsync(
        DownloadRequest request,
        string destinationPath,
        Action<DownloadProgress> report,
        CancellationToken cancellationToken)
    {
        List<DownloadAttemptError> errors = [];
        long startedAt = Stopwatch.GetTimestamp();

        foreach (string source in request.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            IDownloadConnection? connection = null;
            IDownloadWriter? writer = null;
            long requestedOffset = 0;
            string stage = "connect";
            try
            {
                _log?.Debug(LogModuleName, $"Source attempt started source={DescribeSource(source)} destination={destinationPath} attempt={errors.Count + 1}");
                report(new DownloadProgress(DownloadStage.Connecting, source, 0, -1, 0));
                if (request.MaxParallelSegments > 1)
                {
                    stage = "segmented_transfer";
                    DownloadTransferResult? segmented = await TryDownloadSegmentedAsync(
                        request,
                        source,
                        destinationPath,
                        errors,
                        startedAt,
                        report,
                        cancellationToken).ConfigureAwait(false);
                    if (segmented is not null)
                    {
                        return segmented;
                    }
                }

                stage = "open_writer";
                writer = request.WriterFactory(destinationPath)
                    ?? throw new InvalidOperationException($"No download writer was created for {destinationPath}.");
                requestedOffset = Math.Max(0, writer.ExistingLength);
                _log?.Debug(LogModuleName, $"Resume check destination={destinationPath} existing_bytes={requestedOffset}");
                stage = "connect";
                connection = request.ConnectionFactory(source)
                    ?? throw new InvalidOperationException($"No download connection was created for {source}.");
                DownloadConnectionInfo connectionInfo = await connection
                    .StartAsync(requestedOffset, cancellationToken)
                    .ConfigureAwait(false);

                long startOffset = connectionInfo.BeginOffset == requestedOffset ? requestedOffset : 0;
                _log?.Debug(LogModuleName, $"Source connected source={DescribeSource(source)} begin_offset={connectionInfo.BeginOffset} length={connectionInfo.Length}");
                stage = "transfer";
                Stream writeStream = await writer
                    .CreateStreamAsync(startOffset, cancellationToken)
                    .ConfigureAwait(false);
                report(new DownloadProgress(
                    DownloadStage.Reading,
                    source,
                    startOffset,
                    Math.Max(connectionInfo.Length, startOffset),
                    0));

                byte[] buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
                try
                {
                    long readStartedAt = Stopwatch.GetTimestamp();
                    long sessionRead = 0;
                    long totalRead = startOffset;
                    while (true)
                    {
                        int read = await connection
                            .ReadAsync(buffer.AsMemory(0, _bufferSize), cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        await writeStream
                            .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                            .ConfigureAwait(false);
                        totalRead += read;
                        sessionRead += read;
                        report(new DownloadProgress(
                            DownloadStage.Downloading,
                            source,
                            totalRead,
                            Math.Max(connectionInfo.Length, totalRead),
                            CalculateSpeed(sessionRead, readStartedAt)));
                    }

                    stage = "flush";
                    await writeStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stage = "commit";
                    report(new DownloadProgress(DownloadStage.Committing, source, totalRead, totalRead, 0));
                    await writer.FinishAsync(cancellationToken).ConfigureAwait(false);
                    report(new DownloadProgress(DownloadStage.Completed, source, totalRead, totalRead, 0));
                    DownloadTransferResult result = new(
                        true,
                        destinationPath,
                        source,
                        totalRead,
                        Stopwatch.GetElapsedTime(startedAt),
                        errors.ToArray());
                    _log?.Write(LogLevel.Info, LogModuleName,
                        $"Download completed destination={destinationPath} source={DescribeSource(source)} bytes={totalRead} failed_sources={errors.Count}");
                    return result;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (OperationCanceledException)
            {
                _log?.Info(LogModuleName, $"Source attempt cancelled stage={stage} destination={destinationPath} source={DescribeSource(source)}");
                throw;
            }
            catch (Exception exception)
            {
                _log?.Write(LogLevel.Warn, LogModuleName,
                    $"Source attempt failed; checking failover stage={stage} destination={destinationPath} source={DescribeSource(source)} attempt={errors.Count + 1}", ExceptionDiagnostics.Describe(exception));
                if (ShouldDiscardPartialDownload(exception, requestedOffset))
                {
                    _log?.Warn(LogModuleName, $"Discarding invalid resume data destination={destinationPath} requested_offset={requestedOffset}");
                    await ResetPartialDownloadAsync(writer, cancellationToken).ConfigureAwait(false);
                }

                errors.Add(new DownloadAttemptError(source, exception.Message, exception));
                report(new DownloadProgress(DownloadStage.Retrying, source, 0, -1, 0));
            }
            finally
            {
                await StopWriterAsync(writer).ConfigureAwait(false);
                await StopConnectionAsync(connection).ConfigureAwait(false);
            }
        }

        report(new DownloadProgress(DownloadStage.Failed, string.Empty, 0, -1, 0));
        _log?.Write(LogLevel.Error, LogModuleName,
            $"All download sources failed destination={destinationPath} sources={request.Sources.Count} errors={errors.Count}");
        return new DownloadTransferResult(
            false,
            destinationPath,
            null,
            0,
            Stopwatch.GetElapsedTime(startedAt),
            errors.ToArray());
    }

    /// <summary>
    /// Attempts one segmented transfer against the source. Returns null when the source cannot
    /// serve parallel segments (non-segmented connection, server rejection, or a file too small
    /// to split), and the caller falls back to the single-stream path.
    /// </summary>
    private async Task<DownloadTransferResult?> TryDownloadSegmentedAsync(
        DownloadRequest request,
        string source,
        string destinationPath,
        List<DownloadAttemptError> errors,
        long startedAt,
        Action<DownloadProgress> report,
        CancellationToken cancellationToken)
    {
        IDownloadConnection? probeConnection = request.ConnectionFactory(source);
        if (probeConnection is not ISegmentedDownloadConnection probe)
        {
            await StopConnectionAsync(probeConnection).ConfigureAwait(false);
            return null;
        }

        long totalLength;
        try
        {
            DownloadConnectionInfo probeInfo = await probe
                .StartSegmentAsync(0, 0, cancellationToken)
                .ConfigureAwait(false);
            if (!probeInfo.IsSupportSegment || probeInfo.BeginOffset != 0 || probeInfo.EndOffset != 0
                || probeInfo.Length <= 0)
            {
                return null;
            }

            totalLength = probeInfo.Length;
        }
        finally
        {
            await StopConnectionAsync(probe).ConfigureAwait(false);
        }

        int segmentCount = Math.Min(
            request.MaxParallelSegments,
            Math.Max(1, (int)Math.Ceiling(totalLength / (double)_minimumSegmentBytes)));
        if (segmentCount <= 1)
        {
            return null;
        }

        string partPrefix = destinationPath + ".PCLSegment." + Guid.NewGuid().ToString("N");
        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        long[] segmentBytes = new long[segmentCount];
        long speedStartedAt = Stopwatch.GetTimestamp();
        long lastReportedBytes = 0;
        object progressGate = new();
        try
        {
            Task[] transfers = new Task[segmentCount];
            for (int index = 0; index < segmentCount; index++)
            {
                int segmentIndex = index;
                long begin = totalLength * segmentIndex / segmentCount;
                long end = totalLength * (segmentIndex + 1) / segmentCount - 1;
                string partPath = partPrefix + segmentIndex.ToString(CultureInfo.InvariantCulture);
                _log?.Write(LogLevel.RealTime, LogModuleName,
                    $"Segment started index={segmentIndex} range={begin}-{end} source={DescribeSource(source)}");
                transfers[segmentIndex] = DownloadSegmentAsync(
                    request,
                    source,
                    partPath,
                    segmentIndex,
                    begin,
                    end,
                    totalLength,
                    segmentBytes,
                    speedStartedAt,
                    report,
                    progressGate,
                    () => lastReportedBytes,
                    value => lastReportedBytes = value,
                    cancellationToken);
            }

            await Task.WhenAll(transfers).ConfigureAwait(false);
            _log?.Write(LogLevel.RealTime, LogModuleName,
                $"Segments ready for merge destination={destinationPath} segments={segmentCount}");

            IDownloadWriter writer = request.WriterFactory(destinationPath)
                ?? throw new InvalidOperationException($"No download writer was created for {destinationPath}.");
            try
            {
                Stream output = await writer.CreateStreamAsync(0, cancellationToken).ConfigureAwait(false);
                for (int index = 0; index < segmentCount; index++)
                {
                    await using FileStream input = File.OpenRead(partPrefix + index.ToString(CultureInfo.InvariantCulture));
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                report(new DownloadProgress(DownloadStage.Committing, source, totalLength, totalLength, 0));
                await writer.FinishAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await StopWriterAsync(writer).ConfigureAwait(false);
            }

            report(new DownloadProgress(DownloadStage.Completed, source, totalLength, totalLength, 0));
            _log?.Write(LogLevel.Info, LogModuleName,
                $"Segmented download completed destination={destinationPath} source={DescribeSource(source)} bytes={totalLength} segments={segmentCount}");
            return new DownloadTransferResult(
                true,
                destinationPath,
                source,
                totalLength,
                Stopwatch.GetElapsedTime(startedAt),
                errors.ToArray());
        }
        finally
        {
            for (int index = 0; index < segmentCount; index++)
            {
                try
                {
                    File.Delete(partPrefix + index.ToString(CultureInfo.InvariantCulture));
                }
                catch (IOException)
                {
                    // Part cleanup must not mask the transfer outcome.
                }
            }
        }
    }

    private async Task DownloadSegmentAsync(
        DownloadRequest request,
        string source,
        string partPath,
        int segmentIndex,
        long begin,
        long end,
        long totalLength,
        long[] segmentBytes,
        long speedStartedAt,
        Action<DownloadProgress> report,
        object progressGate,
        Func<long> getLastReportedBytes,
        Action<long> setLastReportedBytes,
        CancellationToken cancellationToken)
    {
        IDownloadConnection? connection = request.ConnectionFactory(source);
        if (connection is not ISegmentedDownloadConnection segmented)
        {
            await StopConnectionAsync(connection).ConfigureAwait(false);
            throw new InvalidOperationException("The download connection does not support segmented requests.");
        }

        try
        {
            DownloadConnectionInfo info = await segmented
                .StartSegmentAsync(begin, end, cancellationToken)
                .ConfigureAwait(false);
            if (info.BeginOffset != begin || info.EndOffset != end)
            {
                throw new IOException($"The server returned a wrong download segment: {info.BeginOffset}-{info.EndOffset}.");
            }

            await using FileStream target = new(
                partPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                _bufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
            try
            {
                long expected = end - begin + 1;
                while (segmentBytes[segmentIndex] < expected)
                {
                    int requested = (int)Math.Min(_bufferSize, expected - segmentBytes[segmentIndex]);
                    int read = await connection
                        .ReadAsync(buffer.AsMemory(0, requested), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    Interlocked.Add(ref segmentBytes[segmentIndex], read);
                    AggregateSegmentProgress(
                        segmentBytes,
                        totalLength,
                        speedStartedAt,
                        source,
                        report,
                        progressGate,
                        getLastReportedBytes,
                        setLastReportedBytes);
                }

                if (segmentBytes[segmentIndex] != expected)
                {
                    throw new EndOfStreamException($"The download segment is incomplete: {segmentBytes[segmentIndex]}/{expected}.");
                }

                _log?.Write(LogLevel.RealTime, LogModuleName,
                    $"Segment completed index={segmentIndex} bytes={expected} source={DescribeSource(source)}");
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            await StopConnectionAsync(segmented).ConfigureAwait(false);
        }
    }

    private static void AggregateSegmentProgress(
        long[] segmentBytes,
        long totalLength,
        long speedStartedAt,
        string source,
        Action<DownloadProgress> report,
        object progressGate,
        Func<long> getLastReportedBytes,
        Action<long> setLastReportedBytes)
    {
        lock (progressGate)
        {
            long downloaded = 0;
            for (int index = 0; index < segmentBytes.Length; index++)
            {
                downloaded += Interlocked.Read(ref segmentBytes[index]);
            }

            if (downloaded - getLastReportedBytes() < MinBytesBetweenSegmentProgress && downloaded != totalLength)
            {
                return;
            }

            setLastReportedBytes(downloaded);
            double elapsed = Stopwatch.GetElapsedTime(speedStartedAt).TotalSeconds;
            long speed = elapsed < 0.1 ? 0 : checked((long)(downloaded / elapsed));
            report(new DownloadProgress(DownloadStage.Downloading, source, downloaded, totalLength, speed));
        }
    }

    private void UpsertView(DownloadTransferView view)
    {
        lock (_stateGate)
        {
            for (int attempt = 0; attempt < MaxStateConflicts; attempt++)
            {
                XsrCollectionSnapshot<DownloadTransferView> snapshot =
                    _store.ReadCollection<DownloadTransferView>(_transfersId);
                XsrCollectionApplyResult result = _store.PublishDelta(
                    _transfersId,
                    new XsrCollectionDelta<DownloadTransferView, string>(snapshot.Revision, [view], []));
                if (result.IsApplied)
                {
                    return;
                }
            }
        }
    }

    private void RemoveView(string destinationPath)
    {
        lock (_stateGate)
        {
            for (int attempt = 0; attempt < MaxStateConflicts; attempt++)
            {
                XsrCollectionSnapshot<DownloadTransferView> snapshot =
                    _store.ReadCollection<DownloadTransferView>(_transfersId);
                List<string> removals = snapshot
                    .Items
                    .Where(view => string.Equals(view.DestinationPath, destinationPath, StringComparison.Ordinal))
                    .Select(view => view.DestinationPath)
                    .ToList();
                if (removals.Count == 0)
                {
                    return;
                }

                XsrCollectionApplyResult result = _store.PublishDelta(
                    _transfersId,
                    new XsrCollectionDelta<DownloadTransferView, string>(snapshot.Revision, [], removals));
                if (result.IsApplied)
                {
                    return;
                }
            }
        }
    }

    private static long CalculateSpeed(long bytes, long startedAt)
    {
        double elapsed = Stopwatch.GetElapsedTime(startedAt).TotalSeconds;
        return elapsed < 0.1 ? 0 : checked((long)(bytes / elapsed));
    }

    private static bool ShouldDiscardPartialDownload(Exception exception, long requestedOffset) =>
        requestedOffset > 0
        && exception is System.Net.Http.HttpRequestException { StatusCode: System.Net.HttpStatusCode.RequestedRangeNotSatisfiable };

    private static async ValueTask ResetPartialDownloadAsync(IDownloadWriter? writer, CancellationToken cancellationToken)
    {
        if (writer is null)
        {
            return;
        }

        Stream? resetStream = null;
        try
        {
            resetStream = await writer.CreateStreamAsync(0, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (resetStream is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                resetStream?.Dispose();
            }
        }
    }

    private static async ValueTask StopWriterAsync(IDownloadWriter? writer)
    {
        if (writer is null)
        {
            return;
        }

        try
        {
            await writer.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cleanup must not hide the original transfer outcome.
        }
    }

    private static async ValueTask StopConnectionAsync(IDownloadConnection? connection)
    {
        if (connection is null)
        {
            return;
        }

        try
        {
            await connection.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cleanup must not hide the original transfer outcome.
        }
    }

    private static void ValidateRequest(DownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);
        ArgumentNullException.ThrowIfNull(request.Sources);
        ArgumentNullException.ThrowIfNull(request.ConnectionFactory);
        ArgumentNullException.ThrowIfNull(request.WriterFactory);
        if (request.MaxParallelSegments <= 0)
        {
            throw new ArgumentException("MaxParallelSegments must be positive.", nameof(request));
        }

        if (request.Sources.Count == 0)
        {
            throw new ArgumentException("At least one download source is required.", nameof(request));
        }
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string DescribeSource(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri))
        {
            // Source paths, query strings and userinfo may carry signed credentials. The
            // destination identifies the artifact; the source only identifies the origin.
            return uri.IsDefaultPort ? $"{uri.Scheme}://{uri.IdnHost}" : $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}";
        }

        return "(non-uri source)";
    }

    private sealed class DownloadOperation(
        DownloadRequest request,
        Func<DownloadOperation, Task> start) : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource<DownloadTransferResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<long, Action<DownloadProgress>> _subscribers = [];
        private readonly object _sync = new();
        private readonly Func<DownloadOperation, Task> _start = start;
        private long _subscriberId;
        private int _waiterCount;
        private int _started;
        private bool _disposed;

        public DownloadRequest Request { get; } = request;

        public CancellationToken CancellationToken => _cancellation.Token;

        public async Task<DownloadTransferResult> WaitAsync(
            Action<DownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            long subscriberId = progress is null ? 0 : AddSubscriber(progress);
            Interlocked.Increment(ref _waiterCount);
            try
            {
                if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
                {
                    _ = _start(this);
                }

                return await _completion.Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (subscriberId != 0)
                {
                    RemoveSubscriber(subscriberId);
                }

                if (Interlocked.Decrement(ref _waiterCount) == 0 && !_completion.Task.IsCompleted)
                {
                    await _cancellation.CancelAsync().ConfigureAwait(false);
                }
            }
        }

        public void Report(DownloadProgress progress)
        {
            lock (_sync)
            {
                foreach (Action<DownloadProgress> subscriber in _subscribers.Values)
                {
                    try
                    {
                        subscriber(progress);
                    }
                    catch (Exception)
                    {
                        // Progress handlers must not terminate the transfer.
                    }
                }
            }
        }

        public void SetResult(DownloadTransferResult result) => _completion.TrySetResult(result);

        public void SetCanceled(CancellationToken cancellationToken) => _completion.TrySetCanceled(cancellationToken);

        public void SetException(Exception exception) => _completion.TrySetException(exception);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cancellation.Dispose();
        }

        private long AddSubscriber(Action<DownloadProgress> progress)
        {
            long id = Interlocked.Increment(ref _subscriberId);
            lock (_sync)
            {
                _subscribers.Add(id, progress);
            }

            return id;
        }

        private void RemoveSubscriber(long subscriberId)
        {
            lock (_sync)
            {
                _subscribers.Remove(subscriberId);
            }
        }
    }
}
