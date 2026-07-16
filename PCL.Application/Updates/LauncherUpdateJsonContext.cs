// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json.Serialization;

namespace PCL.Application.Updates;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GitHubReleaseDto))]
[JsonSerializable(typeof(List<GitHubReleaseDto>))]
[JsonSerializable(typeof(GitHubAssetDto))]
internal sealed partial class LauncherUpdateJsonContext : JsonSerializerContext;

internal sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("target_commitish")]
    public string? TargetCommitish { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAssetDto>? Assets { get; set; }
}

internal sealed class GitHubAssetDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }
}
