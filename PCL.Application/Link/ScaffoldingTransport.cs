// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PCL.Core.Link.Scaffolding;
using PCL.Core.Link.Scaffolding.Client.Models;
using PCL.Core.Logging;

namespace PCL.Application.Link;

public sealed class ScaffoldingClientSession(
    string host,
    int port,
    PlayerProfile localProfile) : IAsyncDisposable
{
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Task? _heartbeatTask;
    private int _disposed;

    public bool IsConnected => _client?.Connected == true && Volatile.Read(ref _disposed) == 0;

    public IReadOnlyList<PlayerProfile> Players { get; private set; } = [];

    public event Action<IReadOnlyList<PlayerProfile>, long>? Heartbeat;

    public event Action<Exception?>? Disconnected;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_client is not null)
            return;

        TcpClient client = new();
        try
        {
            PortableLog.Info("Scaffolding", $"连接控制服务器：{host}:{port}");
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            _client = client;
            _stream = client.GetStream();
            await PlayerPingAsync(cancellationToken).ConfigureAwait(false);
            _heartbeatTask = RunHeartbeatAsync(_lifetime.Token);
        }
        catch
        {
            client.Dispose();
            _client = null;
            _stream = null;
            throw;
        }
    }

    public async Task<ushort> GetMinecraftPortAsync(CancellationToken cancellationToken = default)
    {
        ReadOnlyMemory<byte> response = await SendRequestAsync(
            "c:server_port",
            ReadOnlyMemory<byte>.Empty,
            cancellationToken).ConfigureAwait(false);
        if (response.Length != 2)
            throw new InvalidDataException("Scaffolding 返回了无效的 Minecraft 端口。");
        return BinaryPrimitives.ReadUInt16BigEndian(response.Span);
    }

    public async Task<IReadOnlyList<string>> GetProtocolsAsync(CancellationToken cancellationToken = default)
    {
        byte[] requestBody = Encoding.ASCII.GetBytes(string.Join('\0', ScaffoldingProtocol.SupportedRequests));
        ReadOnlyMemory<byte> response = await SendRequestAsync(
            "c:protocols",
            requestBody,
            cancellationToken).ConfigureAwait(false);
        return Encoding.ASCII.GetString(response.Span)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    public async Task<IReadOnlyList<PlayerProfile>> GetPlayersAsync(
        CancellationToken cancellationToken = default)
    {
        ReadOnlyMemory<byte> response = await SendRequestAsync(
            "c:player_profiles_list",
            ReadOnlyMemory<byte>.Empty,
            cancellationToken).ConfigureAwait(false);
        Players = ScaffoldingProtocol.DeserializeProfiles(response.Span);
        return Players;
    }

    public async Task<ReadOnlyMemory<byte>> PingAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (payload.Length >= 32)
            throw new ArgumentOutOfRangeException(nameof(payload), "Ping payload must be shorter than 32 bytes.");
        return await SendRequestAsync("c:ping", payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task PlayerPingAsync(CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> response = await SendRequestAsync(
            "c:player_ping",
            ScaffoldingProtocol.SerializeProfile(localProfile),
            cancellationToken).ConfigureAwait(false);
        if (!response.IsEmpty)
            throw new InvalidDataException("Scaffolding player_ping 应返回空响应。");
    }

    private async Task<ReadOnlyMemory<byte>> SendRequestAsync(
        string requestType,
        ReadOnlyMemory<byte> requestBody,
        CancellationToken cancellationToken)
    {
        NetworkStream stream = _stream ?? throw new InvalidOperationException("Scaffolding 客户端尚未连接。");
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] request = ScaffoldingProtocol.EncodeRequest(requestType, requestBody.Span);
            await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            ScaffoldingResponseFrame response = await ScaffoldingProtocol
                .ReadResponseAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            return response.Body;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task RunHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                Stopwatch timer = Stopwatch.StartNew();
                await PlayerPingAsync(cancellationToken).ConfigureAwait(false);
                IReadOnlyList<PlayerProfile> players = await GetPlayersAsync(cancellationToken).ConfigureAwait(false);
                timer.Stop();
                Heartbeat?.Invoke(players, timer.ElapsedMilliseconds);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            PortableLog.Error(ex, "Scaffolding", "玩家心跳失败，控制服务器可能已关闭。");
            Disconnected?.Invoke(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_heartbeatTask is not null)
        {
            try
            {
                await _heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        _stream?.Dispose();
        _client?.Dispose();
        _lifetime.Dispose();
        _requestLock.Dispose();
    }
}

public sealed class ScaffoldingServerHost : IAsyncDisposable
{
    private static readonly TimeSpan PlayerTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(5);
    private readonly int _minecraftPort;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, TrackedPlayer> _players = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Task> _clientTasks = [];
    private Task? _listenerTask;
    private Task? _cleanupTask;
    private int _disposed;

    public ScaffoldingServerHost(int port, int minecraftPort, PlayerProfile hostProfile)
    {
        if (port is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(port));
        if (minecraftPort is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(minecraftPort));
        ArgumentNullException.ThrowIfNull(hostProfile);

        Port = port;
        _minecraftPort = minecraftPort;
        _listener = new TcpListener(IPAddress.Loopback, port);
        PlayerProfile normalizedHost = hostProfile with { Kind = PlayerKind.HOST };
        _players[normalizedHost.MachineId] = new TrackedPlayer(normalizedHost, DateTime.UtcNow);
    }

    public int Port { get; }

    public IReadOnlyList<PlayerProfile> Players =>
        _players.Values
            .Select(static tracked => tracked.Profile)
            .OrderBy(static profile => profile.Kind == PlayerKind.HOST ? 0 : 1)
            .ThenBy(static profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public event Action<IReadOnlyList<PlayerProfile>>? PlayersChanged;

    public event Action<Exception?>? Stopped;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_listenerTask is not null)
            return;
        _listener.Start();
        PortableLog.Info("Scaffolding", $"控制服务器正在监听 127.0.0.1:{Port}");
        _listenerTask = AcceptClientsAsync(_lifetime.Token);
        _cleanupTask = MonitorPlayersAsync(_lifetime.Token);
        PlayersChanged?.Invoke(Players);
    }

    private async Task AcceptClientsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                Guid sessionId = Guid.NewGuid();
                Task task = HandleClientAsync(sessionId, client, cancellationToken);
                _clientTasks[sessionId] = task;
                _ = task.ContinueWith(
                    (completedTask, state) =>
                    {
                        _ = completedTask;
                        _clientTasks.TryRemove((Guid)state!, out Task? _);
                    },
                    sessionId,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected.
        }
        catch (Exception ex)
        {
            PortableLog.Error(ex, "Scaffolding", "控制服务器监听失败。");
            Stopped?.Invoke(ex);
        }
    }

    private async Task HandleClientAsync(
        Guid sessionId,
        TcpClient client,
        CancellationToken cancellationToken)
    {
        string endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        PortableLog.Debug("Scaffolding", $"控制连接已建立；Session={sessionId:N}；Remote={endpoint}");
        using (client)
        {
            NetworkStream stream = client.GetStream();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    ScaffoldingRequestFrame? frame = await ScaffoldingProtocol
                        .ReadRequestAsync(stream, cancellationToken)
                        .ConfigureAwait(false);
                    if (frame is null)
                        break;
                    (byte status, ReadOnlyMemory<byte> responseBody) =
                        HandleRequest(frame.Value, sessionId.ToString("N"));
                    byte[] response = ScaffoldingProtocol.EncodeResponse(status, responseBody.Span);
                    await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected.
            }
            catch (IOException)
            {
                PortableLog.Debug("Scaffolding", $"控制连接已关闭；Session={sessionId:N}");
            }
            catch (Exception ex)
            {
                PortableLog.Warn(ex, "Scaffolding", $"处理控制连接失败；Session={sessionId:N}");
            }
        }
    }

    private (byte Status, ReadOnlyMemory<byte> Body) HandleRequest(
        ScaffoldingRequestFrame frame,
        string sessionId)
    {
        switch (frame.RequestType)
        {
            case "c:ping":
                return (0, frame.Body);
            case "c:protocols":
                return (0, Encoding.ASCII.GetBytes(string.Join('\0', ScaffoldingProtocol.SupportedRequests)));
            case "c:server_port":
            {
                byte[] port = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(port, (ushort)_minecraftPort);
                return (0, port);
            }
            case "c:player_profiles_list":
                return (0, ScaffoldingProtocol.SerializeProfiles(Players));
            case "c:player_ping":
                return HandlePlayerPing(frame.Body, sessionId);
            default:
                return (byte.MaxValue, Encoding.UTF8.GetBytes($"Unsupported request: {frame.RequestType}"));
        }
    }

    private (byte Status, ReadOnlyMemory<byte> Body) HandlePlayerPing(
        ReadOnlyMemory<byte> body,
        string sessionId)
    {
        try
        {
            PlayerProfile? profile = ScaffoldingProtocol.DeserializeProfile(body.Span);
            if (profile is null || string.IsNullOrWhiteSpace(profile.MachineId))
                return (32, ReadOnlyMemory<byte>.Empty);

            PlayerProfile guest = profile with { Kind = PlayerKind.GUEST };
            bool changed = false;
            _players.AddOrUpdate(
                guest.MachineId,
                _ =>
                {
                    changed = true;
                    PortableLog.Info(
                        "Scaffolding",
                        $"玩家加入；Name={guest.Name}；MachineId={guest.MachineId}; Session={sessionId}");
                    return new TrackedPlayer(guest, DateTime.UtcNow);
                },
                (_, existing) =>
                {
                    if (existing.Profile != guest)
                        changed = true;
                    return new TrackedPlayer(guest, DateTime.UtcNow);
                });
            if (changed)
                PlayersChanged?.Invoke(Players);
            return (0, ReadOnlyMemory<byte>.Empty);
        }
        catch (JsonException ex)
        {
            PortableLog.Warn(ex, "Scaffolding", $"玩家心跳 JSON 无效；Session={sessionId}");
            return (32, ReadOnlyMemory<byte>.Empty);
        }
    }

    private async Task MonitorPlayersAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(CleanupInterval, cancellationToken).ConfigureAwait(false);
                DateTime now = DateTime.UtcNow;
                bool changed = false;
                foreach ((string key, TrackedPlayer tracked) in _players)
                {
                    if (tracked.Profile.Kind == PlayerKind.HOST || now - tracked.LastSeenUtc <= PlayerTimeout)
                        continue;
                    if (_players.TryRemove(key, out TrackedPlayer? removed))
                    {
                        changed = true;
                        PortableLog.Info("Scaffolding", $"玩家心跳超时：{removed.Profile.Name}");
                    }
                }

                if (changed)
                    PlayersChanged?.Invoke(Players);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _listener.Stop();

        List<Task> tasks = [];
        if (_listenerTask is not null)
            tasks.Add(_listenerTask);
        if (_cleanupTask is not null)
            tasks.Add(_cleanupTask);
        tasks.AddRange(_clientTasks.Values);
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        _lifetime.Dispose();
        Stopped?.Invoke(null);
    }

    private sealed record TrackedPlayer(PlayerProfile Profile, DateTime LastSeenUtc);
}
