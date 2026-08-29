namespace PCL.Services.Downloads;

/// <summary>
/// Transfer lifecycle stages reported through progress and published state, ordered like the
/// legacy download engine's stages.
/// </summary>
public enum DownloadStage
{
    Connecting,
    Reading,
    Downloading,
    Committing,
    Retrying,
    Completed,
    Failed,
}

/// <summary>
/// One progress observation of an active transfer.
/// </summary>
public readonly record struct DownloadProgress(
    DownloadStage Stage,
    string Source,
    long DownloadedBytes,
    long TotalBytes,
    long BytesPerSecond);

/// <summary>
/// Connection handshake result: content length, the byte range the server will deliver, and
/// whether the server accepts segmented (range) requests.
/// </summary>
public readonly record struct DownloadConnectionInfo(
    long Length,
    long BeginOffset,
    long EndOffset,
    bool IsSupportSegment);

/// <summary>
/// One failed attempt against one source, retained in the transfer result.
/// </summary>
public sealed record DownloadAttemptError(string Source, string Message, Exception Failure);

/// <summary>
/// The final outcome of one download request.
/// </summary>
public sealed record DownloadTransferResult(
    bool Success,
    string DestinationPath,
    string? SuccessfulSource,
    long TotalBytes,
    TimeSpan Duration,
    IReadOnlyList<DownloadAttemptError> Errors);

/// <summary>
/// The published state view of one active transfer, keyed by destination path. This is what
/// the renderer reads; it never observes engine callbacks directly.
/// </summary>
public readonly record struct DownloadTransferView(
    string DestinationPath,
    DownloadStage Stage,
    string Source,
    long DownloadedBytes,
    long TotalBytes,
    long BytesPerSecond);
