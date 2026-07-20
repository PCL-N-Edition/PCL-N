// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCL.Core.Logging;
using PCL.Platform.Abstractions.Security;
using PCL.Platform.Paths;
using PCL.Platform.Security;

namespace PCL.Desktop.Features.Launching;

/// <summary>
/// Encrypted third-party (Yggdrasil / LittleSkin) login secrets for silent re-auth.
/// Uses platform secure storage (Windows DPAPI / macOS Keychain / Linux Secret Service).
/// Never write passwords into launch-profiles.json.
/// </summary>
internal static class ThirdPartyCredentialStore
{
    private const string KeyPrefix = "third-party-auth/v1/";

    public static async Task SaveAsync(
        string authServer,
        string profileUuid,
        string loginUsername,
        string password,
        string? clientToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authServer);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileUuid);
        ArgumentException.ThrowIfNullOrWhiteSpace(loginUsername);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        ThirdPartyStoredCredential payload = new(
            NormalizeServer(authServer),
            NormalizeUuid(profileUuid),
            loginUsername.Trim(),
            password,
            string.IsNullOrWhiteSpace(clientToken) ? null : clientToken.Trim());

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            ThirdPartyCredentialJsonContext.Default.ThirdPartyStoredCredential);
        try
        {
            SecureStorageOperationResult result = await CreateStorage()
                .WriteAsync(BuildKey(payload.AuthServer, payload.ProfileUuid), bytes, cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsSuccess || result.Status == SecureStorageStatus.Failed)
            {
                PortableLog.Warn(
                    "ThirdPartyAuth",
                    $"加密保存第三方凭据失败：{result.Message ?? result.Status.ToString()}。自动刷新可能不可用。");
            }
            else
            {
                PortableLog.Info(
                    "ThirdPartyAuth",
                    $"已加密保存第三方凭据；服务器={GetHost(payload.AuthServer)}；档案={payload.ProfileUuid[..Math.Min(8, payload.ProfileUuid.Length)]}…。");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static async Task<ThirdPartyStoredCredential?> TryReadAsync(
        string authServer,
        string profileUuid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authServer) || string.IsNullOrWhiteSpace(profileUuid))
            return null;

        string key = BuildKey(NormalizeServer(authServer), NormalizeUuid(profileUuid));
        SecureStorageReadResult result = await CreateStorage()
            .ReadAsync(key, cancellationToken)
            .ConfigureAwait(false);
        if (result is not { Status: SecureStorageStatus.Success, Value: { Length: > 0 } value })
            return null;

        try
        {
            ThirdPartyStoredCredential? credential = JsonSerializer.Deserialize(
                value,
                ThirdPartyCredentialJsonContext.Default.ThirdPartyStoredCredential);
            if (credential is null ||
                string.IsNullOrWhiteSpace(credential.LoginUsername) ||
                string.IsNullOrWhiteSpace(credential.Password))
            {
                return null;
            }

            return credential;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            PortableLog.Warn(ex, "ThirdPartyAuth", "解密后的第三方凭据无法解析，已忽略。");
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    public static async Task DeleteAsync(
        string authServer,
        string profileUuid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authServer) || string.IsNullOrWhiteSpace(profileUuid))
            return;

        SecureStorageOperationResult result = await CreateStorage()
            .DeleteAsync(BuildKey(NormalizeServer(authServer), NormalizeUuid(profileUuid)), cancellationToken)
            .ConfigureAwait(false);
        if (result.Status == SecureStorageStatus.Success)
        {
            PortableLog.Info(
                "ThirdPartyAuth",
                $"已删除加密第三方凭据；服务器={GetHost(NormalizeServer(authServer))}。");
        }
    }

    private static string BuildKey(string authServer, string profileUuid) =>
        KeyPrefix + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(authServer + "\0" + profileUuid)))
            .ToLowerInvariant();

    private static string NormalizeServer(string authServer)
    {
        string normalized = authServer.Trim().TrimEnd('/');
        if (!normalized.Contains("://", StringComparison.Ordinal))
            normalized = "https://" + normalized;
        return normalized.ToLowerInvariant();
    }

    private static string NormalizeUuid(string uuid) =>
        new string(uuid.Where(static ch => ch is not ('-' or ' ')).ToArray()).ToLowerInvariant();

    private static string GetHost(string authServer) =>
        Uri.TryCreate(authServer, UriKind.Absolute, out Uri? uri) ? uri.Host : authServer;

    private static DefaultSecureStorage CreateStorage()
    {
        DefaultPlatformPathProvider paths = new();
        return new DefaultSecureStorage(paths.ApplicationDataDirectory);
    }
}

internal sealed record ThirdPartyStoredCredential(
    string AuthServer,
    string ProfileUuid,
    string LoginUsername,
    string Password,
    string? ClientToken);
