using System.IO.Pipes;
using System.Net.Sockets;
using PCL.Sidecar.Protocol;

namespace PCL.Sidecar.Transport;

/// <summary>
/// Accepts one Sidecar connection at a time over the platform local IPC: named pipes on Windows,
/// Unix-domain sockets elsewhere. The stream factories are the only OS-specific surface; the
/// protocol and session layers stay transport-agnostic.
/// </summary>
public sealed class SidecarIpcListener : IDisposable
{
    private readonly string _pipeName;
    private readonly object _gate = new();
    private NamedPipeServerStream? _windowsServer;
    private Socket? _unixSocket;
    private bool _disposed;

    private SidecarIpcListener(string pipeName)
    {
        _pipeName = pipeName;
    }

    public static bool IsSupported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>
    /// Binds a listener. The pipe name is the endpoint both sides agree on.
    /// </summary>
    public static SidecarIpcListener Bind(string pipeName)
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("No local IPC transport on this platform.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        SidecarIpcListener listener = new(pipeName);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            listener.BindUnix();
        }

        return listener;
    }

    /// <summary>
    /// Accepts one connection. On Windows a fresh pipe instance is created per accept.
    /// </summary>
    public async ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (OperatingSystem.IsWindows())
        {
            NamedPipeServerStream pipe = CreateWindowsPipe();
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            return pipe;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            Socket accepted = await _unixSocket!.AcceptAsync(cancellationToken).ConfigureAwait(false);
            return new NetworkStream(accepted, ownsSocket: true);
        }

        throw new PlatformNotSupportedException("No local IPC transport on this platform.");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (OperatingSystem.IsWindows())
        {
            _windowsServer?.Dispose();
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            _unixSocket?.Dispose();
            try
            {
                if (File.Exists(_pipeName))
                {
                    File.Delete(_pipeName);
                }
            }
            catch (IOException)
            {
                // The socket file is best-effort cleanup.
            }
        }
    }

    private void BindUnix()
    {
        try
        {
            if (File.Exists(_pipeName))
            {
                File.Delete(_pipeName);
            }
        }
        catch (IOException)
        {
        }

        _unixSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _unixSocket.Bind(new UnixDomainSocketEndPoint(_pipeName));
        _unixSocket.Listen(1);
    }

    private NamedPipeServerStream CreateWindowsPipe()
    {
        lock (_gate)
        {
            _windowsServer?.Dispose();
            _windowsServer = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            return _windowsServer;
        }
    }
}

/// <summary>
/// Connects to a Sidecar IPC listener.
/// </summary>
public static class SidecarIpcConnector
{
    public static async ValueTask<Stream> ConnectAsync(
        string pipeName,
        CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            NamedPipeClientStream pipe = new(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout: 10_000, cancellationToken).ConfigureAwait(false);
            return pipe;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            Socket socket = new(
                AddressFamily.Unix,
                SocketType.Stream,
                ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(pipeName), cancellationToken)
                .ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }

        throw new PlatformNotSupportedException("No local IPC transport on this platform.");
    }
}
