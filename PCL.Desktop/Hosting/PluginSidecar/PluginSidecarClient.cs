// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Desktop.Hosting.PluginSidecar;

/// <summary>AOT-safe RPC client for the plugin sidecar process.</summary>
internal sealed class PluginSidecarClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Stream? _stream;
    private int _nextId;

    public bool IsConnected => _stream is { CanRead: true, CanWrite: true };

    public async Task ConnectAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stream = stream;
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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

            while (true)
            {
                PluginSidecarResponse? response = await PluginSidecarFraming.ReadAsync(
                        stream,
                        PluginSidecarJsonContext.Default.PluginSidecarResponse,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (response is null)
                    throw new InvalidOperationException("Empty sidecar response.");
                if (!string.Equals(response.Id, id, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Sidecar response id mismatch: expected {id}, got {response.Id}.");

                if (response.Progress is not null && response.Result is null && response.Error is null)
                {
                    progress?.Report(response.Progress);
                    continue;
                }

                if (response.Error is not null)
                    throw new InvalidOperationException($"Sidecar error {response.Error.Code}: {response.Error.Message}");
                return response.Result ?? new PluginSidecarResult { Ok = true };
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
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
            _gate.Release();
            _gate.Dispose();
        }
    }
}
