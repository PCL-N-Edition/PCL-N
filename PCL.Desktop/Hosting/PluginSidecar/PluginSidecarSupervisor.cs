// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using PCL.Core.Logging;
using PCL.Platform.Paths;

// DefaultPlatformPathProvider lives in PCL.Platform

namespace PCL.Desktop.Hosting.PluginSidecar;

/// <summary>
/// Spawns and supervises the CoreCLR plugin sidecar. Host remains AOT-safe:
/// missing sidecar disables plugin features without failing the shell.
/// </summary>
internal sealed class PluginSidecarSupervisor : IAsyncDisposable
{
    public static PluginSidecarSupervisor Instance { get; } = new();

    private readonly object _gate = new();
    private Process? _process;
    private PluginSidecarClient? _client;
    private string? _pipeName;
    private string? _token;

    public bool IsAvailable
    {
        get
        {
            lock (_gate)
                return _client is { IsConnected: true } && _process is { HasExited: false };
        }
    }

    public PluginSidecarClient? Client
    {
        get
        {
            lock (_gate)
                return _client;
        }
    }

    /// <summary>Try start sidecar if binary exists; never throws into shell init.</summary>
    public async Task<bool> TryStartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_client is { IsConnected: true } && _process is { HasExited: false })
                return true;

            // Allow retry after failed start or crashed process.
            if (_process is not null || _client is not null)
            {
                _client = null;
                try { _process?.Dispose(); } catch { /* ignore */ }
                _process = null;
            }
        }

        string? executable = PluginSidecarPaths.ResolveExecutable();
        if (executable is null)
        {
            PortableLog.Info("PluginSidecar", "Sidecar binary not found; plugin platform disabled.");
            return false;
        }

        DefaultPlatformPathProvider paths = new();
        _pipeName = "pcl-n-plugin-" + Guid.NewGuid().ToString("N");
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        string pipePath = OperatingSystem.IsWindows()
            ? @"\\.\pipe\" + _pipeName
            : Path.Combine(paths.TemporaryDirectory, _pipeName + ".sock");

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                // Ensure parent dir for UDS path
                Directory.CreateDirectory(paths.TemporaryDirectory);
                if (File.Exists(pipePath))
                    File.Delete(pipePath);
            }

            ProcessStartInfo start = new()
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
                ArgumentList =
                {
                    "--pipe", _pipeName,
                    "--token", _token,
                    "--data", paths.ApplicationDataDirectory,
                    "--cache", paths.CacheDirectory
                }
            };
            start.Environment["PCL_PLUGIN_SIDECAR_TOKEN"] = _token;

            PortableLog.Info("PluginSidecar", $"Starting sidecar: {executable}");
            Process process = new() { StartInfo = start };
            if (!process.Start())
            {
                PortableLog.Warn("PluginSidecar", "Failed to start sidecar process.");
                return false;
            }

            _process = process;

            // Connect as client after a short delay so the server can listen.
            PluginSidecarClient client = new();
            Stream stream = await ConnectPipeAsync(_pipeName, cancellationToken).ConfigureAwait(false);
            await client.ConnectAsync(stream, cancellationToken).ConfigureAwait(false);

            PluginSidecarResult hello = await client.HelloAsync(_token, cancellationToken).ConfigureAwait(false);
            if (!hello.Ok)
            {
                PortableLog.Warn("PluginSidecar", "Sidecar hello rejected: " + (hello.Message ?? "unknown"));
                await client.DisposeAsync().ConfigureAwait(false);
                TryKill(process);
                return false;
            }

            string hostVersion = typeof(PluginSidecarSupervisor).Assembly.GetName().Version?.ToString() ?? "dev";
            await client.InitRuntimeAsync(
                    paths.ApplicationDataDirectory,
                    paths.CacheDirectory,
                    hostVersion,
                    cancellationToken)
                .ConfigureAwait(false);

            lock (_gate)
                _client = client;

            PortableLog.Info(
                "PluginSidecar",
                $"Sidecar ready (protocol={hello.ProtocolVersion}, version={hello.SidecarVersion ?? "?"}).");
            return true;
        }
        catch (Exception ex)
        {
            PortableLog.Warn("PluginSidecar", "Sidecar start failed: " + ex.Message);
            await DisposeAsync().ConfigureAwait(false);
            return false;
        }
    }

    private static async Task<Stream> ConnectPipeAsync(string pipeName, CancellationToken cancellationToken)
    {
        const int maxAttempts = 40;
        Exception? last = null;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    NamedPipeClientStream pipe = new(
                        ".",
                        pipeName,
                        PipeDirection.InOut,
                        PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                    return pipe;
                }

                // Unix domain socket
                string path = Path.Combine(Path.GetTempPath(), pipeName + ".sock");
                System.Net.Sockets.Socket socket = new(
                    System.Net.Sockets.AddressFamily.Unix,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Unspecified);
                await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(path), cancellationToken)
                    .ConfigureAwait(false);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or System.Net.Sockets.SocketException)
            {
                last = ex;
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException("Timed out connecting to plugin sidecar pipe.", last);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // ignore
        }
    }

    public async ValueTask DisposeAsync()
    {
        PluginSidecarClient? client;
        Process? process;
        lock (_gate)
        {
            client = _client;
            process = _process;
            _client = null;
            _process = null;
        }

        if (client is not null)
        {
            try
            {
                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
                await client.ShutdownAsync(cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }

            await client.DisposeAsync().ConfigureAwait(false);
        }

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    if (!process.WaitForExit(1500))
                        TryKill(process);
                }
            }
            catch
            {
                TryKill(process);
            }

            process.Dispose();
        }
    }
}
