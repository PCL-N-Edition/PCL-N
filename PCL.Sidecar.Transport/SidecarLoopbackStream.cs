namespace PCL.Sidecar.Transport;

/// <summary>
/// One end of an in-memory duplex pipe. Bytes written on one end are read from the other;
/// closing an end ends the peer's stream after its buffered bytes are consumed. Writes never
/// block and the pipe is unbounded — it exists for tests and in-process hosts.
/// </summary>
public sealed class SidecarLoopbackStream : Stream
{
    private readonly SidecarLoopbackBuffer _readBuffer = new();
    private SidecarLoopbackBuffer? _peerWriteBuffer;
    private bool _closed;

    private SidecarLoopbackStream()
    {
    }

    /// <summary>
    /// Creates one connected loopback pair.
    /// </summary>
    public static (SidecarLoopbackStream First, SidecarLoopbackStream Second) CreatePair()
    {
        SidecarLoopbackStream first = new();
        SidecarLoopbackStream second = new();
        first._peerWriteBuffer = second._readBuffer;
        second._peerWriteBuffer = first._readBuffer;
        return (first, second);
    }

    internal SidecarLoopbackBuffer ReadBuffer => _readBuffer;

    public override bool CanRead => !_closed;

    public override bool CanWrite => !_closed;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        Validate(buffer, offset, count);
        return _readBuffer.Read(buffer.AsMemory(offset, count));
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) => _readBuffer.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
    {
        Validate(buffer, offset, count);
        ThrowIfClosed();
        WritePeer(buffer.AsSpan(offset, count));
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        WritePeer(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override void Close()
    {
        _closed = true;
        // Closing one end ends both directions: the peer's reads hit EOF, and this end's reads
        // return EOF after any buffered bytes.
        _peerWriteBuffer?.CloseWriter();
        _readBuffer.CloseWriter();
    }

    protected override void Dispose(bool disposing)
    {
        _closed = true;
        _peerWriteBuffer?.CloseWriter();
        _readBuffer.CloseWriter();
        base.Dispose(disposing);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private void WritePeer(ReadOnlySpan<byte> bytes)
    {
        if (_peerWriteBuffer is not { } peer)
        {
            throw new InvalidOperationException("The loopback stream is not connected to a peer.");
        }

        peer.Write(bytes);
    }

    private void ThrowIfClosed()
    {
        if (_closed)
        {
            throw new InvalidOperationException("The loopback stream end is closed.");
        }
    }

    private static void Validate(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + count, buffer.Length);
    }

    /// <summary>
    /// One unbounded FIFO of bytes with an async wait when empty.
    /// </summary>
    internal sealed class SidecarLoopbackBuffer
    {
        private readonly Queue<byte> _bytes = new();
        private readonly object _gate = new();
        private TaskCompletionSource? _readWaiter;
        private bool _writerClosed;

        public void Write(ReadOnlySpan<byte> bytes)
        {
            TaskCompletionSource? waiter;
            lock (_gate)
            {
                foreach (byte value in bytes)
                {
                    _bytes.Enqueue(value);
                }

                waiter = TakeWaiter();
            }

            waiter?.TrySetResult();
        }

        public int Read(Memory<byte> target)
        {
            lock (_gate)
            {
                int read = Math.Min(target.Length, _bytes.Count);
                for (int index = 0; index < read; index++)
                {
                    target.Span[index] = _bytes.Dequeue();
                }

                return read;
            }
        }

        public async ValueTask<int> ReadAsync(Memory<byte> target, CancellationToken cancellationToken)
        {
            while (true)
            {
                TaskCompletionSource? wait;
                int read;
                lock (_gate)
                {
                    read = Math.Min(target.Length, _bytes.Count);
                    for (int index = 0; index < read; index++)
                    {
                        target.Span[index] = _bytes.Dequeue();
                    }

                    wait = read == 0 && !_writerClosed ? AttachWaiter() : null;
                }

                if (read > 0)
                {
                    return read;
                }

                lock (_gate)
                {
                    if (_writerClosed && _bytes.Count == 0)
                    {
                        return 0;
                    }
                }

                if (wait is not null)
                {
                    await wait.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public void CloseWriter()
        {
            TaskCompletionSource? waiter;
            lock (_gate)
            {
                _writerClosed = true;
                waiter = TakeWaiter();
            }

            waiter?.TrySetResult();
        }

        private TaskCompletionSource? AttachWaiter()
        {
            _readWaiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _readWaiter;
        }

        private TaskCompletionSource? TakeWaiter()
        {
            TaskCompletionSource? waiter = _readWaiter;
            _readWaiter = null;
            return waiter;
        }
    }
}
