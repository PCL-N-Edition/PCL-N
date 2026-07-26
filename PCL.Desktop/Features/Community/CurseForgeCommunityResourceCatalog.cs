// Copyright (c) MUXUE1230. All rights reserved.
// Online implementation is owned by PCL.Plugin.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using PCL.Application.Settings;

namespace PCL.Desktop.Features.Community;

public sealed class CurseForgeCommunityResourceCatalog :
    ICommunityResourceCatalog,
    ICommunityResourceFingerprintLookup,
    IDisposable
{
    private const string ApiRoot = "https://api.curseforge.com/v1";
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly string? _apiKey;
    private readonly DownloadSourcePreference? _sourcePreference;

    public CurseForgeCommunityResourceCatalog()
        : this(CreateDefaultClient(), ResolveApiKey(), ownsClient: true)
    {
    }

    public CurseForgeCommunityResourceCatalog(HttpClient client, string? apiKey, bool ownsClient = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _ownsClient = ownsClient;
    }

    internal CurseForgeCommunityResourceCatalog(
        HttpClient client,
        string? apiKey,
        DownloadSourcePreference sourcePreference,
        bool ownsClient = false)
        : this(client, apiKey, ownsClient)
    {
        _sourcePreference = sourcePreference;
    }

    public async Task<IReadOnlyList<CommunityResourceEntry>> SearchAsync(
        CommunityResourceCategory category,
        string query,
        CommunitySearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new CommunitySearchOptions();
        List<string> parameters =
        [
            "gameId=432",
            "sortOrder=desc",
            // CurseForge rejects values above 50 with HTTP 400.
            "pageSize=50",
            "classId=" + GetClassId(category).ToString(CultureInfo.InvariantCulture),
            "sortField=" + GetSortField(options.Sort).ToString(CultureInfo.InvariantCulture)
        ];
        if (!string.IsNullOrWhiteSpace(query))
            parameters.Add("searchFilter=" + Uri.EscapeDataString(query.Trim()));
        // Dual tag format from UI: "{curseForgeCategoryId}/{modrinthSlug}". Only pass categoryId when set.
        // Never send categoryId=0 — that broke CurseForge search (WPF #2221).
        if (TryGetCurseForgeCategoryId(options.Tag, out int categoryId))
            parameters.Add("categoryId=" + categoryId.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(options.GameVersion))
            parameters.Add("gameVersion=" + Uri.EscapeDataString(options.GameVersion.Trim()));
        if (TryGetLoaderType(options.Loader, out int loaderType))
            parameters.Add("modLoaderType=" + loaderType.ToString(CultureInfo.InvariantCulture));

        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Get,
            ApiRoot + "/mods/search?" + string.Join('&', parameters),
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!TryGetProperty(document.RootElement, "data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
            return [];

        List<CommunityResourceEntry> entries = [];
        foreach (JsonElement project in data.EnumerateArray())
        {
            string id = ReadNumberOrString(project, "id");
            string slug = ReadString(project, "slug");
            string title = ReadString(project, "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
                continue;

            string? iconUrl = null;
            if (TryGetProperty(project, "logo", out JsonElement logo) && logo.ValueKind == JsonValueKind.Object)
                iconUrl = NullIfWhiteSpace(ReadString(logo, "thumbnailUrl")) ?? NullIfWhiteSpace(ReadString(logo, "url"));
            string? website = null;
            if (TryGetProperty(project, "links", out JsonElement links) && links.ValueKind == JsonValueKind.Object)
                website = NullIfWhiteSpace(ReadString(links, "websiteUrl"));

            entries.Add(new CommunityResourceEntry(
                id,
                slug,
                title,
                ReadString(project, "summary"),
                GetProjectType(category),
                iconUrl,
                ReadInt64(project, "downloadCount"),
                ReadDateTimeOffset(project, "dateModified"))
            {
                Source = CommunityResourceSource.CurseForge,
                ProjectUrl = website,
                Tags = ReadCategoryNames(project)
            });
        }

        return string.IsNullOrWhiteSpace(query) || options.Sort != CommunityResourceSort.Relevance
            ? entries
            : RankSearchResults(entries, query);
    }

    internal static IReadOnlyList<CommunityResourceEntry> RankSearchResults(
        IEnumerable<CommunityResourceEntry> entries,
        string query)
    {
        string term = query.Trim();
        return entries
            .OrderBy(entry => GetSearchRank(entry, term))
            .ThenByDescending(static entry => entry.Downloads)
            .ThenByDescending(static entry => entry.UpdatedAt ?? DateTimeOffset.MinValue)
            .ToArray();
    }

    internal static int GetSearchRank(CommunityResourceEntry entry, string query)
    {
        string title = entry.Title.Trim();
        string slug = entry.Slug.Trim();
        if (title.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (slug.Equals(query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (title.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (slug.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 3;
        if (title.Contains(query, StringComparison.OrdinalIgnoreCase)) return 4;
        if (slug.Contains(query, StringComparison.OrdinalIgnoreCase)) return 5;
        return 6;
    }

    public async Task<CommunityResourceDownloadFile?> ResolveDownloadAsync(
        CommunityResourceEntry entry,
        CommunitySearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CommunityResourceVersion> versions =
            await GetVersionsAsync(entry, options, cancellationToken).ConfigureAwait(false);
        return versions.SelectMany(static version => version.Files).FirstOrDefault();
    }

    public async Task<IReadOnlyList<CommunityResourceVersion>> GetVersionsAsync(
        CommunityResourceEntry entry,
        CommunitySearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        options ??= new CommunitySearchOptions();
        if (string.IsNullOrWhiteSpace(entry.ProjectId))
            return [];

        List<string> parameters = ["pageSize=50"];
        if (!string.IsNullOrWhiteSpace(options.GameVersion))
            parameters.Add("gameVersion=" + Uri.EscapeDataString(options.GameVersion.Trim()));
        if (TryGetLoaderType(options.Loader, out int loaderType))
            parameters.Add("modLoaderType=" + loaderType.ToString(CultureInfo.InvariantCulture));

        List<CommunityResourceVersion> versions = [];
        int index = 0;
        while (index < 10_000)
        {
            string url = ApiRoot + "/mods/" + Uri.EscapeDataString(entry.ProjectId) + "/files?" +
                         string.Join('&', parameters) + "&index=" + index.ToString(CultureInfo.InvariantCulture);
            using HttpResponseMessage response = await SendAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!TryGetProperty(document.RootElement, "data", out JsonElement data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            foreach (JsonElement file in data.EnumerateArray())
            {
                CommunityResourceVersion? parsed = ParseVersion(file);
                if (parsed is not null)
                    versions.Add(parsed);
            }

            int pageCount = data.GetArrayLength();
            if (pageCount == 0)
                break;

            int nextIndex = index + pageCount;
            long totalCount = TryGetProperty(document.RootElement, "pagination", out JsonElement pagination)
                ? ReadInt64(pagination, "totalCount")
                : 0L;
            if (totalCount > 0 ? nextIndex >= totalCount : pageCount < 50)
                break;
            index = nextIndex;
        }

        return versions
            .OrderByDescending(static version => version.PublishedAt ?? DateTimeOffset.MinValue)
            .ToArray();
    }

    private static CommunityResourceVersion? ParseVersion(JsonElement file)
    {
        string id = ReadNumberOrString(file, "id");
        string fileName = ReadString(file, "fileName");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(fileName))
            return null;

        string urlValue = ReadString(file, "downloadUrl");
        if (string.IsNullOrWhiteSpace(urlValue))
            urlValue = CreateForgeCdnUrl(id, fileName);
        urlValue = NormalizeDownloadUrl(urlValue);

        string displayName = NullIfWhiteSpace(ReadString(file, "displayName")) ?? fileName;
        List<string> gameVersions = ReadStringArray(file, "gameVersions");
        List<string> loaders = gameVersions
            .Where(static value => value.Equals("Forge", StringComparison.OrdinalIgnoreCase) ||
                                   value.Equals("NeoForge", StringComparison.OrdinalIgnoreCase) ||
                                   value.Equals("Fabric", StringComparison.OrdinalIgnoreCase) ||
                                   value.Equals("Quilt", StringComparison.OrdinalIgnoreCase) ||
                                   value.Equals("LiteLoader", StringComparison.OrdinalIgnoreCase))
            .Select(static value => value.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<string> minecraftVersions = gameVersions
            .Where(static value => char.IsDigit(value.FirstOrDefault()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        CommunityResourceDownloadFile download = new(
            fileName,
            urlValue,
            ReadInt64(file, "fileLength"),
            id,
            displayName)
        {
            Source = CommunityResourceSource.CurseForge,
            Sha1 = ReadSha1(file),
            Sha256 = ReadSha256(file),
            CandidateUrls = McimMirrorPolicy.DownloadCandidates(
                urlValue,
                CommunityResourceSource.CurseForge,
                McimMirrorPolicy.CurrentPreference)
        };
        return new CommunityResourceVersion(
            id,
            displayName,
            displayName,
            NullIfWhiteSpace(ReadString(file, "changelog")),
            ReadDateTimeOffset(file, "fileDate"),
            minecraftVersions,
            loaders,
            [download])
        {
            Dependencies = ReadCurseForgeDependencies(file),
            Source = CommunityResourceSource.CurseForge
        };
    }

    public async Task<CommunityResourceEntry?> GetProjectAsync(
        CommunityResourceSource source,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        if (source == CommunityResourceSource.Modrinth || string.IsNullOrWhiteSpace(projectId))
            return null;

        string url = ApiRoot + "/mods/" + Uri.EscapeDataString(projectId.Trim());
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!TryGetProperty(document.RootElement, "data", out JsonElement project) ||
            project.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string id = ReadNumberOrString(project, "id");
        string title = ReadString(project, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            return null;
        string? iconUrl = null;
        if (TryGetProperty(project, "logo", out JsonElement logo) && logo.ValueKind == JsonValueKind.Object)
            iconUrl = NullIfWhiteSpace(ReadString(logo, "thumbnailUrl")) ?? NullIfWhiteSpace(ReadString(logo, "url"));
        string? website = null;
        if (TryGetProperty(project, "links", out JsonElement links) && links.ValueKind == JsonValueKind.Object)
            website = NullIfWhiteSpace(ReadString(links, "websiteUrl"));

        return new CommunityResourceEntry(
            id,
            ReadString(project, "slug"),
            title,
            ReadString(project, "summary"),
            "mod",
            iconUrl,
            ReadInt64(project, "downloadCount"),
            ReadDateTimeOffset(project, "dateModified"))
        {
            Source = CommunityResourceSource.CurseForge,
            ProjectUrl = website,
            Tags = ReadCategoryNames(project)
        };
    }

    public Task<CommunityResourceFileIdentity?> LookupFileBySha1Async(
        string sha1Hex,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<CommunityResourceFileIdentity?>(null);

    public async Task<CommunityResourceFileIdentity?> LookupFileByFingerprintAsync(
        uint fingerprint,
        CancellationToken cancellationToken = default)
    {
        string body = "{\"fingerprints\":[" +
                      fingerprint.ToString(CultureInfo.InvariantCulture) +
                      "]}";
        using HttpResponseMessage response = await SendAsync(
                HttpMethod.Post,
                ApiRoot + "/fingerprints/432",
                body,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!TryGetProperty(document.RootElement, "data", out JsonElement data) ||
            !TryGetProperty(data, "exactMatches", out JsonElement matches) ||
            matches.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement match in matches.EnumerateArray())
        {
            if (!TryGetProperty(match, "file", out JsonElement file) ||
                file.ValueKind != JsonValueKind.Object ||
                ReadInt64(file, "fileFingerprint") != fingerprint)
            {
                continue;
            }

            string projectId = ReadNumberOrString(file, "modId");
            if (string.IsNullOrWhiteSpace(projectId))
                projectId = ReadNumberOrString(match, "id");
            CommunityResourceVersion? version = ParseVersion(file);
            CommunityResourceDownloadFile? currentFile = version is { Files.Count: > 0 }
                ? version.Files[0]
                : null;
            if (string.IsNullOrWhiteSpace(projectId) || version is null || currentFile is null)
                continue;

            return new CommunityResourceFileIdentity(
                projectId,
                projectId,
                projectId,
                "mod",
                version.VersionId,
                version.VersionNumber,
                version.PublishedAt,
                null,
                "https://www.curseforge.com/minecraft/mc-mods/" + projectId)
            {
                Source = CommunityResourceSource.CurseForge,
                CurrentFile = currentFile
            };
        }

        return null;
    }

    public async Task<CommunityResourceVersion?> GetLatestVersionAsync(
        string projectId,
        CommunitySearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CommunityResourceEntry entry = new(projectId, projectId, projectId, string.Empty, "mod", null, 0L, null)
        {
            Source = CommunityResourceSource.CurseForge
        };
        IReadOnlyList<CommunityResourceVersion> versions = await GetVersionsAsync(entry, options, cancellationToken)
            .ConfigureAwait(false);
        return versions.Count == 0 ? null : versions[0];
    }

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, CancellationToken cancellationToken)
        => await SendAsync(method, url, jsonBody: null, cancellationToken).ConfigureAwait(false);

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        string? jsonBody,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        HttpResponseMessage? lastNotFound = null;
        foreach (string candidate in McimMirrorPolicy.ApiCandidates(
                     url,
                     CommunityResourceSource.CurseForge,
                     _sourcePreference ?? McimMirrorPolicy.CurrentPreference))
        {
            bool isOfficialApi = IsOfficialApi(candidate);
            if (isOfficialApi && string.IsNullOrWhiteSpace(_apiKey))
            {
                lastError = new InvalidOperationException(
                    "CurseForge API 密钥未配置，请设置 PCL_CURSEFORGE_API_KEY。");
                continue;
            }

            HttpResponseMessage? response = null;
            try
            {
                using HttpRequestMessage request = new(method, candidate);
                if (isOfficialApi)
                    request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
                if (jsonBody is not null)
                    request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                response = await _client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    lastNotFound?.Dispose();
                    lastNotFound = response;
                    response = null;
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    lastError = new HttpRequestException($"CurseForge API returned {(int)response.StatusCode}.");
                    continue;
                }

                await BufferAndValidateJsonAsync(response, cancellationToken).ConfigureAwait(false);
                HttpResponseMessage result = response;
                response = null;
                lastNotFound?.Dispose();
                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new TimeoutException("CurseForge API request timed out.");
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
            }
            catch (JsonException ex)
            {
                lastError = ex;
            }
            catch (IOException ex)
            {
                lastError = ex;
            }
            finally
            {
                response?.Dispose();
            }
        }

        if (lastError is null && lastNotFound is not null)
            return lastNotFound;
        lastNotFound?.Dispose();
        throw lastError ?? new HttpRequestException("CurseForge API request failed.");
    }

    private static bool IsOfficialApi(string candidate) =>
        Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) &&
        uri.Host.Equals("api.curseforge.com", StringComparison.OrdinalIgnoreCase);

    private static async Task BufferAndValidateJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        HttpContent originalContent = response.Content;
        byte[] payload = await originalContent.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(payload);
        ByteArrayContent bufferedContent = new(payload);
        foreach (KeyValuePair<string, IEnumerable<string>> header in originalContent.Headers)
            bufferedContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = bufferedContent;
        originalContent.Dispose();
    }

    private static HttpClient CreateDefaultClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PCL-N", "1.0"));
        return client;
    }

    private static string? ResolveApiKey() =>
        NullIfWhiteSpace(Environment.GetEnvironmentVariable("PCL_CURSEFORGE_API_KEY")) ??
        NullIfWhiteSpace(Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY"));

    private static int GetClassId(CommunityResourceCategory category) => category switch
    {
        CommunityResourceCategory.Mod => 6,
        CommunityResourceCategory.Modpack => 4471,
        CommunityResourceCategory.DataPack => 6945,
        CommunityResourceCategory.ResourcePack => 12,
        CommunityResourceCategory.Shader => 6552,
        CommunityResourceCategory.World => 17,
        _ => 6
    };

    private static int GetSortField(CommunityResourceSort sort) => sort switch
    {
        CommunityResourceSort.Downloads => 6,
        CommunityResourceSort.Updated => 3,
        _ => 2
    };

    /// <summary>
    /// Parses dual tags like <c>412/technology</c> or CurseForge-only <c>412/</c>.
    /// Returns false when the CurseForge half is empty so we omit categoryId entirely.
    /// </summary>
    internal static bool TryGetCurseForgeCategoryId(string? tag, out int categoryId)
    {
        categoryId = 0;
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        string raw = tag.Trim();
        int slash = raw.IndexOf('/');
        string cursePart = slash >= 0 ? raw[..slash] : raw;
        if (string.IsNullOrWhiteSpace(cursePart))
            return false;
        if (!int.TryParse(cursePart, NumberStyles.None, CultureInfo.InvariantCulture, out categoryId) ||
            categoryId <= 0)
        {
            categoryId = 0;
            return false;
        }

        return true;
    }

    private static bool TryGetLoaderType(string? loader, out int type)
    {
        type = loader?.Trim().ToLowerInvariant() switch
        {
            "forge" => 1,
            "fabric" => 4,
            "quilt" => 5,
            "neoforge" => 6,
            _ => 0
        };
        return type != 0;
    }

    private static List<CommunityResourceDependency> ReadCurseForgeDependencies(JsonElement file)
    {
        if (!TryGetProperty(file, "dependencies", out JsonElement dependencies) ||
            dependencies.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<CommunityResourceDependency> result = [];
        foreach (JsonElement dependency in dependencies.EnumerateArray())
        {
            string projectId = ReadNumberOrString(dependency, "modId");
            if (string.IsNullOrWhiteSpace(projectId))
                continue;
            int relationType = TryGetProperty(dependency, "relationType", out JsonElement relation) &&
                               relation.TryGetInt32(out int parsed)
                ? parsed
                : 0;
            CommunityResourceDependencyType type = relationType switch
            {
                3 => CommunityResourceDependencyType.Required,
                2 => CommunityResourceDependencyType.Optional,
                5 => CommunityResourceDependencyType.Incompatible,
                1 or 6 => CommunityResourceDependencyType.Embedded,
                4 => CommunityResourceDependencyType.Tool,
                _ => CommunityResourceDependencyType.Unknown
            };
            result.Add(new CommunityResourceDependency(
                projectId,
                null,
                null,
                type,
                CommunityResourceSource.CurseForge));
        }

        return result;
    }

    private static string GetProjectType(CommunityResourceCategory category) => category switch
    {
        CommunityResourceCategory.Modpack => "modpack",
        CommunityResourceCategory.DataPack => "datapack",
        CommunityResourceCategory.ResourcePack => "resourcepack",
        CommunityResourceCategory.Shader => "shader",
        CommunityResourceCategory.World => "world",
        _ => "mod"
    };

    private static string CreateForgeCdnUrl(string id, string fileName)
    {
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out long numericId))
            throw new InvalidDataException("CurseForge 返回的文件 ID 无效。");
        long group = numericId / 1000L;
        long suffix = numericId % 1000L;
        return $"https://edge.forgecdn.net/files/{group.ToString(CultureInfo.InvariantCulture)}/{suffix.ToString("000", CultureInfo.InvariantCulture)}/{Uri.EscapeDataString(fileName)}";
    }

    private static string NormalizeDownloadUrl(string url) => url
        .Replace("-service.overwolf.wtf", ".forgecdn.net", StringComparison.OrdinalIgnoreCase)
        .Replace("://mediafilez.", "://edge.", StringComparison.OrdinalIgnoreCase)
        .Replace("://media.", "://edge.", StringComparison.OrdinalIgnoreCase)
        .Replace(" ", "%20", StringComparison.Ordinal);

    private static string ReadString(JsonElement element, string name) =>
        TryGetProperty(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string ReadNumberOrString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out JsonElement value))
            return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static long ReadInt64(JsonElement element, string name) =>
        TryGetProperty(element, name, out JsonElement value) && value.TryGetInt64(out long result) ? result : 0L;

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string name) =>
        DateTimeOffset.TryParse(ReadString(element, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset value)
            ? value
            : null;

    private static List<string> ReadStringArray(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            return [];
        return array.EnumerateArray()
            .Where(static value => value.ValueKind == JsonValueKind.String)
            .Select(static value => value.GetString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .ToList();
    }

    private static List<string> ReadCategoryNames(JsonElement project)
    {
        if (!TryGetProperty(project, "categories", out JsonElement categories) ||
            categories.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return categories.EnumerateArray()
            .Select(static category => ReadString(category, "name"))
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ReadSha256(JsonElement file)
        => ReadHash(file, 3, CommunityResourceMerge.NormalizeSha256);

    private static string? ReadSha1(JsonElement file)
        => ReadHash(file, 1, CommunityResourceMerge.NormalizeSha1);

    private static string? ReadHash(
        JsonElement file,
        long algorithm,
        Func<string?, string?> normalize)
    {
        if (!TryGetProperty(file, "hashes", out JsonElement hashes) ||
            hashes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement hash in hashes.EnumerateArray())
        {
            if (ReadInt64(hash, "algo") == algorithm)
                return normalize(ReadString(hash, "value"));
        }
        return null;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
            return true;
        value = default;
        return false;
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
