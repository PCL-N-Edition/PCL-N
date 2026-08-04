// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using PCL.Core.Logging;
using PCL.Desktop.Paths;
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
        PluginSidecarClient? staleClient = null;
        Process? staleProcess = null;
        lock (_gate)
        {
            if (_client is { IsConnected: true } && _process is { HasExited: false })
                return true;

            // Allow retry after failed start, crashed process, or broken pipe (id desync).
            staleClient = _client;
            staleProcess = _process;
            _client = null;
            _process = null;
        }

        if (staleClient is not null || staleProcess is not null)
        {
            try
            {
                if (staleClient is not null)
                    await staleClient.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }

            if (staleProcess is not null)
            {
                TryKill(staleProcess);
                try { staleProcess.Dispose(); } catch { /* ignore */ }
            }
        }

        // Previous sessions can leave orphan sidecars that hold no pipe to us but confuse users.
        KillOrphanSidecars();

        string? executable = await PluginSidecarPaths.ResolveExecutableAsync(cancellationToken)
            .ConfigureAwait(false);
        if (executable is null)
        {
            PortableLog.Info("PluginSidecar", "Sidecar binary not found; plugin platform disabled.");
            return false;
        }

        DefaultPlatformPathProvider platformPaths = new();
        // Align plugin runtime with OOBE / pcln-paths.json data roots (not only OS defaults).
        string dataRoot = LauncherPathLayout.ResolveDataDirectory();
        string cacheRoot = LauncherPathLayout.ResolveCacheDirectory();
        string sidecarDataArg = ResolveSidecarDataArgument(dataRoot);
        string sidecarCacheArg = ResolveSidecarCacheArgument(cacheRoot);

        _pipeName = "pcl-n-plugin-" + Guid.NewGuid().ToString("N");
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        // macOS AF_UNIX sun_path is ~104 bytes; platform temp dirs can be very long.
        string unixSocketDirectory = OperatingSystem.IsWindows()
            ? platformPaths.TemporaryDirectory
            : "/tmp";
        string pipePath = OperatingSystem.IsWindows()
            ? @"\\.\pipe\" + _pipeName
            : Path.Combine(unixSocketDirectory, _pipeName + ".sock");

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(unixSocketDirectory);
                if (File.Exists(pipePath))
                    File.Delete(pipePath);
            }

            ProcessStartInfo start = new()
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
                ArgumentList =
                {
                    "--pipe", _pipeName,
                    "--token", _token,
                    "--data", sidecarDataArg,
                    "--cache", sidecarCacheArg
                }
            };
            start.Environment["PCL_PLUGIN_SIDECAR_TOKEN"] = _token;

            PortableLog.Info(
                "PluginSidecar",
                $"Starting sidecar: {executable}; data={sidecarDataArg}; cache={sidecarCacheArg}");
            Process process = new() { StartInfo = start };
            if (!process.Start())
            {
                PortableLog.Warn("PluginSidecar", "Failed to start sidecar process.");
                return false;
            }

            _process = process;
            process.ErrorDataReceived += static (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                    PortableLog.Warn("PluginSidecar", eventArgs.Data);
            };
            process.BeginErrorReadLine();

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
                    sidecarDataArg,
                    sidecarCacheArg,
                    hostVersion,
                    cancellationToken)
                .ConfigureAwait(false);

            lock (_gate)
                _client = client;

            if (client.ProtocolVersion >= PluginSidecarProtocolVersions.Current)
            {
                await PluginSidecarHostStateBridge.AttachAndSynchronizeAsync(client, cancellationToken)
                    .ConfigureAwait(false);
            }

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

                // Unix domain socket (prefer /tmp so paths stay under macOS sun_path limit)
                string path = Path.Combine(
                    OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() ? "/tmp" : Path.GetTempPath(),
                    pipeName + ".sock");
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

    /// <summary>
    /// Sidecar historically received OS ApplicationData and nested <c>PCL-N</c> itself.
    /// Host data dir is usually <c>…/PCL-N</c>; pass the parent in that case so plugin-runtime
    /// lands next to launcher-settings.json. Custom roots (no PCL-N name) are passed as-is.
    /// </summary>
    internal static string ResolveSidecarDataArgument(string launcherDataDirectory)
    {
        string full = Path.GetFullPath(launcherDataDirectory);
        string name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(name, "PCL-N", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(parent))
                return parent;
        }

        return full;
    }

    internal static string ResolveSidecarCacheArgument(string launcherCacheDirectory)
    {
        string full = Path.GetFullPath(launcherCacheDirectory);
        string name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(name, "PCL-N", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(parent))
                return parent;
        }

        return full;
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

    internal static void KillOrphanSidecars()
    {
        try
        {
            foreach (Process orphan in Process.GetProcessesByName("PCL.Plugin.Sidecar"))
            {
                try
                {
                    if (!orphan.HasExited)
                        orphan.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    orphan.Dispose();
                }
            }
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
                using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(500));
                await client.ShutdownAsync(cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // ignore — always dispose/kill below
            }

            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    // Do not hang host exit on a wedged sidecar.
                    if (!process.WaitForExit(400))
                        TryKill(process);
                }
            }
            catch
            {
                TryKill(process);
            }

            try { process.Dispose(); } catch { /* ignore */ }
        }

        // Sweep any orphaned sidecars from previous sessions (same user).
        KillOrphanSidecars();
    }
}
