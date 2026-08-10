// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json.Serialization;

namespace PCL.Application.Updates;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GitHubReleaseDto))]
[JsonSerializable(typeof(List<GitHubReleaseDto>))]
[JsonSerializable(typeof(GitHubAssetDto))]
[JsonSerializable(typeof(LauncherBuildMetadataDto))]
[JsonSerializable(typeof(LauncherChannelReleaseDto))]
[JsonSerializable(typeof(LauncherPatchIndexDto))]
[JsonSerializable(typeof(LauncherUpdateBlockMap))]
[JsonSerializable(typeof(LauncherUpdateChunkingParameters))]
[JsonSerializable(typeof(LauncherScatterPatchManifest))]
[JsonSerializable(typeof(LauncherInstallPlan))]
internal sealed partial class LauncherUpdateJsonContext : JsonSerializerContext;

internal sealed class LauncherBuildMetadataDto
{
    public int FormatVersion { get; set; }

    public string? Channel { get; set; }

    public string? Commit { get; set; }

    public string? Ref { get; set; }

    public string? Tag { get; set; }

    public string? RunId { get; set; }

    public string? Artifact { get; set; }

    public string? PackageSha256 { get; set; }

    public long? PackageSize { get; set; }

    public bool SupportsPatches { get; set; }

    public DateTimeOffset? BuiltAt { get; set; }
}

internal sealed class LauncherChannelReleaseDto
{
    public string? Tag { get; set; }

    public string? Version { get; set; }

    public string? Channel { get; set; }

    public string? CommitSha { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public string? ManifestKey { get; set; }
}

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

internal sealed class LauncherPatchIndexDto
{
    public int FormatVersion { get; set; }

    public string? TargetVersion { get; set; }

    public string? TargetTag { get; set; }

    public string? SourceRepo { get; set; }

    public LauncherPatchStrategyDto? Strategy { get; set; }

    public List<LauncherPatchVariantDto>? Variants { get; set; }
}

internal sealed class LauncherPatchStrategyDto
{
    public int MaxDirectFromVersions { get; set; }

    public int HopInterval { get; set; }

    public string? UpgradeMode { get; set; }

    public List<string>? SelectedFromTags { get; set; }
}

internal sealed class LauncherPatchVariantDto
{
    public string? RuntimeId { get; set; }

    public string? RuntimeVariant { get; set; }

    public string? Configuration { get; set; }

    public string? TargetAssetName { get; set; }

    public string? TargetBinaryName { get; set; }

    public string? TargetSha256 { get; set; }

    public long TargetSize { get; set; }

    public long TargetArchiveSize { get; set; }

    public string? TargetManifestSha256 { get; set; }

    public int TargetFileCount { get; set; }

    public List<LauncherPatchDto>? Patches { get; set; }
}

internal sealed class LauncherPatchDto
{
    public string? FromVersion { get; set; }

    public string? FromTag { get; set; }

    public string? Algorithm { get; set; }

    public string? FileName { get; set; }

    public string? DownloadUrl { get; set; }

    public string? Sha256 { get; set; }

    public long Size { get; set; }

    public string? FromSha256 { get; set; }

    public long FromSize { get; set; }

    public string? Layout { get; set; }

    public string? FromManifestSha256 { get; set; }

    public string? TargetManifestSha256 { get; set; }

    public int OperationCount { get; set; }
}
