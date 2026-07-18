// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCL.Core.Logging;

namespace PCL.Desktop.Features.Community;

public sealed record McimTranslationResult(string? Text, bool NotFound = false, bool FromCache = false);

public sealed class McimTranslationService
{
    private readonly HttpClient _client;
    private readonly string _cacheDirectory;

    public McimTranslationService(HttpClient client, string? cacheDirectory = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _cacheDirectory = cacheDirectory ?? Path.Combine(Path.GetTempPath(), "PCL-N", "Cache", "McimTranslations");
    }

    public async Task<McimTranslationResult> GetAsync(
        CommunityResourceEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string source = entry.Source == CommunityResourceSource.CurseForge ? "curseforge" : "modrinth";
        string cacheKey = CreateCacheKey(source, entry.ProjectId, entry.Description);
        string cachePath = Path.Combine(_cacheDirectory, cacheKey + ".txt");
        if (File.Exists(cachePath))
        {
            string cached = await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false);
            return new McimTranslationResult(cached, FromCache: true);
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        string url = "https://mod.mcimirror.top/translate/" + source + "/" + Uri.EscapeDataString(entry.ProjectId);
        PortableLog.Info("MCIM", $"请求中文描述；来源={source}；项目={entry.ProjectId}。");
        using HttpResponseMessage response = await _client.GetAsync(url, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return new McimTranslationResult(null, NotFound: true);
        response.EnsureSuccessStatusCode();
        string payload = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        string? translated = ParseTranslation(payload);
        if (string.IsNullOrWhiteSpace(translated))
            return new McimTranslationResult(null, NotFound: true);

        Directory.CreateDirectory(_cacheDirectory);
        await File.WriteAllTextAsync(cachePath, translated, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return new McimTranslationResult(translated);
    }

    internal static string CreateCacheKey(string source, string projectId, string description)
    {
        byte[] descriptionHash = SHA256.HashData(Encoding.UTF8.GetBytes(description ?? string.Empty));
        string safeProject = string.Concat(projectId.Where(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        return source + "-" + safeProject + "-" + Convert.ToHexString(descriptionHash).ToLowerInvariant();
    }

    internal static string? ParseTranslation(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
                return root.GetString();
            foreach (string key in new[] { "translated", "translation", "description", "content", "text" })
            {
                if (root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
                if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty(key, out value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
            return null;
        }
        catch (JsonException)
        {
            return payload.Trim();
        }
    }
}
