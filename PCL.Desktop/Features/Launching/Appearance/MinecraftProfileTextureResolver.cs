// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Features.Launching.Views;

namespace PCL.Desktop.Features.Launching.Appearance;

public sealed record MinecraftProfileTextures(
    string SkinAddress,
    string? CapeAddress,
    bool IsSlim);

/// <summary>
/// Resolves the signed session profile once so the appearance surface can render
/// both skin and cape. Failure is deliberately best-effort: the launch profile's
/// known skin remains usable while a missing cape does not block the page.
/// </summary>
public static class MinecraftProfileTextureResolver
{
    private static readonly HttpClient Client = CreateClient();

    public static async Task<MinecraftProfileTextures> ResolveAsync(
        LoginProfileInfo profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        string fallbackSkin = profile.DisplaySkinAddress;
        bool fallbackSlim = profile.Kind == LaunchLoginProfileKind.Offline &&
                            string.Equals(
                                LoginProfileInfo.ResolveOfflineDefaultModel(profile.Uuid),
                                "Alex",
                                StringComparison.OrdinalIgnoreCase);
        Uri? sessionUri = CreateSessionProfileUri(profile);
        if (sessionUri is null)
            return new MinecraftProfileTextures(fallbackSkin, null, fallbackSlim);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, sessionUri);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using HttpResponseMessage response = await Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new MinecraftProfileTextures(fallbackSkin, null, fallbackSlim);

            await using Stream stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return ParseSessionProfile(document.RootElement, fallbackSkin, fallbackSlim);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            IOException or
            JsonException or
            FormatException or
            TaskCanceledException)
        {
            return new MinecraftProfileTextures(fallbackSkin, null, fallbackSlim);
        }
    }

    internal static MinecraftProfileTextures ParseSessionProfile(
        JsonElement profile,
        string fallbackSkin,
        bool fallbackSlim = false)
    {
        if (!profile.TryGetProperty("properties", out JsonElement properties) ||
            properties.ValueKind != JsonValueKind.Array)
        {
            return new MinecraftProfileTextures(fallbackSkin, null, fallbackSlim);
        }

        foreach (JsonElement property in properties.EnumerateArray())
        {
            if (property.ValueKind != JsonValueKind.Object ||
                !property.TryGetProperty("name", out JsonElement nameElement) ||
                !string.Equals(nameElement.GetString(), "textures", StringComparison.OrdinalIgnoreCase) ||
                !property.TryGetProperty("value", out JsonElement valueElement))
            {
                continue;
            }

            string? encoded = valueElement.GetString();
            if (string.IsNullOrWhiteSpace(encoded))
                continue;

            using JsonDocument textureDocument = JsonDocument.Parse(Convert.FromBase64String(encoded));
            if (!textureDocument.RootElement.TryGetProperty("textures", out JsonElement textures) ||
                textures.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string skin = GetTextureUrl(textures, "SKIN") ?? fallbackSkin;
            string? cape = GetTextureUrl(textures, "CAPE");
            bool slim = fallbackSlim;
            if (textures.TryGetProperty("SKIN", out JsonElement skinElement) &&
                skinElement.ValueKind == JsonValueKind.Object &&
                skinElement.TryGetProperty("metadata", out JsonElement metadata) &&
                metadata.ValueKind == JsonValueKind.Object &&
                metadata.TryGetProperty("model", out JsonElement model))
            {
                slim = string.Equals(model.GetString(), "slim", StringComparison.OrdinalIgnoreCase);
            }

            return new MinecraftProfileTextures(skin, cape, slim);
        }

        return new MinecraftProfileTextures(fallbackSkin, null, fallbackSlim);
    }

    private static string? GetTextureUrl(JsonElement textures, string propertyName)
    {
        if (!textures.TryGetProperty(propertyName, out JsonElement texture) ||
            texture.ValueKind != JsonValueKind.Object ||
            !texture.TryGetProperty("url", out JsonElement urlElement))
        {
            return null;
        }

        string? value = urlElement.GetString();
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
               (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            ? uri.AbsoluteUri
            : null;
    }

    private static Uri? CreateSessionProfileUri(LoginProfileInfo profile)
    {
        string uuid = NormalizeUuid(profile.Uuid);
        if (uuid.Length != 32 || profile.Kind == LaunchLoginProfileKind.Offline)
            return null;

        if (profile.UsesYggdrasil)
        {
            string address = MySkin.ResolveSkinAddress(null, uuid, profile.AuthServer);
            return Uri.TryCreate(address, UriKind.Absolute, out Uri? thirdPartyUri)
                ? thirdPartyUri
                : null;
        }

        return new Uri("https://sessionserver.mojang.com/session/minecraft/profile/" + uuid);
    }

    private static string NormalizeUuid(string? uuid) =>
        string.IsNullOrWhiteSpace(uuid)
            ? string.Empty
            : new string(uuid.Where(static character => character is not ('-' or ' ')).ToArray())
                .ToLowerInvariant();

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-N/1.0");
        return client;
    }
}
