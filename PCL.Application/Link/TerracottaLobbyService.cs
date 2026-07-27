// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using PCL.Core.Link.McPing;
using PCL.Core.Link.McPing.Model;
using PCL.Core.Link.Scaffolding;
using PCL.Core.Link.Scaffolding.Client.Models;
using PCL.Core.Logging;

namespace PCL.Application.Link;

public enum TerracottaLobbyState
{
    Idle,
    Initializing,
    Discovering,
    Creating,
    Joining,
    Connected,
    Leaving,
    Error
}

public sealed record TerracottaWorld(string Name, int Port, string Description);

public sealed record TerracottaStatus(
    TerracottaLobbyState State,
    string Stage,
    string Detail,
    double Progress);

public sealed class TerracottaLobbyService : IAsyncDisposable
{
    private static readonly Regex MotdPattern = new(
        @"\[MOTD\](.*?)\[/MOTD\]",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex PortPattern = new(
        @"\[AD\](\d{1,5})\[/AD\]",
        RegexOptions.CultureInvariant);
    private readonly string _dataDirectory;
    private readonly EasyTierRuntime _easyTier;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly ObservableCollection<TerracottaWorld> _worlds = [];
    private readonly ReadOnlyObservableCollection<TerracottaWorld> _readOnlyWorlds;
    private readonly ObservableCollection<PlayerProfile> _players = [];
    private readonly ReadOnlyObservableCollection<PlayerProfile> _readOnlyPlayers;
    private CancellationTokenSource _sessionLifetime = new();
    private ScaffoldingServerHost? _server;
    private ScaffoldingClientSession? _client;
    private LocalMinecraftBroadcast? _broadcast;
    private int _disposed;

    public TerracottaLobbyService(string dataDirectory, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _easyTier = new EasyTierRuntime(_dataDirectory, httpClient);
        _readOnlyWorlds = new ReadOnlyObservableCollection<TerracottaWorld>(_worlds);
        _readOnlyPlayers = new ReadOnlyObservableCollection<PlayerProfile>(_players);
        _easyTier.ProcessExited += exitCode =>
        {
            if (State == TerracottaLobbyState.Connected)
            {
                SetState(
                    TerracottaLobbyState.Error,
                    "EasyTier 已退出",
                    $"联机核心意外退出（{exitCode?.ToString(CultureInfo.InvariantCulture) ?? "未知退出码"}）",
                    0);
            }
        };
    }

    public TerracottaLobbyState State { get; private set; } = TerracottaLobbyState.Idle;

    public bool IsHost { get; private set; }

    public string? LobbyCode { get; private set; }

    public string? UserName { get; private set; }

    public int LocalMinecraftPort { get; private set; }

    public long? HostLatency { get; private set; }

    public ReadOnlyObservableCollection<TerracottaWorld> Worlds => _readOnlyWorlds;

    public ReadOnlyObservableCollection<PlayerProfile> Players => _readOnlyPlayers;

    public event Action<TerracottaStatus>? StatusChanged;

    public event Action<IReadOnlyList<TerracottaWorld>>? WorldsChanged;

    public event Action<IReadOnlyList<PlayerProfile>>? PlayersChanged;

    public event Action? ServerStopped;

    public async Task InitializeAsync(
        IProgress<EasyTierInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (State is TerracottaLobbyState.Connected or TerracottaLobbyState.Creating or TerracottaLobbyState.Joining)
            return;
        SetState(TerracottaLobbyState.Initializing, "初始化联机", "正在检查 EasyTier 组件", 0);
        try
        {
            await _easyTier.EnsureInstalledAsync(progress, cancellationToken).ConfigureAwait(false);
            SetState(TerracottaLobbyState.Idle, "联机已就绪", "可以创建或加入房间", 1);
        }
        catch (Exception ex)
        {
            SetState(TerracottaLobbyState.Error, "初始化失败", ex.Message, 0);
            throw;
        }
    }

    public async Task<IReadOnlyList<TerracottaWorld>> DiscoverWorldsAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (State == TerracottaLobbyState.Connected)
            return _worlds.ToArray();
        SetState(TerracottaLobbyState.Discovering, "搜索世界", "正在监听 Minecraft 局域网广播", 0);

        HashSet<int> ports = [];
        List<UdpClient> listeners = [];
        try
        {
            UdpClient ipv4 = CreateDiscoveryListener(AddressFamily.InterNetwork);
            ipv4.JoinMulticastGroup(IPAddress.Parse("224.0.2.60"));
            listeners.Add(ipv4);
            try
            {
                UdpClient ipv6 = CreateDiscoveryListener(AddressFamily.InterNetworkV6);
                ipv6.JoinMulticastGroup(IPAddress.Parse("ff75:230::60"));
                listeners.Add(ipv6);
            }
            catch (Exception ex) when (ex is SocketException or NotSupportedException)
            {
                PortableLog.Debug(ex, "Terracotta", "当前网络环境无法监听 IPv6 Minecraft 广播。");
            }

            using CancellationTokenSource discoveryTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            discoveryTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            Task[] receiveTasks = listeners
                .Select(listener => ReceiveAnnouncementsAsync(listener, ports, discoveryTimeout.Token))
                .ToArray();
            try
            {
                await Task.WhenAll(receiveTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                discoveryTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Normal end of the discovery window.
            }
        }
        finally
        {
            foreach (UdpClient listener in listeners)
                listener.Dispose();
        }

        List<TerracottaWorld> discovered = [];
        foreach (int port in ports.Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using IMcPingService ping = McPingServiceFactory.CreateService("127.0.0.1", port, 2000);
            try
            {
                McPingResult? result = await ping.PingAsync(cancellationToken).ConfigureAwait(false);
                if (result is null)
                    continue;
                string displayName = string.IsNullOrWhiteSpace(result.Description)
                    ? $"Minecraft {result.Version.Name} · {port}"
                    : $"{result.Description} · {result.Version.Name} · {port}";
                discovered.Add(new TerracottaWorld(displayName, port, result.Description));
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                PortableLog.Debug(ex, "Terracotta", $"无法探测本地 Minecraft 端口 {port}。");
            }
        }

        ReplaceCollection(_worlds, discovered);
        WorldsChanged?.Invoke(discovered);
        SetState(TerracottaLobbyState.Idle, "搜索完成", $"发现 {discovered.Count} 个局域网世界", 1);
        return discovered;
    }

    public async Task<EasyTierNatStatus> TestNatAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        SetState(TerracottaLobbyState.Initializing, "网络测试", "正在检测 NAT 与 IPv6", 0.2);
        try
        {
            EasyTierNatStatus result = await _easyTier.TestNatAsync(cancellationToken).ConfigureAwait(false);
            SetState(TerracottaLobbyState.Idle, "网络测试完成", DescribeNat(result), 1);
            return result;
        }
        catch
        {
            SetState(TerracottaLobbyState.Error, "网络测试失败", "无法获取 NAT 信息", 0);
            throw;
        }
    }

    public async Task CreateLobbyAsync(
        int minecraftPort,
        string userName,
        IProgress<EasyTierInstallProgress>? installProgress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidatePort(minecraftPort);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LeaveCoreAsync().ConfigureAwait(false);
            SetState(TerracottaLobbyState.Creating, "创建房间", "正在检查本地 Minecraft 世界", 0.05);
            await ValidateMinecraftPortAsync(minecraftPort, cancellationToken).ConfigureAwait(false);
            await _easyTier.EnsureInstalledAsync(installProgress, cancellationToken).ConfigureAwait(false);

            string machineId = await GetMachineIdAsync(cancellationToken).ConfigureAwait(false);
            LobbyInfo lobby = LobbyCodeGenerator.Generate();
            int scaffoldingPort = GetFreeTcpPort();
            string vendor = BuildVendor();
            PlayerProfile profile = new()
            {
                Name = userName.Trim(),
                MachineId = machineId,
                Vendor = vendor,
                Kind = PlayerKind.HOST
            };

            SetState(TerracottaLobbyState.Creating, "创建房间", "正在启动 EasyTier 网络", 0.3);
            await _easyTier.LaunchAsync(
                lobby,
                machineId,
                minecraftPort,
                scaffoldingPort,
                asHost: true,
                cancellationToken).ConfigureAwait(false);
            ScaffoldingServerHost server = new(scaffoldingPort, minecraftPort, profile);
            server.PlayersChanged += OnPlayersChanged;
            server.Stopped += _ => ServerStopped?.Invoke();
            server.Start();
            _server = server;

            SetState(TerracottaLobbyState.Creating, "创建房间", "正在等待 EasyTier 网络就绪", 0.65);
            await _easyTier.WaitForNetworkAsync(cancellationToken).ConfigureAwait(false);

            IsHost = true;
            LobbyCode = lobby.FullCode;
            UserName = profile.Name;
            LocalMinecraftPort = minecraftPort;
            OnPlayersChanged(server.Players);
            SetState(TerracottaLobbyState.Connected, "房间已创建", lobby.FullCode, 1);
            PortableLog.Info("Terracotta", $"房间创建成功；Lobby={lobby.FullCode}；MinecraftPort={minecraftPort}");
        }
        catch (Exception ex)
        {
            await LeaveCoreAsync().ConfigureAwait(false);
            SetState(TerracottaLobbyState.Error, "创建房间失败", ex.Message, 0);
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task JoinLobbyAsync(
        string lobbyCode,
        string userName,
        IProgress<EasyTierInstallProgress>? installProgress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(lobbyCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        string normalizedLobbyCode = lobbyCode.Trim().ToUpperInvariant();
        if (!LobbyCodeGenerator.TryParse(normalizedLobbyCode, out LobbyInfo? lobby))
            throw new ArgumentException("房间码格式无效。应为 U/XXXX-XXXX-XXXX-XXXX。", nameof(lobbyCode));

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LeaveCoreAsync().ConfigureAwait(false);
            SetState(TerracottaLobbyState.Joining, "加入房间", "正在准备 EasyTier 组件", 0.05);
            await _easyTier.EnsureInstalledAsync(installProgress, cancellationToken).ConfigureAwait(false);
            string machineId = await GetMachineIdAsync(cancellationToken).ConfigureAwait(false);
            PlayerProfile localProfile = new()
            {
                Name = userName.Trim(),
                MachineId = machineId,
                Vendor = BuildVendor(),
                Kind = null
            };

            SetState(TerracottaLobbyState.Joining, "加入房间", "正在加入 EasyTier 网络", 0.25);
            await _easyTier.LaunchAsync(
                lobby,
                machineId,
                minecraftPort: 0,
                scaffoldingPort: 0,
                asHost: false,
                cancellationToken).ConfigureAwait(false);

            IReadOnlyList<EasyTierPeer> peers =
                await _easyTier.WaitForNetworkAsync(cancellationToken).ConfigureAwait(false);
            EasyTierPeer host = peers.SingleOrDefault(static peer => peer.IsScaffoldingHost)
                                ?? throw new InvalidOperationException("EasyTier 网络中没有找到房主。");
            if (!int.TryParse(
                    host.HostName.AsSpan(EasyTierRuntime.ScaffoldingHostPrefix.Length),
                    out int scaffoldingPort))
            {
                throw new InvalidDataException("房主发布了无效的 Scaffolding 控制端口。");
            }

            SetState(TerracottaLobbyState.Joining, "加入房间", "正在连接房主控制服务", 0.55);
            int localControlPort = await _easyTier
                .AddPortForwardAsync(host.Ip, scaffoldingPort, cancellationToken)
                .ConfigureAwait(false);
            ScaffoldingClientSession client = new("127.0.0.1", localControlPort, localProfile);
            client.Heartbeat += OnHeartbeat;
            client.Disconnected += exception =>
            {
                SetState(
                    TerracottaLobbyState.Error,
                    "与房主断开连接",
                    exception?.Message ?? "房主已关闭房间",
                    0);
                ServerStopped?.Invoke();
            };
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> protocols = await client.GetProtocolsAsync(cancellationToken).ConfigureAwait(false);
            string[] required = ["c:server_port", "c:player_ping", "c:player_profiles_list"];
            if (required.Any(requiredProtocol => !protocols.Contains(requiredProtocol, StringComparer.Ordinal)))
                throw new InvalidDataException("房主的 Scaffolding 协议版本不兼容。");
            ushort minecraftPort = await client.GetMinecraftPortAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<PlayerProfile> players = await client.GetPlayersAsync(cancellationToken).ConfigureAwait(false);
            _client = client;

            SetState(TerracottaLobbyState.Joining, "加入房间", "正在转发 Minecraft 端口", 0.8);
            int localMinecraftPort = await _easyTier
                .AddPortForwardAsync(host.Ip, minecraftPort, cancellationToken)
                .ConfigureAwait(false);
            LocalMinecraftBroadcast broadcast = new(
                $"PCL N 陶瓦联机 - {players.FirstOrDefault(static player => player.Kind == PlayerKind.HOST)?.Name ?? "房主"}",
                localMinecraftPort);
            broadcast.Start();
            _broadcast = broadcast;

            IsHost = false;
            LobbyCode = lobby.FullCode;
            UserName = localProfile.Name;
            LocalMinecraftPort = localMinecraftPort;
            OnPlayersChanged(players);
            SetState(
                TerracottaLobbyState.Connected,
                "已加入房间",
                $"Minecraft 局域网入口：127.0.0.1:{localMinecraftPort}",
                1);
            PortableLog.Info(
                "Terracotta",
                $"加入房间成功；Lobby={lobby.FullCode}；LocalMinecraftPort={localMinecraftPort}");
        }
        catch (Exception ex)
        {
            await LeaveCoreAsync().ConfigureAwait(false);
            SetState(TerracottaLobbyState.Error, "加入房间失败", ex.Message, 0);
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task LeaveLobbyAsync()
    {
        ThrowIfDisposed();
        await _operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            SetState(TerracottaLobbyState.Leaving, "正在退出房间", "正在清理端口转发与控制连接", 0.2);
            await LeaveCoreAsync().ConfigureAwait(false);
            SetState(TerracottaLobbyState.Idle, "已退出房间", "可以创建或加入其他房间", 1);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task LeaveCoreAsync()
    {
        await _sessionLifetime.CancelAsync().ConfigureAwait(false);
        _broadcast?.Dispose();
        _broadcast = null;
        if (_client is not null)
        {
            _client.Heartbeat -= OnHeartbeat;
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }
        if (_server is not null)
        {
            _server.PlayersChanged -= OnPlayersChanged;
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }
        await _easyTier.StopAsync().ConfigureAwait(false);
        _sessionLifetime.Dispose();
        _sessionLifetime = new CancellationTokenSource();
        ReplaceCollection(_players, []);
        PlayersChanged?.Invoke([]);
        IsHost = false;
        LobbyCode = null;
        UserName = null;
        LocalMinecraftPort = 0;
        HostLatency = null;
    }

    private void OnHeartbeat(IReadOnlyList<PlayerProfile> players, long latency)
    {
        HostLatency = latency;
        OnPlayersChanged(players);
        StatusChanged?.Invoke(new TerracottaStatus(
            State,
            "已连接",
            $"与房主的控制延迟：{latency} ms",
            1));
    }

    private void OnPlayersChanged(IReadOnlyList<PlayerProfile> players)
    {
        ReplaceCollection(
            _players,
            players.OrderBy(static profile => profile.Kind == PlayerKind.HOST ? 0 : 1)
                .ThenBy(static profile => profile.Name, StringComparer.OrdinalIgnoreCase));
        PlayersChanged?.Invoke(_players.ToArray());
    }

    private void SetState(
        TerracottaLobbyState state,
        string stage,
        string detail,
        double progress)
    {
        State = state;
        PortableLog.Info("Terracotta", $"状态：{state}；阶段={stage}；详情={detail}");
        StatusChanged?.Invoke(new TerracottaStatus(state, stage, detail, Math.Clamp(progress, 0, 1)));
    }

    private async Task<string> GetMachineIdAsync(CancellationToken cancellationToken)
    {
        string directory = Path.Combine(_dataDirectory, "PCL-N");
        string path = Path.Combine(directory, "terracotta-machine-id.txt");
        if (File.Exists(path))
        {
            string existing = (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Trim();
            if (!string.IsNullOrWhiteSpace(existing))
                return existing;
        }

        Directory.CreateDirectory(directory);
        string machineId = Guid.NewGuid().ToString("N");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, machineId, cancellationToken).ConfigureAwait(false);
        try
        {
            File.Move(temporary, path, overwrite: false);
            return machineId;
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(temporary);
            return (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Trim();
        }
    }

    private static async Task ValidateMinecraftPortAsync(int port, CancellationToken cancellationToken)
    {
        using IMcPingService ping = McPingServiceFactory.CreateService("127.0.0.1", port, 5000);
        McPingResult? result = await ping.PingAsync(cancellationToken).ConfigureAwait(false);
        if (result is null || result.Version.Protocol == 0)
            throw new InvalidOperationException($"127.0.0.1:{port} 不是可用的 Minecraft 局域网世界。");
    }

    private static UdpClient CreateDiscoveryListener(AddressFamily family)
    {
        UdpClient client = new(family);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new IPEndPoint(
            family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any,
            4445));
        return client;
    }

    private static async Task ReceiveAnnouncementsAsync(
        UdpClient listener,
        HashSet<int> ports,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult packet = await listener.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (!IsLocalAddress(packet.RemoteEndPoint.Address))
                continue;
            string message = Encoding.UTF8.GetString(packet.Buffer);
            Match portMatch = PortPattern.Match(message);
            if (!portMatch.Success ||
                !int.TryParse(portMatch.Groups[1].Value, out int port) ||
                port is <= 0 or > ushort.MaxValue)
            {
                continue;
            }

            lock (ports)
                ports.Add(port);
            Match motd = MotdPattern.Match(message);
            PortableLog.Debug(
                "Terracotta",
                $"发现 Minecraft 广播；Port={port}；MOTD={(motd.Success ? motd.Groups[1].Value : "(none)")}");
        }
    }

    private static bool IsLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;
        foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up)
                continue;
            if (network.GetIPProperties().UnicastAddresses.Any(entry => entry.Address.Equals(address)))
                return true;
        }

        return false;
    }

    private static int GetFreeTcpPort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string BuildVendor()
    {
        string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        return $"PCL N {version}, EasyTier {EasyTierRuntime.CurrentVersion}";
    }

    private static string DescribeNat(EasyTierNatStatus status) =>
        $"UDP: {NatName(status.UdpNatType)}；TCP: {NatName(status.TcpNatType)}；IPv6: {(status.SupportsIpv6 ? "可用" : "不可用")}";

    private static string NatName(int type) => type switch
    {
        0 => "开放网络",
        1 => "无端口转换",
        2 => "完全锥形",
        3 => "受限锥形",
        4 => "端口受限锥形",
        5 => "易打洞对称型",
        6 => "对称型",
        7 => "对称型防火墙",
        8 => "UDP 被阻止",
        _ => "未知"
    };

    private static void ValidatePort(int port)
    {
        if (port is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(port), "端口必须在 1 到 65535 之间。");
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> collection, IEnumerable<T> values)
    {
        collection.Clear();
        foreach (T value in values)
            collection.Add(value);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await LeaveCoreAsync().ConfigureAwait(false);
            await _easyTier.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
            _operationLock.Dispose();
        }
    }
}

internal sealed class LocalMinecraftBroadcast(string description, int localPort) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _broadcastTask;

    public void Start()
    {
        if (_broadcastTask is not null)
            return;
        _broadcastTask = BroadcastAsync(_lifetime.Token);
        PortableLog.Info("Terracotta", $"开始发布 Minecraft 局域网入口：127.0.0.1:{localPort}");
    }

    private async Task BroadcastAsync(CancellationToken cancellationToken)
    {
        byte[] payload = Encoding.UTF8.GetBytes($"[MOTD]{description}[/MOTD][AD]{localPort}[/AD]");
        using UdpClient ipv4 = new(AddressFamily.InterNetwork);
        UdpClient? ipv6 = null;
        try
        {
            ipv6 = new UdpClient(AddressFamily.InterNetworkV6);
            while (!cancellationToken.IsCancellationRequested)
            {
                await ipv4.SendAsync(
                    payload,
                    new IPEndPoint(IPAddress.Loopback, 4445),
                    cancellationToken).ConfigureAwait(false);
                try
                {
                    await ipv6.SendAsync(
                        payload,
                        new IPEndPoint(IPAddress.IPv6Loopback, 4445),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // IPv4 is sufficient on hosts without IPv6 loopback.
                }

                await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected.
        }
        finally
        {
            ipv6?.Dispose();
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        try
        {
            _broadcastTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
        _lifetime.Dispose();
    }
}
