// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Application.Updates;

/// <summary>
/// Independent PCL.Plugin Sidecar update check result (sibling of launcher updates).
/// </summary>
public sealed record PluginSidecarUpdateCheckResult(
    bool Success,
    bool IsUpdateAvailable,
    string? CurrentVersion,
    string? LatestVersion,
    string? ReleaseName,
    string? ReleaseNotes,
    string? ReleaseUrl,
    string? PackageUrl,
    string? PackageSha256,
    long? PackageSize,
    string? BlockMapUrl,
    string? ErrorMessage,
    string? RemoteCommitSha,
    DateTimeOffset? PublishedAt,
    string Channel = "plugin")
{
    public static PluginSidecarUpdateCheckResult Failed(string message) =>
        new(
            Success: false,
            IsUpdateAvailable: false,
            CurrentVersion: null,
            LatestVersion: null,
            ReleaseName: null,
            ReleaseNotes: null,
            ReleaseUrl: null,
            PackageUrl: null,
            PackageSha256: null,
            PackageSize: null,
            BlockMapUrl: null,
            ErrorMessage: message,
            RemoteCommitSha: null,
            PublishedAt: null);
}

public sealed record PluginSidecarInstallIdentity(
    string RuntimeId,
    string RuntimeVariant,
    string CurrentVersion,
    string? CurrentCommitSha = null);
