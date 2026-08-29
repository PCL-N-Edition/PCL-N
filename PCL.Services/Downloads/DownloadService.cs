using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
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
    private const int MaxStateConflicts = 8;

    private readonly ConcurrentDictionary<string, Lazy<DownloadOperation>> _active = new(GetPathComparer());
    private readonly int _bufferSize;
    private readonly XsrStateStore _store;
    private readonly XsrStateId _transfersId;
    private readonly object _stateGate = new();
    private readonly LogService? _log;

    public DownloadService(int bufferSize = DefaultBufferSize, IXsrStateObserver? observer = null, LogService? log = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);

        _bufferSize = bufferSize;
        _log = log;
        XsrStateStoreBuilder builder = new();
        builder.Collection<DownloadTransferView, string>(
            TransfersKey,
            OwnerName,
            static view => view.DestinationPath);
        _store = builder.Build(observer);
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
        _log?.Write(LogLevel.Info, LogModuleName, $"提交下载任务；目标={destinationPath}；来源数={request.Sources.Count}。");
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
        try
        {
            void Report(DownloadProgress progress)
            {
                operation.Report(progress);
                UpsertView(new DownloadTransferView(
                    destinationPath,
                    progress.Stage,
                    progress.Source,
                    progress.DownloadedBytes,
                    progress.TotalBytes,
                    progress.BytesPerSecond));
            }

            operation.SetResult(await DownloadCoreAsync(
                operation.Request,
                destinationPath,
                Report,
                operation.CancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException failure)
        {
            operation.SetCanceled(failure.CancellationToken);
        }
        catch (Exception failure)
        {
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
            try
            {
                _log?.Write(LogLevel.Debug, LogModuleName, $"尝试下载来源；Source={DescribeSource(source)}；Destination={destinationPath}。");
                report(new DownloadProgress(DownloadStage.Connecting, source, 0, -1, 0));
                writer = request.WriterFactory(destinationPath)
                    ?? throw new InvalidOperationException($"No download writer was created for {destinationPath}.");
                requestedOffset = Math.Max(0, writer.ExistingLength);
                _log?.Write(LogLevel.Debug, LogModuleName, $"下载续传检查；Destination={destinationPath}；ExistingBytes={requestedOffset}。");
                connection = request.ConnectionFactory(source)
                    ?? throw new InvalidOperationException($"No download connection was created for {source}.");
                DownloadConnectionInfo connectionInfo = await connection
                    .StartAsync(requestedOffset, cancellationToken)
                    .ConfigureAwait(false);

                long startOffset = connectionInfo.BeginOffset == requestedOffset ? requestedOffset : 0;
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

                    await writeStream.FlushAsync(cancellationToken).ConfigureAwait(false);
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
                        $"下载完成；目标={destinationPath}；来源={DescribeSource(source)}；字节={totalRead}；耗时={result.Duration.TotalSeconds:0.###}s；失败来源={errors.Count}。");
                    return result;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (OperationCanceledException)
            {
                _log?.Write(LogLevel.Warn, LogModuleName, $"下载已取消；目标={destinationPath}；来源={DescribeSource(source)}。");
                throw;
            }
            catch (Exception exception)
            {
                _log?.Write(LogLevel.Warn, LogModuleName,
                    $"下载来源失败，将尝试下一来源；目标={destinationPath}；来源={DescribeSource(source)}；原因={exception.Message}。");
                if (ShouldDiscardPartialDownload(exception, requestedOffset))
                {
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
            $"所有下载来源均失败；目标={destinationPath}；来源数={request.Sources.Count}；错误数={errors.Count}。");
        return new DownloadTransferResult(
            false,
            destinationPath,
            null,
            0,
            Stopwatch.GetElapsedTime(startedAt),
            errors.ToArray());
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

        if (request.MaxParallelSegments > 1)
        {
            throw new NotSupportedException(
                "Segmented transfers are not available yet; MaxParallelSegments must be 1.");
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
            return uri.GetLeftPart(UriPartial.Path);
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
