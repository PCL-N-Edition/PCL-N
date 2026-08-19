// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Headers;
using System.Text.Json;
using PCL.Core.IO.Net;

namespace PCL.Application.Accounts;

public sealed record MinecraftSkinUploadResult(string? SkinAddress);

public interface IMinecraftSkinService
{
    Task<MinecraftSkinUploadResult> UploadAsync(
        string accessToken,
        byte[] pngBytes,
        string fileName,
        bool isSlim,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Uploads a skin owned by the authenticated Microsoft Minecraft profile.
/// The JSON profile response is parsed for the canonical active texture URL.
/// </summary>
public sealed class MinecraftSkinService : IMinecraftSkinService
{
    private static readonly Uri SkinEndpoint =
        new("https://api.minecraftservices.com/minecraft/profile/skins");

    private readonly HttpClient _client;

    public MinecraftSkinService()
        : this(PortableHttp.Client)
    {
    }

    public MinecraftSkinService(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<MinecraftSkinUploadResult> UploadAsync(
        string accessToken,
        byte[] pngBytes,
        string fileName,
        bool isSlim,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentNullException.ThrowIfNull(pngBytes);
        if (pngBytes.Length == 0)
            throw new ArgumentException("皮肤文件为空。", nameof(pngBytes));

        using MultipartFormDataContent content = new();
        content.Add(new StringContent(isSlim ? "slim" : "classic"), "variant");
        ByteArrayContent fileContent = new(pngBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(
            fileContent,
            "file",
            string.IsNullOrWhiteSpace(fileName) ? "skin.png" : Path.GetFileName(fileName));

        using HttpRequestMessage request = new(HttpMethod.Post, SkinEndpoint)
        {
            Content = content
        };
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        string body = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response, body);
        return new MinecraftSkinUploadResult(ParseActiveSkinAddress(body));
    }

    internal static string? ParseActiveSkinAddress(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("skins", out JsonElement skins) ||
                skins.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? fallback = null;
            foreach (JsonElement skin in skins.EnumerateArray())
            {
                if (skin.ValueKind != JsonValueKind.Object)
                    continue;
                string state = ReadString(skin, "state");
                string address = ReadString(skin, "url");
                if (!TryNormalizeHttpAddress(address, out string? normalized))
                    continue;

                fallback ??= normalized;
                if (string.Equals(state, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                    return normalized;
            }

            return fallback;
        }
        catch (JsonException)
        {
            // A malformed success body must not replace the uploaded local preview.
            return null;
        }
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static bool TryNormalizeHttpAddress(string address, out string? normalized)
    {
        normalized = null;
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        normalized = uri.AbsoluteUri;
        return true;
    }

    private static void EnsureSuccess(HttpResponseMessage response, string responseBody)
    {
        if (response.IsSuccessStatusCode)
            return;

        string detail = string.IsNullOrWhiteSpace(responseBody)
            ? $"{(int)response.StatusCode} {response.ReasonPhrase}"
            : responseBody.Trim();
        throw new HttpRequestException(
            "更换正版皮肤失败：" + detail,
            null,
            response.StatusCode);
    }
}
