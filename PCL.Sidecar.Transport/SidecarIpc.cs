using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using PCL.Sidecar.Protocol;

namespace PCL.Sidecar.Transport;

/// <summary>
/// Accepts one Sidecar connection at a time over the platform local IPC: named pipes on Windows,
/// Unix-domain sockets elsewhere. The stream factories are the only OS-specific surface; the
/// protocol and session layers stay transport-agnostic. On Unix the socket lives in a
/// randomized 0700 directory and the socket file itself is 0600 — same-user security is a
/// requirement, never a umask assumption.
/// </summary>
public sealed class SidecarIpcListener : IDisposable
{
    private readonly string _pipeName;
    private readonly string? _unixDirectory;
    private readonly object _gate = new();
    private NamedPipeServerStream? _windowsServer;
    private Socket? _unixSocket;
    private bool _disposed;

    private SidecarIpcListener(string pipeName, string? unixDirectory)
    {
        _pipeName = pipeName;
        _unixDirectory = unixDirectory;
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
        string? unixDirectory = null;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            unixDirectory = CreateGuardedUnixDirectory();
        }

        SidecarIpcListener listener = new(pipeName, unixDirectory);
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
                if (File.Exists(SocketPath))
                {
                    File.Delete(SocketPath);
                }

                if (_unixDirectory is not null && Directory.Exists(_unixDirectory))
                {
                    Directory.Delete(_unixDirectory);
                }
            }
            catch (IOException)
            {
                // The socket file and directory are best-effort cleanup.
            }
        }
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
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

        string socketPath = SocketPath;
        _unixSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _unixSocket.Bind(new UnixDomainSocketEndPoint(socketPath));
        _unixSocket.Listen(1);
        File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private string SocketPath => _unixDirectory is null ? _pipeName : Path.Combine(_unixDirectory, _pipeName);

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static string CreateGuardedUnixDirectory()
    {
        // A randomized 0700 directory per listener: even a world-writable temp root cannot let
        // another user reach or pre-empt the endpoint.
        string directory = Path.Combine(
            Path.GetTempPath(),
            "pcl-n-sidecar-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        File.SetUnixFileMode(
            directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return directory;
    }

    /// <summary>
    /// Gets the endpoint the connector dialls: the pipe name on Windows, the full socket path on
    /// Unix (inside the randomized 0700 directory).
    /// </summary>
    public string Endpoint => OperatingSystem.IsWindows() ? _pipeName : SocketPath;

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
