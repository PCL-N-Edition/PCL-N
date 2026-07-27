// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCL.Core.Link.Scaffolding.Client.Models;
using PCL.Core.Logging;

namespace PCL.Application.Link;

public enum EasyTierState
{
    Stopped,
    Starting,
    Active,
    Ready
}

public sealed record EasyTierInstallProgress(
    string Stage,
    double Progress,
    string Detail);

public sealed record EasyTierPeer(
    string HostName,
    string Ip,
    double Ping,
    double Loss,
    string NatType,
    string Version)
{
    public bool IsScaffoldingHost =>
        HostName.StartsWith(EasyTierRuntime.ScaffoldingHostPrefix, StringComparison.Ordinal);
}

public sealed record EasyTierNatStatus(int UdpNatType, int TcpNatType, bool SupportsIpv6);

public sealed class EasyTierRuntime : IAsyncDisposable
{
    public const string CurrentVersion = "2.6.4";
    public const string ScaffoldingHostPrefix = "scaffolding-mc-server-";
    public const string HostVirtualAddress = "10.114.51.41";

    private static readonly string[] PublicRelays =
    [
        "tcp://public.easytier.top:11010",
        "tcp://public2.easytier.cn:54321",
        "https://etnode.zkitefly.eu.org/node1",
        "https://etnode.zkitefly.eu.org/node2",
        "https://etnode.zkitefly.eu.org/-node1",
        "https://etnode.zkitefly.eu.org/-node2"
    ];

    private readonly string _dataDirectory;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _installLock = new(1, 1);
    private Process? _process;
    private int _rpcPort;
    private string? _installDirectory;

    public EasyTierRuntime(string dataDirectory, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _ownsHttpClient = httpClient is null;
    }

    public EasyTierState State { get; private set; }

    public event Action<string>? OutputReceived;

    public event Action<int?>? ProcessExited;

    public async Task EnsureInstalledAsync(
        IProgress<EasyTierInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? existing = FindInstallDirectory();
            if (existing is not null)
            {
                _installDirectory = existing;
                progress?.Report(new EasyTierInstallProgress("Ready", 1, "EasyTier 组件已就绪"));
                return;
            }

            string assetName = GetAssetName();
            string versionDirectory = Path.Combine(_dataDirectory, "EasyTier", CurrentVersion);
            string archivePath = Path.Combine(versionDirectory, assetName);
            Directory.CreateDirectory(versionDirectory);
            progress?.Report(new EasyTierInstallProgress("Download", 0, $"正在下载 {assetName}"));

            Exception? lastException = null;
            foreach (string address in GetDownloadAddresses(assetName))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    PortableLog.Info("Terracotta", $"下载 EasyTier：{address}");
                    await DownloadAsync(address, archivePath, progress, cancellationToken).ConfigureAwait(false);
                    ValidateArchive(archivePath);
                    lastException = null;
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                    lastException = ex;
                    if (File.Exists(archivePath))
                        File.Delete(archivePath);
                    PortableLog.Warn(ex, "Terracotta", $"EasyTier 下载源不可用：{address}");
                }
            }

            if (lastException is not null)
                throw new IOException("所有 EasyTier 下载源均不可用。", lastException);

            progress?.Report(new EasyTierInstallProgress("Extract", 0.92, "正在解压 EasyTier"));
            ExtractArchive(archivePath, versionDirectory);
            File.Delete(archivePath);
            _installDirectory = FindInstallDirectory()
                ?? throw new InvalidDataException("EasyTier 压缩包不包含核心程序或命令行程序。");
            EnsureUnixExecutable(Path.Combine(_installDirectory, CoreExecutableName));
            EnsureUnixExecutable(Path.Combine(_installDirectory, CliExecutableName));
            progress?.Report(new EasyTierInstallProgress("Ready", 1, "EasyTier 组件安装完成"));
            PortableLog.Info("Terracotta", $"EasyTier {CurrentVersion} 安装完成：{_installDirectory}");
        }
        finally
        {
            _installLock.Release();
        }
    }

    public async Task LaunchAsync(
        LobbyInfo lobby,
        string machineId,
        int minecraftPort,
        int scaffoldingPort,
        bool asHost,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lobby);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        await EnsureInstalledAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await StopAsync().ConfigureAwait(false);

        _rpcPort = GetFreeTcpPort();
        ProcessStartInfo startInfo = CreateStartInfo(CoreExecutablePath);
        AddFlag(startInfo, "no-tun");
        AddFlag(startInfo, "multi-thread");
        AddFlag(startInfo, "enable-kcp-proxy");
        AddFlag(startInfo, "enable-quic-proxy");
        AddFlag(startInfo, "latency-first");
        AddOption(startInfo, "encryption-algorithm", "aes-gcm");
        AddOption(startInfo, "compression", "zstd");
        AddOption(startInfo, "default-protocol", "tcp");
        AddOption(startInfo, "network-name", lobby.NetworkName);
        AddOption(startInfo, "network-secret", lobby.NetworkSecret);
        AddOption(startInfo, "machine-id", machineId);
        AddOption(startInfo, "rpc-portal", _rpcPort.ToString(CultureInfo.InvariantCulture));
        AddOption(startInfo, "private-mode", "true");
        AddFlag(startInfo, "p2p-only");

        if (asHost)
        {
            AddShortOption(startInfo, "i", HostVirtualAddress);
            AddOption(startInfo, "hostname", ScaffoldingHostPrefix + scaffoldingPort.ToString(CultureInfo.InvariantCulture));
            AddOption(startInfo, "tcp-whitelist", scaffoldingPort.ToString(CultureInfo.InvariantCulture));
            AddOption(startInfo, "udp-whitelist", scaffoldingPort.ToString(CultureInfo.InvariantCulture));
            AddOption(startInfo, "tcp-whitelist", minecraftPort.ToString(CultureInfo.InvariantCulture));
            AddOption(startInfo, "udp-whitelist", minecraftPort.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            startInfo.ArgumentList.Add("-d");
            AddOption(startInfo, "hostname", Guid.NewGuid().ToString());
            AddOption(startInfo, "tcp-whitelist", "0");
            AddOption(startInfo, "udp-whitelist", "0");
        }

        AddShortOption(startInfo, "l", "tcp://0.0.0.0:0");
        AddShortOption(startInfo, "l", "udp://0.0.0.0:0");
        foreach (string relay in PublicRelays)
            AddShortOption(startInfo, "p", relay);

        Process process = new()
        {
            EnableRaisingEvents = true,
            StartInfo = startInfo
        };
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                PortableLog.Debug("EasyTier", args.Data);
                OutputReceived?.Invoke(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                PortableLog.Warn("EasyTier", args.Data);
                OutputReceived?.Invoke(args.Data);
            }
        };
        process.Exited += (_, _) =>
        {
            int? exitCode = null;
            try
            {
                exitCode = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                // Process disappeared before an exit code could be observed.
            }

            State = EasyTierState.Stopped;
            PortableLog.Warn(
                "Terracotta",
                $"EasyTier 进程已退出；ExitCode={exitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}");
            ProcessExited?.Invoke(exitCode);
        };

        State = EasyTierState.Starting;
        PortableLog.Info(
            "Terracotta",
            $"启动 EasyTier；Host={asHost}；Network={lobby.NetworkName}；RpcPort={_rpcPort}；ScaffoldingPort={scaffoldingPort}；MinecraftPort={minecraftPort}");
        PortableLog.Debug("Terracotta", BuildDiagnosticCommand(startInfo));
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("EasyTier 进程未能启动。");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            State = EasyTierState.Active;
        }
        catch
        {
            State = EasyTierState.Stopped;
            process.Dispose();
            throw;
        }
    }

    public async Task<IReadOnlyList<EasyTierPeer>> WaitForNetworkAsync(
        CancellationToken cancellationToken = default)
    {
        for (int attempt = 0; attempt < 300; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureProcessAlive();
            IReadOnlyList<EasyTierPeer> peers = await GetPeersAsync(cancellationToken).ConfigureAwait(false);
            EasyTierPeer? host = peers.FirstOrDefault(static peer => peer.IsScaffoldingHost);
            if (host is not null && host.Ping < 1000)
            {
                State = EasyTierState.Ready;
                PortableLog.Info("Terracotta", $"EasyTier 网络已就绪；Host={host.Ip}；Ping={host.Ping}ms");
                return peers;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("等待 EasyTier 网络就绪超时。");
    }

    public async Task<IReadOnlyList<EasyTierPeer>> GetPeersAsync(
        CancellationToken cancellationToken = default)
    {
        ProcessResult result = await RunCliAsync(
            ["--rpc-portal", $"127.0.0.1:{_rpcPort}", "-o", "json", "peer"],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            return [];

        try
        {
            EasyTierPeerDto[] peers = JsonSerializer.Deserialize(
                result.StandardOutput,
                EasyTierJsonContext.Default.EasyTierPeerDtoArray) ?? [];
            return peers.Select(static peer => new EasyTierPeer(
                    peer.HostName,
                    peer.Ipv4,
                    ParseNumber(peer.Ping),
                    ParseNumber(peer.Loss.Replace("%", "", StringComparison.Ordinal)),
                    peer.NatType,
                    peer.Version))
                .ToArray();
        }
        catch (JsonException ex)
        {
            PortableLog.Warn(ex, "Terracotta", "无法解析 EasyTier peer 输出。");
            PortableLog.Debug("Terracotta", result.StandardOutput);
            return [];
        }
    }

    public async Task<int> AddPortForwardAsync(
        string targetIp,
        int targetPort,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIp);
        if (targetPort is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(targetPort));

        int localPort = GetFreeTcpPort();
        foreach ((string protocol, string listenAddress) in new[]
                 {
                     ("tcp", $"127.0.0.1:{localPort}"),
                     ("udp", $"127.0.0.1:{localPort}"),
                     ("tcp", $"[::]:{localPort}"),
                     ("udp", $"[::]:{localPort}")
                 })
        {
            ProcessResult result = await RunCliAsync(
                [
                    "--rpc-portal", $"127.0.0.1:{_rpcPort}",
                    "port-forward", "add", protocol, listenAddress, $"{targetIp}:{targetPort}"
                ],
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                PortableLog.Warn(
                    "Terracotta",
                    $"EasyTier 添加 {protocol} 转发失败；Listen={listenAddress}；Target={targetIp}:{targetPort}；{result.StandardError}");
            }
        }

        PortableLog.Info("Terracotta", $"端口转发已建立：127.0.0.1:{localPort} -> {targetIp}:{targetPort}");
        return localPort;
    }

    public async Task<EasyTierNatStatus> TestNatAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInstalledAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        ProcessResult result = await RunCliProcessAsync(
            ["-o", "json", "stun"],
            requireRuntimeProcess: false,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"EasyTier NAT 测试失败：{result.StandardError}");

        EasyTierStunDto dto = JsonSerializer.Deserialize(
                                  result.StandardOutput,
                                  EasyTierJsonContext.Default.EasyTierStunDto)
                              ?? throw new InvalidDataException("EasyTier NAT 测试未返回有效结果。");
        return new EasyTierNatStatus(
            dto.UdpNatType,
            dto.TcpNatType,
            dto.PublicIps.Any(static address => address.Contains(':', StringComparison.Ordinal)));
    }

    public async Task StopAsync()
    {
        Process? process = Interlocked.Exchange(ref _process, null);
        if (process is null)
        {
            State = EasyTierState.Stopped;
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                PortableLog.Info("Terracotta", $"正在停止 EasyTier；PID={process.Id}");
                process.Kill(entireProcessTree: true);
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            PortableLog.Warn("Terracotta", "等待 EasyTier 退出超时。");
        }
        catch (InvalidOperationException)
        {
            // It has already exited.
        }
        finally
        {
            process.Dispose();
            State = EasyTierState.Stopped;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _installLock.Dispose();
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private string CoreExecutablePath =>
        Path.Combine(_installDirectory ?? throw new InvalidOperationException("EasyTier 尚未安装。"), CoreExecutableName);

    private string CliExecutablePath =>
        Path.Combine(_installDirectory ?? throw new InvalidOperationException("EasyTier 尚未安装。"), CliExecutableName);

    private static string CoreExecutableName => OperatingSystem.IsWindows() ? "easytier-core.exe" : "easytier-core";

    private static string CliExecutableName => OperatingSystem.IsWindows() ? "easytier-cli.exe" : "easytier-cli";

    private string? FindInstallDirectory()
    {
        string root = Path.Combine(_dataDirectory, "EasyTier", CurrentVersion);
        if (!Directory.Exists(root))
            return null;

        return Directory.EnumerateFiles(root, CoreExecutableName, SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .FirstOrDefault(directory =>
                directory is not null && File.Exists(Path.Combine(directory, CliExecutableName)));
    }

    private static string GetAssetName()
    {
        string os = OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsLinux()
                ? "linux"
                : OperatingSystem.IsMacOS()
                    ? "macos"
                    : throw new PlatformNotSupportedException("当前平台没有可用的 EasyTier 构建。");
        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 when OperatingSystem.IsWindows() => "arm64",
            Architecture.Arm64 => "aarch64",
            _ => throw new PlatformNotSupportedException(
                $"当前架构没有可用的 EasyTier 构建：{RuntimeInformation.ProcessArchitecture}")
        };
        return $"easytier-{os}-{architecture}-v{CurrentVersion}.zip";
    }

    private static IEnumerable<string> GetDownloadAddresses(string assetName)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return $"https://staticassets.naids.com/resources/pclce/static/easytier/{assetName}";
            yield return $"https://s3.pysio.online/pcl2-ce/static/easytier/{assetName}";
        }

        yield return $"https://github.com/EasyTier/EasyTier/releases/download/v{CurrentVersion}/{assetName}";
    }

    private async Task DownloadAsync(
        string address,
        string targetPath,
        IProgress<EasyTierInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        string temporaryPath = targetPath + ".download";
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, address);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PCL-N", "1"));
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength;
            {
                await using Stream input = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using FileStream output = new(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                byte[] buffer = new byte[128 * 1024];
                long written = 0;
                while (true)
                {
                    int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    written += read;
                    double downloadProgress = total is > 0 ? Math.Clamp((double)written / total.Value, 0, 1) : 0;
                    progress?.Report(new EasyTierInstallProgress(
                        "Download",
                        downloadProgress * 0.9,
                        total is > 0
                            ? $"正在下载 EasyTier（{written / 1024d / 1024d:F1}/{total.Value / 1024d / 1024d:F1} MiB）"
                            : $"正在下载 EasyTier（{written / 1024d / 1024d:F1} MiB）"));
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void ExtractArchive(string archivePath, string destinationDirectory)
    {
        string destinationRoot = Path.GetFullPath(destinationDirectory) + Path.DirectorySeparatorChar;
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));
            if (!target.StartsWith(destinationRoot, StringComparison.Ordinal))
                throw new InvalidDataException($"EasyTier 压缩包包含非法路径：{entry.FullName}");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destinationDirectory);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void ValidateArchive(string archivePath)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        bool hasCore = archive.Entries.Any(entry =>
            string.Equals(entry.Name, CoreExecutableName, StringComparison.OrdinalIgnoreCase));
        bool hasCli = archive.Entries.Any(entry =>
            string.Equals(entry.Name, CliExecutableName, StringComparison.OrdinalIgnoreCase));
        if (!hasCore || !hasCli)
            throw new InvalidDataException("EasyTier 压缩包缺少核心程序或命令行程序。");
    }

    private static void EnsureUnixExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        UnixFileMode current = File.GetUnixFileMode(path);
        File.SetUnixFileMode(
            path,
            current |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherExecute);
    }

    private async Task<ProcessResult> RunCliAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        await RunCliProcessAsync(arguments, requireRuntimeProcess: true, cancellationToken).ConfigureAwait(false);

    private async Task<ProcessResult> RunCliProcessAsync(
        IReadOnlyList<string> arguments,
        bool requireRuntimeProcess,
        CancellationToken cancellationToken)
    {
        if (requireRuntimeProcess)
            EnsureProcessAlive();
        ProcessStartInfo startInfo = CreateStartInfo(CliExecutablePath);
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("EasyTier CLI 未能启动。");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        return new ProcessResult(
            process.ExitCode,
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false));
    }

    private ProcessStartInfo CreateStartInfo(string executable) => new()
    {
        FileName = executable,
        WorkingDirectory = Path.GetDirectoryName(executable) ?? _dataDirectory,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8
    };

    private void EnsureProcessAlive()
    {
        if (_process is null || _process.HasExited)
            throw new InvalidOperationException("EasyTier 进程没有运行。");
    }

    private static int GetFreeTcpPort()
    {
        using System.Net.Sockets.TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void AddFlag(ProcessStartInfo startInfo, string name) =>
        startInfo.ArgumentList.Add("--" + name);

    private static void AddOption(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add("--" + name);
        startInfo.ArgumentList.Add(value);
    }

    private static void AddShortOption(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add("-" + name);
        startInfo.ArgumentList.Add(value);
    }

    private static string BuildDiagnosticCommand(ProcessStartInfo startInfo) =>
        $"{startInfo.FileName} {string.Join(' ', startInfo.ArgumentList.Select(static argument =>
            argument.Contains(' ') ? $"\"{argument}\"" : argument))}";

    private static double ParseNumber(string value) =>
        double.TryParse(value == "-" ? "0" : value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
            ? result
            : 0;

    private readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}

internal sealed record EasyTierPeerDto
{
    [JsonPropertyName("hostname")]
    public string HostName { get; init; } = string.Empty;

    [JsonPropertyName("ipv4")]
    public string Ipv4 { get; init; } = string.Empty;

    [JsonPropertyName("lat_ms")]
    public string Ping { get; init; } = "0";

    [JsonPropertyName("loss_rate")]
    public string Loss { get; init; } = "0";

    [JsonPropertyName("nat_type")]
    public string NatType { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;
}

[JsonSerializable(typeof(EasyTierStunDto))]
[JsonSerializable(typeof(EasyTierPeerDto[]))]
internal sealed partial class EasyTierJsonContext : JsonSerializerContext;

internal sealed record EasyTierStunDto
{
    [JsonPropertyName("udp_nat_type")]
    public int UdpNatType { get; init; }

    [JsonPropertyName("tcp_nat_type")]
    public int TcpNatType { get; init; }

    [JsonPropertyName("public_ip")]
    public string[] PublicIps { get; init; } = [];
}
