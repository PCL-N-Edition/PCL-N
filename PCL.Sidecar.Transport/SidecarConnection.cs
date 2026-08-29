using PCL.Sidecar.Protocol;

namespace PCL.Sidecar.Transport;

/// <summary>
/// One live Sidecar connection over a duplex stream: the frame transport plus an explicit,
/// observable lifecycle. Protocol failures move the connection to <see cref="SidecarConnectionState.Failed"/>
/// and every later operation throws; a graceful close moves it to
/// <see cref="SidecarConnectionState.Closed"/>. Reconnection is the session's job, not the
/// connection's.
/// </summary>
public sealed class SidecarConnection : IDisposable
{
    private readonly Stream _stream;
    private readonly SidecarFrameTransport _transport;
    private readonly object _gate = new();
    private SidecarConnectionState _state = SidecarConnectionState.Connected;
    private string? _failureReason;

    public SidecarConnection(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _transport = new SidecarFrameTransport(stream);
    }

    public SidecarConnectionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Gets why the connection failed, or null while it is alive.
    /// </summary>
    public string? FailureReason
    {
        get
        {
            lock (_gate)
            {
                return _failureReason;
            }
        }
    }

    /// <summary>
    /// Writes one frame. Throws on a closed or failed connection.
    /// </summary>
    public ValueTask SendAsync(SidecarFrame frame, CancellationToken cancellationToken = default)
    {
        ThrowIfNotUsable();
        return SendGuardedAsync(frame, cancellationToken);
    }

    /// <summary>
    /// Reads one frame. A protocol failure or stream end fails the connection and rethrows.
    /// </summary>
    public async ValueTask<SidecarFrame> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNotUsable();

        try
        {
            return await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fail(exception);
            throw;
        }
    }

    /// <summary>
    /// Closes the underlying stream and marks the connection closed. Idempotent.
    /// </summary>
    public void Close()
    {
        lock (_gate)
        {
            if (_state == SidecarConnectionState.Connected)
            {
                _state = SidecarConnectionState.Closed;
            }
        }

        _stream.Close();
    }

    public void Dispose() => Close();

    private async ValueTask SendGuardedAsync(SidecarFrame frame, CancellationToken cancellationToken)
    {
        try
        {
            await _transport.SendAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fail(exception);
            throw;
        }
    }

    private void Fail(Exception exception)
    {
        lock (_gate)
        {
            if (_state == SidecarConnectionState.Connected)
            {
                _state = SidecarConnectionState.Failed;
                _failureReason = exception.Message;
            }
        }

        _stream.Close();
    }

    private void ThrowIfNotUsable()
    {
        SidecarConnectionState state = State;
        if (state == SidecarConnectionState.Closed)
        {
            throw new InvalidOperationException("The sidecar connection is closed.");
        }

        if (state == SidecarConnectionState.Failed)
        {
            throw new InvalidOperationException(
                $"The sidecar connection failed: {FailureReason ?? "unknown reason"}.");
        }
    }
}
