// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Threading.Channels;
using PCL.Core.Logging;
using PCL.Desktop.Telemetry;

namespace PCL.Desktop.Hosting.PluginSidecar;

/// <summary>AOT-safe RPC client for the plugin sidecar process.</summary>
internal sealed class PluginSidecarClient : IAsyncDisposable
{
    private const int OutboundCapacity = 256;
    private const int MaxPendingRequests = 128;

    private readonly SemaphoreSlim _legacyGate = new(1, 1);
    private readonly SemaphoreSlim _pendingSlots = new(MaxPendingRequests, MaxPendingRequests);
    private readonly ConcurrentDictionary<ulong, PendingCall> _pending = new();
    private Stream? _stream;
    private Channel<OutboundFrame>? _outbound;
    private CancellationTokenSource? _transportCancellation;
    private PipeReader? _pipeReader;
    private PipeWriter? _pipeWriter;
    private Task? _readLoop;
    private Task? _writeLoop;
    private long _nextId;
    private int _protocolVersion = PluginSidecarProtocolVersions.Legacy;
    private int _disposed;
    private int _broken;

    public bool IsConnected =>
        _stream is { CanRead: true, CanWrite: true } &&
        _disposed == 0 &&
        _broken == 0;

    /// <summary>True when the pipe desynced and must be restarted (not merely disposed).</summary>
    public bool IsBroken => _broken != 0;

    internal int ProtocolVersion => Volatile.Read(ref _protocolVersion);

    public async Task ConnectAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await _legacyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, nameof(PluginSidecarClient));
            _stream = stream;
            Volatile.Write(ref _broken, 0);
            Volatile.Write(ref _protocolVersion, PluginSidecarProtocolVersions.Legacy);
        }
        finally
        {
            _legacyGate.Release();
        }
    }

    public async Task<PluginSidecarResult> HelloAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        PluginSidecarResult result = await CallLegacyAsync(
                PluginSidecarMethods.SystemHello,
                new PluginSidecarParams
                {
                    Token = token,
                    MinimumProtocolVersion = PluginSidecarProtocolVersions.Legacy,
                    MaximumProtocolVersion = PluginSidecarProtocolVersions.Current
                },
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.ProtocolVersion >= PluginSidecarProtocolVersions.Current)
            StartV4Transport();
        return result;
    }

    public Task<PluginSidecarResult> PingAsync(CancellationToken cancellationToken = default) =>
        CallAsync(PluginSidecarMethods.HealthPing, null, cancellationToken);

    public Task<PluginSidecarResult> InitRuntimeAsync(
        string applicationDataDirectory,
        string cacheDirectory,
        string hostVersion,
        CancellationToken cancellationToken = default) =>
        CallAsync(
            PluginSidecarMethods.RuntimeInit,
            new PluginSidecarParams
            {
                ApplicationDataDirectory = applicationDataDirectory,
                CacheDirectory = cacheDirectory,
                HostVersion = hostVersion
            },
            cancellationToken);

    public Task<PluginSidecarResult> ListCatalogAsync(CancellationToken cancellationToken = default) =>
        CallAsync(PluginSidecarMethods.CatalogList, null, cancellationToken);

    public Task<PluginSidecarResult> RuntimeStatusAsync(CancellationToken cancellationToken = default) =>
        CallAsync(PluginSidecarMethods.RuntimeStatus, null, cancellationToken);

    public Task<PluginSidecarResult> SyncHostStateAsync(
        PluginSidecarHostInstance[] instances,
        PluginSidecarGameSession[] sessions,
        CancellationToken cancellationToken = default) =>
        CallAsync(
            PluginSidecarMethods.HostSyncState,
            new PluginSidecarParams { Instances = instances, Sessions = sessions },
            cancellationToken);

    public Task<PluginSidecarResult> UiManifestAsync(CancellationToken cancellationToken = default) =>
        CallAsync(PluginSidecarMethods.UiManifest, null, cancellationToken);

    public Task<PluginSidecarResult> UiGetPageAsync(string pageId, CancellationToken cancellationToken = default) =>
        CallAsync(
            PluginSidecarMethods.UiGetPage,
            new PluginSidecarParams { PageId = pageId },
            cancellationToken);

    public Task<PluginSidecarResult> UiInvokeActionAsync(
        string pageId,
        string actionId,
        string? value = null,
        bool? boolValue = null,
        string? packagePath = null,
        string? pluginId = null,
        IProgress<PluginSidecarProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(
            PluginSidecarMethods.UiInvokeAction,
            new PluginSidecarParams
            {
                PageId = pageId,
                ActionId = actionId,
                Value = value,
                BoolValue = boolValue,
                PackagePath = packagePath,
                PluginId = pluginId
            },
            progress,
            cancellationToken);

    public Task<PluginSidecarResult> InstallPnpAsync(
        string packagePath,
        CancellationToken cancellationToken = default) =>
        CallAsync(
            PluginSidecarMethods.CatalogInstallPnp,
            new PluginSidecarParams { PackagePath = packagePath },
            cancellationToken);

    public async Task<PluginSidecarResult> SetEnabledAsync(
        string pluginId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        PluginSidecarResult result = await CallAsync(
                PluginSidecarMethods.CatalogSetEnabled,
                new PluginSidecarParams { PluginId = pluginId, Enabled = enabled },
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Ok)
            LauncherTelemetry.CaptureEvent(enabled ? "plugin_enabled" : "plugin_disabled");
        return result;
    }

    public Task<PluginSidecarResult> UninstallAsync(
        string pluginId,
        CancellationToken cancellationToken = default) =>
        CallAsync(
            PluginSidecarMethods.CatalogUninstall,
            new PluginSidecarParams { PluginId = pluginId },
            cancellationToken);

    public Task<PluginSidecarResult> ShutdownAsync(CancellationToken cancellationToken = default) =>
        CallAsync(PluginSidecarMethods.SystemShutdown, null, cancellationToken);

    public Task<PluginSidecarResult> FeedbackSessionAsync(CancellationToken cancellationToken = default) =>
        CallAsync(PluginSidecarMethods.FeedbackSession, null, cancellationToken);

    public Task<PluginSidecarResult> FeedbackCatalogAsync(CancellationToken cancellationToken = default) =>
        CallAsync(PluginSidecarMethods.FeedbackCatalog, null, cancellationToken);

    public Task<PluginSidecarResult> FeedbackSubmitAsync(
        string category,
        string title,
        string description,
        CancellationToken cancellationToken = default) =>
        CallAsync(
            PluginSidecarMethods.FeedbackSubmit,
            new PluginSidecarParams
            {
                Category = category,
                Title = title,
                Description = description
            },
            cancellationToken);

    public Task<PluginSidecarResult> CallAsync(
        string method,
        PluginSidecarParams? parameters,
        CancellationToken cancellationToken = default) =>
        CallAsync(method, parameters, progress: null, cancellationToken);

    public async Task<PluginSidecarResult> CallAsync(
        string method,
        PluginSidecarParams? parameters,
        IProgress<PluginSidecarProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ObjectDisposedException.ThrowIf(_disposed != 0, nameof(PluginSidecarClient));
        if (_broken != 0)
            throw new InvalidOperationException("插件侧车连接已损坏，请刷新页面或重启启动器以重建连接。");

        using TelemetryOperation operation = LauncherTelemetry.StartOperation(
            "sidecar." + TelemetryDataPolicy.NormalizeName(method),
            "ipc.request");
        try
        {
            return await (ProtocolVersion >= PluginSidecarProtocolVersions.Current
                    ? CallV4Async(method, parameters, progress, cancellationToken)
                    : CallLegacyAsync(method, parameters, progress, cancellationToken))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception ex)
        {
            operation.Fail(ex);
            LauncherTelemetry.CaptureException(ex, "ipc.request");
            throw;
        }
    }

    private async Task<PluginSidecarResult> CallV4Async(
        string method,
        PluginSidecarParams? parameters,
        IProgress<PluginSidecarProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_broken != 0 || _disposed != 0)
            throw new InvalidOperationException("插件侧车连接不可用。");

        await _pendingSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Channel<OutboundFrame> outbound = _outbound
                ?? throw new InvalidOperationException("Sidecar v4 transport is not running.");
            ulong id = checked((ulong)Interlocked.Increment(ref _nextId));
            PendingCall pending = new(progress);
            if (!_pending.TryAdd(id, pending))
                throw new InvalidOperationException($"Duplicate sidecar request id: {id}.");

            using CancellationTokenRegistration registration = cancellationToken.Register(
                static state =>
                {
                    CancelState cancel = (CancelState)state!;
                    cancel.Client.CancelV4Request(cancel.RequestId, cancel.Token);
                },
                new CancelState(this, id, cancellationToken));

            try
            {
                await outbound.Writer.WriteAsync(
                        new OutboundFrame(
                            PluginSidecarMessageType.Request,
                            id,
                            new PluginSidecarV4Request { Method = method, Params = parameters }),
                        cancellationToken)
                    .ConfigureAwait(false);
                return await pending.Completion.Task.ConfigureAwait(false);
            }
            catch
            {
                _pending.TryRemove(id, out _);
                throw;
            }
        }
        finally
        {
            _pendingSlots.Release();
        }
    }

    private async Task<PluginSidecarResult> CallLegacyAsync(
        string method,
        PluginSidecarParams? parameters,
        IProgress<PluginSidecarProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _legacyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool wroteRequest = false;
        try
        {
            if (_broken != 0 || _disposed != 0)
                throw new InvalidOperationException("插件侧车连接不可用。");

            Stream stream = _stream ?? throw new InvalidOperationException("Sidecar client is not connected.");
            string id = Interlocked.Increment(ref _nextId)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            PluginSidecarRequest request = new()
            {
                Id = id,
                Method = method,
                Params = parameters
            };

            await PluginSidecarFraming.WriteAsync(
                    stream,
                    request,
                    PluginSidecarJsonContext.Default.PluginSidecarRequest,
                    cancellationToken)
                .ConfigureAwait(false);
            wroteRequest = true;

            const int maxSkips = 16;
            int skips = 0;
            while (true)
            {
                PluginSidecarResponse? response = await PluginSidecarFraming.ReadAsync(
                        stream,
                        PluginSidecarJsonContext.Default.PluginSidecarResponse,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (response is null)
                {
                    MarkBroken(new InvalidDataException("Empty sidecar response."));
                    throw new InvalidOperationException("Empty sidecar response.");
                }

                if (!string.Equals(response.Id, id, StringComparison.Ordinal))
                {
                    skips++;
                    PortableLog.Warn(
                        "PluginSidecar",
                        $"忽略错序响应帧：expected {id}, got {response.Id}（skip {skips}/{maxSkips}）。");
                    if (skips >= maxSkips)
                    {
                        InvalidDataException error = new(
                            $"插件侧车传输异常：expected {id}, got {response.Id}。");
                        MarkBroken(error);
                        throw new InvalidOperationException(
                            error.Message +
                            "连接已标记为损坏；请刷新插件页或重启启动器。若仍无法启动，请在任务管理器结束 PCL-N-Edition 与 PCL.Plugin.Sidecar。");
                    }

                    continue;
                }

                if (response.Progress is not null && response.Result is null && response.Error is null)
                {
                    ReportProgress(progress, response.Progress);
                    continue;
                }

                if (response.Error is not null)
                    throw CreateSidecarException(response.Error);
                return response.Result ?? new PluginSidecarResult { Ok = true };
            }
        }
        catch (OperationCanceledException) when (wroteRequest)
        {
            MarkBroken(new OperationCanceledException("Legacy sidecar request was cancelled after write."));
            throw;
        }
        catch (IOException ex)
        {
            MarkBroken(ex);
            throw;
        }
        catch (ObjectDisposedException ex)
        {
            MarkBroken(ex);
            throw;
        }
        finally
        {
            try
            {
                _legacyGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // shutting down
            }
        }
    }

    private void StartV4Transport()
    {
        if (Interlocked.CompareExchange(
                ref _protocolVersion,
                PluginSidecarProtocolVersions.Current,
                PluginSidecarProtocolVersions.Legacy) != PluginSidecarProtocolVersions.Legacy)
        {
            return;
        }

        Stream stream = _stream ?? throw new InvalidOperationException("Sidecar client is not connected.");
        CancellationTokenSource transportCancellation = new();
        Channel<OutboundFrame> outbound = Channel.CreateBounded<OutboundFrame>(
            new BoundedChannelOptions(OutboundCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        PipeReader reader = PipeReader.Create(
            stream,
            new StreamPipeReaderOptions(bufferSize: 64 * 1024, leaveOpen: true));
        PipeWriter writer = PipeWriter.Create(
            stream,
            new StreamPipeWriterOptions(minimumBufferSize: 64 * 1024, leaveOpen: true));

        _transportCancellation = transportCancellation;
        _outbound = outbound;
        _pipeReader = reader;
        _pipeWriter = writer;
        _readLoop = ReadV4LoopAsync(reader, transportCancellation.Token);
        _writeLoop = WriteV4LoopAsync(writer, outbound.Reader, transportCancellation.Token);
    }

    private async Task ReadV4LoopAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;
                SequencePosition examined = buffer.End;
                try
                {
                    while (PluginSidecarV4Framing.TryReadFrame(ref buffer, out PluginSidecarFrameHeader header, out ReadOnlySequence<byte> payload))
                        DispatchV4Frame(header, payload);
                }
                finally
                {
                    reader.AdvanceTo(buffer.Start, examined);
                }

                if (result.IsCompleted)
                {
                    if (!buffer.IsEmpty)
                        throw new EndOfStreamException("Sidecar closed with an incomplete v4 frame.");
                    throw new EndOfStreamException("Sidecar v4 connection closed.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            MarkBroken(ex);
        }
        finally
        {
            try
            {
                await reader.CompleteAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignore transport cleanup failures
            }
        }
    }

    private void DispatchV4Frame(
        PluginSidecarFrameHeader header,
        ReadOnlySequence<byte> payload)
    {
        if (header.RequestId == 0)
            throw new InvalidDataException("Sidecar v4 response has no request id.");

        if (header.MessageType == PluginSidecarMessageType.Progress)
        {
            if (_pending.TryGetValue(header.RequestId, out PendingCall? pending))
                ReportProgress(pending.Progress, PluginSidecarV4Framing.ReadProgress(payload));
            return;
        }

        if (header.MessageType != PluginSidecarMessageType.Response)
        {
            PortableLog.Debug(
                "PluginSidecar",
                $"忽略未知 v4 帧：type={(ushort)header.MessageType}, request={header.RequestId}。");
            return;
        }

        PluginSidecarV4Response? response = PluginSidecarV4Framing.ReadJson(
            payload,
            PluginSidecarJsonContext.Default.PluginSidecarV4Response);
        if (!_pending.TryRemove(header.RequestId, out PendingCall? call))
        {
            PortableLog.Debug("PluginSidecar", $"忽略已取消或过期的 v4 响应：{header.RequestId}。");
            return;
        }

        if (response?.Error is not null)
            call.Completion.TrySetException(CreateSidecarException(response.Error));
        else
            call.Completion.TrySetResult(response?.Result ?? new PluginSidecarResult { Ok = true });
    }

    private async Task WriteV4LoopAsync(
        PipeWriter writer,
        ChannelReader<OutboundFrame> outbound,
        CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> payloadBuffer = new(1024);
        try
        {
            while (await outbound.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (outbound.TryRead(out OutboundFrame frame))
                {
                    if (frame.MessageType == PluginSidecarMessageType.Cancel)
                    {
                        PluginSidecarV4Framing.WriteEmpty(
                            writer,
                            frame.MessageType,
                            PluginSidecarFrameFlags.Final,
                            frame.RequestId);
                    }
                    else
                    {
                        PluginSidecarV4Framing.WriteJson(
                            writer,
                            payloadBuffer,
                            frame.MessageType,
                            PluginSidecarFrameFlags.None,
                            frame.RequestId,
                            frame.Request ?? throw new InvalidOperationException("Missing v4 request payload."),
                            PluginSidecarJsonContext.Default.PluginSidecarV4Request);
                    }
                }

                FlushResult flush = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (flush.IsCompleted)
                    throw new EndOfStreamException("Sidecar v4 writer completed.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            MarkBroken(ex);
        }
        finally
        {
            try
            {
                await writer.CompleteAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignore transport cleanup failures
            }
        }
    }

    private void CancelV4Request(ulong requestId, CancellationToken cancellationToken)
    {
        if (!_pending.TryRemove(requestId, out PendingCall? call))
            return;

        call.Completion.TrySetCanceled(cancellationToken);
        Channel<OutboundFrame>? outbound = _outbound;
        if (outbound is null)
            return;

        OutboundFrame cancel = new(PluginSidecarMessageType.Cancel, requestId, Request: null);
        if (!outbound.Writer.TryWrite(cancel))
            _ = EnqueueCancelAsync(outbound.Writer, cancel);
    }

    private async Task EnqueueCancelAsync(ChannelWriter<OutboundFrame> writer, OutboundFrame cancel)
    {
        try
        {
            CancellationToken token = _transportCancellation?.Token ?? CancellationToken.None;
            await writer.WriteAsync(cancel, token).ConfigureAwait(false);
        }
        catch
        {
            // Connection teardown will cancel the remote request as well.
        }
    }

    private void MarkBroken(Exception cause)
    {
        if (Interlocked.Exchange(ref _broken, 1) == 1)
            return;

        PortableLog.Warn("PluginSidecar", "v4 传输已中断：" + cause.Message);
        _outbound?.Writer.TryComplete(cause);
        try
        {
            _transportCancellation?.Cancel();
        }
        catch
        {
            // ignore
        }

        foreach ((ulong id, PendingCall pending) in _pending)
        {
            if (_pending.TryRemove(id, out _))
                pending.Completion.TrySetException(new IOException("插件侧车连接已中断。", cause));
        }

        Stream? stream = _stream;
        _stream = null;
        if (stream is null)
            return;

        try
        {
            stream.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    private static void ReportProgress(
        IProgress<PluginSidecarProgress>? progress,
        PluginSidecarProgress value)
    {
        try
        {
            progress?.Report(value);
        }
        catch (Exception ex)
        {
            PortableLog.Warn("PluginSidecar", "进度回调异常（已忽略）：" + ex.Message);
        }
    }

    private static InvalidOperationException CreateSidecarException(PluginSidecarError error) =>
        new($"Sidecar error {error.Code}: {error.Message}");

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _outbound?.Writer.TryComplete();
        try
        {
            _transportCancellation?.Cancel();
        }
        catch
        {
            // ignore
        }

        Stream? stream = _stream;
        _stream = null;
        if (stream is not null)
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        Task[] loops = new[] { _readLoop, _writeLoop }
            .Where(static task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (loops.Length > 0)
        {
            try
            {
                await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // bounded shutdown
            }
        }

        foreach ((ulong id, PendingCall pending) in _pending)
        {
            if (_pending.TryRemove(id, out _))
                pending.Completion.TrySetCanceled();
        }

        _transportCancellation?.Dispose();
        _pendingSlots.Dispose();
        _legacyGate.Dispose();
    }

    private sealed class PendingCall(IProgress<PluginSidecarProgress>? progress)
    {
        public TaskCompletionSource<PluginSidecarResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IProgress<PluginSidecarProgress>? Progress { get; } = progress;
    }

    private sealed record CancelState(
        PluginSidecarClient Client,
        ulong RequestId,
        CancellationToken Token);

    private readonly record struct OutboundFrame(
        PluginSidecarMessageType MessageType,
        ulong RequestId,
        PluginSidecarV4Request? Request);
}
