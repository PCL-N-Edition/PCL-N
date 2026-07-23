// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

using PCL.Desktop.Localization;

namespace PCL.Desktop.Features.Community;


public sealed class ModrinthCommunityResourceCatalog :
    ICommunityResourceCatalog,
    ICommunityResourceVersionLookup,
    IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public ModrinthCommunityResourceCatalog()
        : this(CreateDefaultClient(), ownsClient: true)
    {
    }

    public ModrinthCommunityResourceCatalog(HttpClient client, bool ownsClient = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;
    }

    public async Task<IReadOnlyList<CommunityResourceEntry>> SearchAsync(
        CommunityResourceCategory category,
        string query,
        CommunitySearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new CommunitySearchOptions();
        string facets = CreateFacets(category, options);
        string index = options.Sort switch
        {
            CommunityResourceSort.Downloads => "downloads",
            CommunityResourceSort.Updated => "updated",
            _ => "relevance"
        };
        string requestUrl = "https://api.modrinth.com/v2/search?limit=80&index=" + index +
                            "&query=" + Uri.EscapeDataString(query?.Trim() ?? string.Empty) +
                            "&facets=" + Uri.EscapeDataString(facets);
        using HttpResponseMessage response = await GetWithFallbackAsync(requestUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!TryGetProperty(document.RootElement, "hits", out JsonElement hits) || hits.ValueKind != JsonValueKind.Array)
            return [];

        List<CommunityResourceEntry> entries = [];
        foreach (JsonElement hit in hits.EnumerateArray())
        {
            if (hit.ValueKind != JsonValueKind.Object)
                continue;

            string projectId = ReadString(hit, "project_id");
            string slug = ReadString(hit, "slug");
            string title = ReadString(hit, "title");
            if (string.IsNullOrWhiteSpace(projectId) && string.IsNullOrWhiteSpace(slug))
                continue;
            if (string.IsNullOrWhiteSpace(title))
                title = slug;

            entries.Add(new CommunityResourceEntry(
                string.IsNullOrWhiteSpace(projectId) ? slug : projectId,
                slug,
                title,
                ReadString(hit, "description"),
                NormalizeProjectType(ReadString(hit, "project_type"), category),
                NullIfWhiteSpace(ReadString(hit, "icon_url")),
                ReadInt64(hit, "downloads"),
                ReadDateTimeOffset(hit, "date_modified")));
        }

        return entries;
    }

    public async Task<CommunityResourceDownloadFile?> ResolveDownloadAsync(
        CommunityResourceEntry entry,
        CommunitySearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        options ??= new CommunitySearchOptions();
        string id = string.IsNullOrWhiteSpace(entry.ProjectId) ? entry.Slug : entry.ProjectId;
        if (string.IsNullOrWhiteSpace(id))
            return null;

        List<string> query = ["limit=20"];
        if (!string.IsNullOrWhiteSpace(options.GameVersion))
            query.Add("game_versions=" + Uri.EscapeDataString("[\"" + options.GameVersion.Trim() + "\"]"));
        if (!string.IsNullOrWhiteSpace(options.Loader) &&
            !string.Equals(options.Loader, "any", StringComparison.OrdinalIgnoreCase))
        {
            query.Add("loaders=" + Uri.EscapeDataString("[\"" + options.Loader.Trim().ToLowerInvariant() + "\"]"));
        }

        string requestUrl = "https://api.modrinth.com/v2/project/" + Uri.EscapeDataString(id) +
                            "/version?" + string.Join('&', query);
        using HttpResponseMessage response = await GetWithFallbackAsync(requestUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        foreach (JsonElement version in document.RootElement.EnumerateArray())
        {
            if (version.ValueKind != JsonValueKind.Object)
                continue;
            if (!TryGetProperty(version, "files", out JsonElement files) || files.ValueKind != JsonValueKind.Array)
                continue;

            JsonElement? primary = null;
            foreach (JsonElement file in files.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object)
                    continue;
                if (TryGetProperty(file, "primary", out JsonElement primaryFlag) &&
                    primaryFlag.ValueKind == JsonValueKind.True)
                {
                    primary = file;
                    break;
                }

                primary ??= file;
            }

            if (primary is not { } chosen)
                continue;

            string url = ReadString(chosen, "url");
            string fileName = ReadString(chosen, "filename");
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(fileName))
                continue;

            long size = 0;
            if (TryGetProperty(chosen, "size", out JsonElement sizeElement))
                sizeElement.TryGetInt64(out size);

            return CreateDownloadFile(
                fileName,
                url,
                size,
                ReadString(version, "id"),
                ReadString(version, "name"));
        }

        return null;
    }

    public async Task<IReadOnlyList<CommunityResourceVersion>> GetVersionsAsync(
        CommunityResourceEntry entry,
        CommunitySearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        options ??= new CommunitySearchOptions();
        string id = string.IsNullOrWhiteSpace(entry.ProjectId) ? entry.Slug : entry.ProjectId;
        if (string.IsNullOrWhiteSpace(id))
            return [];

        // Paginate: Sodium alone has 200+ version files. limit=100 alone is not enough —
        // and Modrinth's /project/{id}/version list can silently omit older IDs even after
        // full offset pagination (Sodium: ~194 of 219; drops 1.16–1.19). Those still appear
        // in project.versions and via game_versions filter / /versions?ids= batch.
        const int pageSize = 100;
        const int maxPages = 20; // hard cap 2000 versions from the list endpoint
        List<CommunityResourceVersion> versions = [];
        HashSet<string> seenIds = new(StringComparer.OrdinalIgnoreCase);

        for (int page = 0; page < maxPages; page++)
        {
            List<string> query =
            [
                "limit=" + pageSize.ToString(CultureInfo.InvariantCulture),
                "offset=" + (page * pageSize).ToString(CultureInfo.InvariantCulture)
            ];
            if (!string.IsNullOrWhiteSpace(options.GameVersion))
                query.Add("game_versions=" + Uri.EscapeDataString("[\"" + options.GameVersion.Trim() + "\"]"));
            if (!string.IsNullOrWhiteSpace(options.Loader) &&
                !string.Equals(options.Loader, "any", StringComparison.OrdinalIgnoreCase))
            {
                query.Add("loaders=" + Uri.EscapeDataString("[\"" + options.Loader.Trim().ToLowerInvariant() + "\"]"));
            }

            string requestUrl = "https://api.modrinth.com/v2/project/" + Uri.EscapeDataString(id) +
                                "/version?" + string.Join('&', query);
            using HttpResponseMessage response = await GetWithFallbackAsync(requestUrl, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                break;

            int pageCount = 0;
            foreach (JsonElement version in document.RootElement.EnumerateArray())
            {
                pageCount++;
                if (TryParseModrinthVersion(version, seenIds, out CommunityResourceVersion? parsed) &&
                    parsed is not null)
                {
                    versions.Add(parsed);
                }
            }

            if (pageCount < pageSize)
                break;
        }

        // Filtered list-by-game-version already returns legacy files (e.g. Sodium 1.16.3).
        // Unfiltered lists still omit them — reconcile against project.versions.
        if (string.IsNullOrWhiteSpace(options.GameVersion))
        {
            await AppendOmittedProjectVersionsAsync(id, versions, seenIds, options, cancellationToken)
                .ConfigureAwait(false);
        }

        return versions;
    }

    /// <summary>
    /// Modrinth sometimes omits older version IDs from <c>/project/.../version</c> pagination
    /// while still listing them on the project and serving them via <c>/versions?ids=</c>.
    /// </summary>
    private async Task AppendOmittedProjectVersionsAsync(
        string projectId,
        List<CommunityResourceVersion> versions,
        HashSet<string> seenIds,
        CommunitySearchOptions options,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> projectVersionIds =
            await GetProjectVersionIdsAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (projectVersionIds.Count == 0)
            return;

        List<string> missing = [];
        foreach (string versionId in projectVersionIds)
        {
            if (string.IsNullOrWhiteSpace(versionId) || seenIds.Contains(versionId))
                continue;
            missing.Add(versionId);
        }

        if (missing.Count == 0)
            return;

        string? loaderFilter = null;
        if (!string.IsNullOrWhiteSpace(options.Loader) &&
            !string.Equals(options.Loader, "any", StringComparison.OrdinalIgnoreCase))
        {
            loaderFilter = options.Loader.Trim();
        }

        // Modrinth accepts a JSON array of version ids; keep batches modest for URL length.
        const int batchSize = 50;
        for (int offset = 0; offset < missing.Count; offset += batchSize)
        {
            int count = Math.Min(batchSize, missing.Count - offset);
            string idsJson = "[" + string.Join(
                ',',
                missing.Skip(offset).Take(count).Select(static v => "\"" + v.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"")) + "]";
            string requestUrl = "https://api.modrinth.com/v2/versions?ids=" + Uri.EscapeDataString(idsJson);
            using HttpResponseMessage response = await GetWithFallbackAsync(requestUrl, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                continue;

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement version in document.RootElement.EnumerateArray())
            {
                if (!TryParseModrinthVersion(version, seenIds, out CommunityResourceVersion? parsed) ||
                    parsed is null)
                {
                    continue;
                }

                if (loaderFilter is not null &&
                    !parsed.Loaders.Any(l => string.Equals(l, loaderFilter, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                versions.Add(parsed);
            }
        }
    }

    private async Task<IReadOnlyList<string>> GetProjectVersionIdsAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        string requestUrl = "https://api.modrinth.com/v2/project/" + Uri.EscapeDataString(projectId);
        using HttpResponseMessage response = await GetWithFallbackAsync(requestUrl, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return [];

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!TryGetProperty(document.RootElement, "versions", out JsonElement versionsEl) ||
            versionsEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<string> ids = [];
        foreach (JsonElement item in versionsEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string id = item.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(id))
                    ids.Add(id);
            }
        }

        return ids;
    }

    private static bool TryParseModrinthVersion(
        JsonElement version,
        HashSet<string> seenIds,
        out CommunityResourceVersion? parsed)
    {
        parsed = null;
        if (version.ValueKind != JsonValueKind.Object)
            return false;

        string versionId = ReadString(version, "id");
        if (string.IsNullOrWhiteSpace(versionId) || !seenIds.Add(versionId))
            return false;

        List<CommunityResourceDownloadFile> files = [];
        if (TryGetProperty(version, "files", out JsonElement filesEl) && filesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement file in filesEl.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object)
                    continue;
                string url = ReadString(file, "url");
                string fileName = ReadString(file, "filename");
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(fileName))
                    continue;
                long size = 0;
                if (TryGetProperty(file, "size", out JsonElement sizeEl))
                    sizeEl.TryGetInt64(out size);
                files.Add(CreateDownloadFile(
                    fileName,
                    url,
                    size,
                    versionId,
                    ReadString(version, "name")));
            }
        }

        if (files.Count == 0)
            return false;

        parsed = new CommunityResourceVersion(
            versionId,
            ReadString(version, "name"),
            ReadString(version, "version_number"),
            NullIfWhiteSpace(ReadString(version, "changelog")),
            ReadDateTimeOffset(version, "date_published"),
            ReadStringArray(version, "game_versions"),
            ReadStringArray(version, "loaders"),
            files)
        {
            Dependencies = ReadModrinthDependencies(version)
        };
        return true;
    }

    public async Task<CommunityResourceEntry?> GetProjectAsync(
        CommunityResourceSource source,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        if (source == CommunityResourceSource.CurseForge || string.IsNullOrWhiteSpace(projectId))
            return null;

        string requestUrl = "https://api.modrinth.com/v2/project/" + Uri.EscapeDataString(projectId.Trim());
        using HttpResponseMessage response = await GetWithFallbackAsync(requestUrl, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        JsonElement project = document.RootElement;
        string id = ReadString(project, "id");
        string slug = ReadString(project, "slug");
        string title = ReadString(project, "title");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            return null;

        return new CommunityResourceEntry(
            id,
            slug,
            title,
            ReadString(project, "description"),
            ReadString(project, "project_type"),
            NullIfWhiteSpace(ReadString(project, "icon_url")),
            ReadInt64(project, "downloads"),
            ReadDateTimeOffset(project, "updated"))
        {
            Source = CommunityResourceSource.Modrinth
        };
    }

    public async Task<CommunityResourceVersionLookupResult?> GetVersionAsync(
        CommunityResourceSource source,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        if (source == CommunityResourceSource.CurseForge || string.IsNullOrWhiteSpace(versionId))
            return null;

        string requestUrl = "https://api.modrinth.com/v2/version/" + Uri.EscapeDataString(versionId.Trim());
        using HttpResponseMessage response = await GetWithFallbackAsync(requestUrl, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        JsonElement versionJson = document.RootElement;
        string projectId = ReadString(versionJson, "project_id");
        if (string.IsNullOrWhiteSpace(projectId) ||
            !TryParseModrinthVersion(versionJson, new HashSet<string>(StringComparer.OrdinalIgnoreCase), out CommunityResourceVersion? version) ||
            version is null)
        {
            return null;
        }

        CommunityResourceEntry? entry = await GetProjectAsync(
                CommunityResourceSource.Modrinth,
                projectId,
                cancellationToken)
            .ConfigureAwait(false);
        entry ??= new CommunityResourceEntry(
            projectId,
            projectId,
            projectId,
            string.Empty,
            "mod",
            null,
            0,
            null)
        {
            Source = CommunityResourceSource.Modrinth
        };
        return new CommunityResourceVersionLookupResult(entry, version);
    }

    public async Task<CommunityResourceFileIdentity?> LookupFileBySha1Async(
        string sha1Hex,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sha1Hex) || sha1Hex.Length is not (40 or 64))
            return null;

        string algorithm = sha1Hex.Length == 64 ? "sha512" : "sha1";
        string url = "https://api.modrinth.com/v2/version_file/" + Uri.EscapeDataString(sha1Hex.ToLowerInvariant()) +
                     "?algorithm=" + algorithm;
        try
        {
            using HttpResponseMessage response = await GetWithFallbackAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            JsonElement root = document.RootElement;
            string projectId = ReadString(root, "project_id");
            string versionId = ReadString(root, "id");
            if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(versionId))
                return null;

            string versionNumber = ReadString(root, "version_number");
            DateTimeOffset? published = ReadDateTimeOffset(root, "date_published");

            // Project metadata for title / icon / type.
            string title = projectId;
            string slug = projectId;
            string projectType = "mod";
            string? iconUrl = null;
            try
            {
                string projectUrl = "https://api.modrinth.com/v2/project/" + Uri.EscapeDataString(projectId);
                using HttpResponseMessage projectResponse = await GetWithFallbackAsync(projectUrl, cancellationToken)
                    .ConfigureAwait(false);
                if (projectResponse.IsSuccessStatusCode)
                {
                    await using Stream projectStream =
                        await projectResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using JsonDocument projectDoc =
                        await JsonDocument.ParseAsync(projectStream, cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                    title = NullIfWhiteSpace(ReadString(projectDoc.RootElement, "title")) ?? title;
                    slug = NullIfWhiteSpace(ReadString(projectDoc.RootElement, "slug")) ?? slug;
                    projectType = NullIfWhiteSpace(ReadString(projectDoc.RootElement, "project_type")) ?? projectType;
                    iconUrl = NullIfWhiteSpace(ReadString(projectDoc.RootElement, "icon_url"));
                }
            }
            catch
            {
                // identity still useful without project decoration
            }

            string website = "https://modrinth.com/" + projectType + "/" +
                             (string.IsNullOrWhiteSpace(slug) ? projectId : slug);
            return new CommunityResourceFileIdentity(
                projectId,
                slug,
                title,
                projectType,
                versionId,
                versionNumber,
                published,
                iconUrl,
                website);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return null;
        }
    }

    public async Task<CommunityResourceVersion?> GetLatestVersionAsync(
        string projectId,
        CommunitySearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            return null;

        options ??= new CommunitySearchOptions();
        CommunityResourceEntry stub = new(
            projectId,
            projectId,
            projectId,
            string.Empty,
            "mod",
            null,
            0,
            null);
        IReadOnlyList<CommunityResourceVersion> versions =
            await GetVersionsAsync(stub, options, cancellationToken).ConfigureAwait(false);
        return versions
            .OrderByDescending(static v => v.PublishedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
    }

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }

    private async Task<HttpResponseMessage> GetWithFallbackAsync(
        string url,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (string candidate in McimMirrorPolicy.ApiCandidates(
                     url,
                     CommunityResourceSource.Modrinth,
                     McimMirrorPolicy.CurrentPreference))
        {
            try
            {
                HttpResponseMessage response = await _client.GetAsync(candidate, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return response;
                lastError = new HttpRequestException($"Modrinth API returned {(int)response.StatusCode}.");
                response.Dispose();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new TimeoutException("Modrinth API request timed out.");
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
            }
        }
        throw lastError ?? new HttpRequestException("Modrinth API request failed.");
    }

    private static CommunityResourceDownloadFile CreateDownloadFile(
        string fileName,
        string url,
        long size,
        string versionId,
        string versionName) =>
        new(fileName, url, size, versionId, versionName)
        {
            CandidateUrls = McimMirrorPolicy.DownloadCandidates(
                url,
                CommunityResourceSource.Modrinth,
                McimMirrorPolicy.CurrentPreference)
        };

    private static HttpClient CreateDefaultClient()
    {
        HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PCL-N", "1.0"));
        return client;
    }

    private static string CreateFacets(CommunityResourceCategory category, CommunitySearchOptions options)
    {
        List<string> groups =
        [
            category switch
            {
                CommunityResourceCategory.Mod => "[\"project_type:mod\"]",
                CommunityResourceCategory.Modpack => "[\"project_type:modpack\"]",
                CommunityResourceCategory.DataPack => "[\"project_type:datapack\"]",
                CommunityResourceCategory.ResourcePack => "[\"project_type:resourcepack\"]",
                CommunityResourceCategory.Shader => "[\"project_type:shader\"]",
                CommunityResourceCategory.World => "[\"project_type:world\"]",
                _ => "[\"project_type:mod\"]"
            }
        ];

        if (!string.IsNullOrWhiteSpace(options.GameVersion))
            groups.Add("[\"versions:" + EscapeFacetValue(options.GameVersion.Trim()) + "\"]");

        if (!string.IsNullOrWhiteSpace(options.Loader) &&
            !string.Equals(options.Loader, "any", StringComparison.OrdinalIgnoreCase))
        {
            groups.Add("[\"categories:" + EscapeFacetValue(options.Loader.Trim().ToLowerInvariant()) + "\"]");
        }

        // Dual tags "{curseId}/{modrinthSlug}" — Modrinth only consumes the slug after '/'.
        string? modrinthTag = ExtractModrinthTag(options.Tag);
        if (!string.IsNullOrWhiteSpace(modrinthTag))
            groups.Add("[\"categories:" + EscapeFacetValue(modrinthTag) + "\"]");

        return "[" + string.Join(',', groups) + "]";
    }

    /// <summary>Parses dual tags like <c>412/technology</c> into the Modrinth slug half.</summary>
    internal static string? ExtractModrinthTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        string raw = tag.Trim();
        int slash = raw.IndexOf('/');
        string slug = slash >= 0 ? raw[(slash + 1)..] : raw;
        slug = slug.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(slug) ? null : slug;
    }

    private static string EscapeFacetValue(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string NormalizeProjectType(string projectType, CommunityResourceCategory category)
    {
        if (!string.IsNullOrWhiteSpace(projectType))
            return projectType.Trim();

        return category switch
        {
            CommunityResourceCategory.Modpack => "modpack",
            CommunityResourceCategory.ResourcePack => "resourcepack",
            CommunityResourceCategory.Shader => "shader",
            CommunityResourceCategory.DataPack => "datapack",
            CommunityResourceCategory.World => "world",
            _ => "mod"
        };
    }

    private static string ReadString(JsonElement element, string name) =>
        TryGetProperty(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long ReadInt64(JsonElement element, string name) =>
        TryGetProperty(element, name, out JsonElement value) && value.TryGetInt64(out long result) ? result : 0L;

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string name) =>
        DateTimeOffset.TryParse(ReadString(element, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset value)
            ? value
            : null;

    private static string? NullIfWhiteSpace(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static List<string> ReadStringArray(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            return [];

        List<string> items = [];
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string? s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    items.Add(s);
            }
        }

        return items;
    }

    private static List<CommunityResourceDependency> ReadModrinthDependencies(JsonElement version)
    {
        if (!TryGetProperty(version, "dependencies", out JsonElement dependencies) ||
            dependencies.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<CommunityResourceDependency> result = [];
        foreach (JsonElement dependency in dependencies.EnumerateArray())
        {
            if (dependency.ValueKind != JsonValueKind.Object)
                continue;
            string projectId = ReadString(dependency, "project_id");
            string versionId = ReadString(dependency, "version_id");
            string fileName = ReadString(dependency, "file_name");
            if (string.IsNullOrWhiteSpace(projectId) &&
                string.IsNullOrWhiteSpace(versionId) &&
                string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            CommunityResourceDependencyType type = ReadString(dependency, "dependency_type").ToLowerInvariant() switch
            {
                "required" => CommunityResourceDependencyType.Required,
                "optional" => CommunityResourceDependencyType.Optional,
                "incompatible" => CommunityResourceDependencyType.Incompatible,
                "embedded" => CommunityResourceDependencyType.Embedded,
                _ => CommunityResourceDependencyType.Unknown
            };
            result.Add(new CommunityResourceDependency(
                projectId,
                NullIfWhiteSpace(versionId),
                NullIfWhiteSpace(fileName),
                type,
                CommunityResourceSource.Modrinth));
        }

        return result;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
