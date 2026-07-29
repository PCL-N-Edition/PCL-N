// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Core.Logging;

namespace PCL.Desktop.Hosting.PluginSidecar;

/// <summary>AOT-safe RPC client for the plugin sidecar process.</summary>
internal sealed class PluginSidecarClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Stream? _stream;
    private int _nextId;
    private int _disposed;
    private int _broken;

    public bool IsConnected =>
        _stream is { CanRead: true, CanWrite: true } &&
        _disposed == 0 &&
        _broken == 0;

    /// <summary>True when the pipe desynced and must be restarted (not merely disposed).</summary>
    public bool IsBroken => _broken != 0;

    public async Task ConnectAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stream = stream;
            Volatile.Write(ref _broken, 0);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<PluginSidecarResult> HelloAsync(string token, CancellationToken cancellationToken = default) =>
        CallAsync(
            PluginSidecarMethods.SystemHello,
            new PluginSidecarParams { Token = token },
            cancellationToken);

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

    public Task<PluginSidecarResult> SetEnabledAsync(
        string pluginId,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        CallAsync(
            PluginSidecarMethods.CatalogSetEnabled,
            new PluginSidecarParams { PluginId = pluginId, Enabled = enabled },
            cancellationToken);

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
        if (_disposed != 0)
            throw new ObjectDisposedException(nameof(PluginSidecarClient));
        if (_broken != 0)
            throw new InvalidOperationException("插件侧车连接已损坏，请刷新页面或重启启动器以重建连接。");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool wroteRequest = false;
        try
        {
            if (_broken != 0 || _disposed != 0)
                throw new InvalidOperationException("插件侧车连接不可用。");

            Stream stream = _stream ?? throw new InvalidOperationException("Sidecar client is not connected.");
            string id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
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

            // Tolerate stray/out-of-order frames (stale progress after cancel, half-dead peer)
            // without permanently poisoning every subsequent refresh with "expected N got M".
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
                    MarkBroken();
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
                        MarkBroken();
                        throw new InvalidOperationException(
                            $"插件侧车传输异常：expected {id}, got {response.Id}。" +
                            "连接已标记为损坏；请刷新插件页或重启启动器。若仍无法启动，请在任务管理器结束 PCL-N-Edition 与 PCL.Plugin.Sidecar。");
                    }

                    continue;
                }

                if (response.Progress is not null && response.Result is null && response.Error is null)
                {
                    try
                    {
                        progress?.Report(response.Progress);
                    }
                    catch (Exception ex)
                    {
                        PortableLog.Warn("PluginSidecar", "进度回调异常（已忽略）：" + ex.Message);
                    }

                    continue;
                }

                if (response.Error is not null)
                    throw new InvalidOperationException($"Sidecar error {response.Error.Code}: {response.Error.Message}");
                return response.Result ?? new PluginSidecarResult { Ok = true };
            }
        }
        catch (OperationCanceledException) when (wroteRequest)
        {
            // Half-finished RPC desyncs the pipe — force reconnect on next use.
            MarkBroken();
            throw;
        }
        catch (IOException)
        {
            MarkBroken();
            throw;
        }
        catch (ObjectDisposedException)
        {
            MarkBroken();
            throw;
        }
        finally
        {
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // shutting down
            }
        }
    }

    private void MarkBroken()
    {
        if (Interlocked.Exchange(ref _broken, 1) == 1)
            return;

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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        try
        {
            // Bounded wait so host Exit cannot hang forever on a wedged pipe.
            if (!await _gate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
            {
                try
                {
                    if (_stream is not null)
                        await _stream.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }

                _stream = null;
                return;
            }

            try
            {
                if (_stream is not null)
                {
                    await _stream.DisposeAsync().ConfigureAwait(false);
                    _stream = null;
                }
            }
            finally
            {
                try
                {
                    _gate.Release();
                }
                catch (ObjectDisposedException)
                {
                    // ignore
                }
            }
        }
        catch
        {
            // ignore dispose races
        }
        finally
        {
            try
            {
                _gate.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }
}
