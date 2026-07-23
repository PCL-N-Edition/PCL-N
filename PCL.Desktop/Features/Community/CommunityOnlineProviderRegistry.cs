// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Desktop.Features.Community;

internal static class CommunityOnlineProviderRegistry
{
    private static readonly object Gate = new();
    private static ICommunityOnlineProvider? _provider;

    public static IDisposable Register(ICommunityOnlineProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (Gate)
        {
            if (_provider is not null)
                throw new InvalidOperationException("社区在线提供者已注册。");
            _provider = provider;
        }
        return new Registration(provider);
    }

    public static (ICommunityResourceCatalog Modrinth, ICommunityResourceCatalog CurseForge) CreateCatalogs()
    {
        ICommunityOnlineProvider provider = GetProvider();
        return provider.CreateCatalogs();
    }

    public static ICommunityTranslationService CreateTranslationService() =>
        GetProvider().CreateTranslationService();

    public static ICommunityArtifactDownloader CreateArtifactDownloader() =>
        GetProvider().CreateArtifactDownloader();

    private static ICommunityOnlineProvider GetProvider()
    {
        lock (Gate)
        {
            return _provider ?? throw new NotSupportedException(
                "当前构建未加载 PCL.Plugin，社区在线服务不可用。");
        }
    }

    private sealed class Registration(ICommunityOnlineProvider provider) : IDisposable
    {
        private ICommunityOnlineProvider? _provider = provider;

        public void Dispose()
        {
            ICommunityOnlineProvider? current = Interlocked.Exchange(ref _provider, null);
            if (current is null)
                return;
            lock (Gate)
            {
                if (ReferenceEquals(CommunityOnlineProviderRegistry._provider, current))
                    CommunityOnlineProviderRegistry._provider = null;
            }
        }
    }
}
