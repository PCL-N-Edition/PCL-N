using PCL.Services.Logging;

namespace PCL.Services.Downloads;

/// <summary>
/// Writes a temporary file and atomically replaces the destination on finish, preserving any
/// existing temporary bytes for resume. This is the legacy file writer behavior: offset zero
/// restarts from scratch, offsets within the existing length truncate-append, and finishing
/// renames with bounded retries.
/// </summary>
public sealed class FileDownloadWriter : IDownloadWriter, IDisposable, IAsyncDisposable
{
    private const int RetryCount = 5;
    private const string LogModuleName = "Download";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly string _finalPath;
    private readonly string _tempPath;
    private readonly LogService? _log;
    private FileStream? _stream;

    public FileDownloadWriter(string finalPath, string tempExtension = ".PCLDownloading", LogService? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        ArgumentNullException.ThrowIfNull(tempExtension);

        _finalPath = Path.GetFullPath(finalPath);
        _tempPath = _finalPath + tempExtension;
        _log = log;
    }

    public bool IsSupportParallel => false;

    public long ExistingLength => File.Exists(_tempPath) ? new FileInfo(_tempPath).Length : 0L;

    public string TempPath => _tempPath;

    public async ValueTask<Stream> CreateStreamAsync(long startOffset, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        _log?.Debug(LogModuleName, $"Opening temporary download file path={_tempPath} offset={startOffset}");
        string? directory = Path.GetDirectoryName(_finalPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await DisposeStreamAsync().ConfigureAwait(false);
        if (startOffset == 0)
        {
            await RemoveTempFileAsync(cancellationToken).ConfigureAwait(false);
        }

        _stream = new FileStream(
            _tempPath,
            new FileStreamOptions
            {
                Mode = startOffset == 0 ? FileMode.Create : FileMode.OpenOrCreate,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });

        if (startOffset > 0)
        {
            if (_stream.Length < startOffset)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
                _stream = null;
                throw new IOException($"The temporary file is shorter than the resume offset: {_tempPath}");
            }

            _stream.SetLength(startOffset);
            _stream.Seek(startOffset, SeekOrigin.Begin);
        }

        return _stream;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default) =>
        await DisposeStreamAsync().ConfigureAwait(false);

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }

    public async ValueTask DisposeAsync() => await DisposeStreamAsync().ConfigureAwait(false);

    public async ValueTask FinishAsync(CancellationToken cancellationToken = default)
    {
        _log?.Debug(LogModuleName, $"Committing temporary download file destination={_finalPath}");
        await DisposeStreamAsync().ConfigureAwait(false);

        for (int retry = 1; ; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(_tempPath, _finalPath, overwrite: true);
                _log?.Debug(LogModuleName, $"Temporary download file committed destination={_finalPath} attempt={retry}");
                return;
            }
            catch (IOException) when (retry < RetryCount)
            {
                _log?.Write(LogLevel.RealTime, LogModuleName,
                    $"Temporary file rename failed; retrying attempt={retry}/{RetryCount} path={_tempPath}");
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                throw new IOException($"Unable to rename the temporary file: {_tempPath} -> {_finalPath}");
            }
        }
    }

    private async ValueTask DisposeStreamAsync()
    {
        if (_stream is null)
        {
            return;
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
        _stream = null;
    }

    private async ValueTask RemoveTempFileAsync(CancellationToken cancellationToken)
    {
        for (int retry = 1; retry <= RetryCount; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(_tempPath);
                return;
            }
            catch (IOException) when (retry < RetryCount)
            {
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
