// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Accounts;

namespace PCL.Desktop.Hosting.PluginSidecar;

/// <summary>
/// Host-process bridge for N Cloud Minecraft sessions.
/// Online credentials live in the CoreCLR sidecar; the AOT host only talks over IPC.
/// </summary>
internal sealed class PluginSidecarOnlineMinecraftAccountProvider : IHostOnlineMinecraftAccountProvider
{
    public bool IsAuthenticated
    {
        get
        {
            try
            {
                PluginSidecarClient? client = PluginSidecarSupervisor.Instance.Client;
                if (client is null)
                    return false;

                // Launch/login paths are async and already off the critical paint path.
                PluginSidecarResult session = client
                    .FeedbackSessionAsync(CancellationToken.None)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
                return session.HasSession ||
                       string.Equals(session.SessionStatus, "authenticated", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task<HostOnlineMinecraftSession> CreateSessionAsync(
        CancellationToken cancellationToken = default)
    {
        PluginSidecarClient client = await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
        PluginSidecarResult result = await client
            .NCloudMinecraftSessionAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!result.Ok)
            throw new InvalidOperationException(result.Message ?? "创建 N Cloud Minecraft 会话失败。");

        if (string.IsNullOrWhiteSpace(result.MinecraftUsername) ||
            string.IsNullOrWhiteSpace(result.MinecraftUuid) ||
            string.IsNullOrWhiteSpace(result.MinecraftAccessToken) ||
            string.IsNullOrWhiteSpace(result.MinecraftClientToken) ||
            string.IsNullOrWhiteSpace(result.MinecraftAuthServer))
        {
            throw new InvalidOperationException(
                result.Message ?? "侧车未返回完整的 N Cloud Minecraft 会话。");
        }

        return new HostOnlineMinecraftSession(
            result.MinecraftUsername,
            result.MinecraftUuid,
            result.MinecraftAccessToken,
            result.MinecraftClientToken,
            result.MinecraftAuthServer,
            result.MinecraftSkinAddress);
    }

    public async Task<HostOnlineSkinResult> UploadSkinAsync(
        ReadOnlyMemory<byte> png,
        bool isSlim,
        CancellationToken cancellationToken = default)
    {
        if (png.IsEmpty)
            throw new ArgumentException("皮肤 PNG 不能为空。", nameof(png));

        PluginSidecarClient client = await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
        PluginSidecarResult result = await client
            .NCloudSkinUploadAsync(Convert.ToBase64String(png.Span), isSlim, cancellationToken)
            .ConfigureAwait(false);
        return RequireSkinResult(result);
    }

    public async Task<HostOnlineSkinResult> UseSkinSiteTextureAsync(
        string siteId,
        string textureId,
        bool isSlim,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(textureId);

        PluginSidecarClient client = await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
        PluginSidecarResult result = await client
            .NCloudSkinReferenceAsync(siteId, textureId, isSlim, cancellationToken)
            .ConfigureAwait(false);
        return RequireSkinResult(result);
    }

    private static async Task<PluginSidecarClient> EnsureClientAsync(CancellationToken cancellationToken)
    {
        if (!PluginSidecarSupervisor.Instance.IsAvailable)
        {
            bool started = await PluginSidecarSupervisor.Instance.TryStartAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!started)
            {
                throw new InvalidOperationException(
                    "插件侧车未运行，无法使用 N Cloud。请确认侧车已启动后重试。");
            }
        }

        return PluginSidecarSupervisor.Instance.Client
               ?? throw new InvalidOperationException("插件侧车未连接。");
    }

    private static HostOnlineSkinResult RequireSkinResult(PluginSidecarResult result)
    {
        if (!result.Ok || string.IsNullOrWhiteSpace(result.SkinAddress))
            throw new InvalidOperationException(result.Message ?? "更新 N Cloud 皮肤失败。");

        return new HostOnlineSkinResult(
            result.SkinAddress,
            result.SkinIsSlim,
            result.SkinSourceKind ?? "upload",
            result.SkinSha1);
    }
}
