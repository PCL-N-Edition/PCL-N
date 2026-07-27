// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Application.Accounts;

/// <summary>
/// A Minecraft session supplied by an optional privileged runtime extension.
/// The host owns only this narrow launch contract; browser authorization,
/// service credentials, and online storage remain outside the launcher core.
/// </summary>
internal sealed record HostOnlineMinecraftSession(
    string Username,
    string Uuid,
    string AccessToken,
    string ClientToken,
    string AuthServer,
    string? SkinAddress);

internal sealed record HostOnlineSkinResult(
    string SkinAddress,
    bool IsSlim,
    string SourceKind,
    string? Sha1);

internal interface IHostOnlineMinecraftAccountProvider
{
    bool IsAuthenticated { get; }

    Task<HostOnlineMinecraftSession> CreateSessionAsync(
        CancellationToken cancellationToken = default);

    Task<HostOnlineSkinResult> UploadSkinAsync(
        ReadOnlyMemory<byte> png,
        bool isSlim,
        CancellationToken cancellationToken = default);

    Task<HostOnlineSkinResult> UseSkinSiteTextureAsync(
        string siteId,
        string textureId,
        bool isSlim,
        CancellationToken cancellationToken = default);
}

internal static class HostOnlineMinecraftAccountProvider
{
    private static readonly object Gate = new();
    private static IHostOnlineMinecraftAccountProvider? _current;

    public static IHostOnlineMinecraftAccountProvider? Current
    {
        get
        {
            lock (Gate)
                return _current;
        }
    }

    public static IDisposable Register(IHostOnlineMinecraftAccountProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (Gate)
        {
            if (_current is not null && !ReferenceEquals(_current, provider))
                throw new InvalidOperationException("在线 Minecraft 账户提供器已注册。");
            _current = provider;
        }
        return new Registration(provider);
    }

    private sealed class Registration(IHostOnlineMinecraftAccountProvider provider) : IDisposable
    {
        private IHostOnlineMinecraftAccountProvider? _provider = provider;

        public void Dispose()
        {
            IHostOnlineMinecraftAccountProvider? current =
                Interlocked.Exchange(ref _provider, null);
            if (current is null)
                return;
            lock (Gate)
            {
                if (ReferenceEquals(_current, current))
                    _current = null;
            }
        }
    }
}
