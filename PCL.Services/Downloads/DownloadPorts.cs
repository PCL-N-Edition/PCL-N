namespace PCL.Services.Downloads;

/// <summary>
/// One download connection: server communication for one source attempt. Readers pull bytes
/// into caller-provided buffers; a zero read means end of stream.
/// </summary>
public interface IDownloadConnection
{
    /// <summary>
    /// Starts the transfer from the requested offset, returning the negotiated range.
    /// </summary>
    ValueTask<DownloadConnectionInfo> StartAsync(
        long beginOffset,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops server communication and releases resources.
    /// </summary>
    ValueTask StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads into the caller's buffer. Zero means end of stream.
    /// </summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
}

/// <summary>
/// One download connection that also accepts explicit byte-range requests for parallel
/// segmented transfers. Range requests return the negotiated range; a server-side mismatch is
/// a transfer failure.
/// </summary>
public interface ISegmentedDownloadConnection : IDownloadConnection
{
    /// <summary>
    /// Starts one segment covering <paramref name="beginOffset"/> through
    /// <paramref name="endOffset"/> inclusive.
    /// </summary>
    ValueTask<DownloadConnectionInfo> StartSegmentAsync(
        long beginOffset,
        long endOffset,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One transfer destination writer. Writers own resume semantics: existing bytes survive a
/// restart, offset zero restarts from scratch, and finishing commits the destination.
/// </summary>
public interface IDownloadWriter
{
    bool IsSupportParallel { get; }

    /// <summary>
    /// How many bytes already exist at the destination and can be resumed from.
    /// </summary>
    long ExistingLength { get; }

    /// <summary>
    /// Opens the destination for writing from the given offset.
    /// </summary>
    ValueTask<Stream> CreateStreamAsync(long startOffset, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops writing without committing.
    /// </summary>
    ValueTask StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits the transfer so the destination becomes the final file.
    /// </summary>
    ValueTask FinishAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// One download request: ordered failover sources, one destination, and the ports that
/// produce connections and writers. Sources are tried in order until one completes.
/// </summary>
public sealed record DownloadRequest
{
    public required IReadOnlyList<string> Sources { get; init; }

    public required string DestinationPath { get; init; }

    public required Func<string, IDownloadConnection?> ConnectionFactory { get; init; }

    public int MaxParallelSegments { get; init; } = 1;

    public Func<string, IDownloadWriter?> WriterFactory { get; init; } =
        static path => new FileDownloadWriter(path);
}
