// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Application.Updates;

/// <summary>Identity stamped into the launcher build and used to select an update asset.</summary>
public sealed record LauncherBuildIdentity(
    string Version,
    string RuntimeId,
    string RuntimeVariant,
    string Configuration)
{
    public string NormalizedRuntimeVariant => NormalizeRuntimeVariant(RuntimeVariant);

    public static string NormalizeRuntimeVariant(string? value)
    {
        string text = value?.Trim() ?? string.Empty;
        // The host is always NativeAOT. This variant records whether the
        // out-of-process plugin sidecar carries its own CoreCLR runtime.
        return text.StartsWith("NoRuntime", StringComparison.OrdinalIgnoreCase)
            ? "NoRuntime"
            : "SelfContained";
    }

    public static string NormalizeConfiguration(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "release" or "stable" or "final" => "Release",
        "beta" or "preview" or "rc" => "Beta",
        _ => "CI"
    };
}

/// <summary>A single HDiffPatch edge. Multi-hop updates contain more than one step.</summary>
public sealed record LauncherUpdatePatchStep(
    string FromVersion,
    string TargetVersion,
    string DownloadUrl,
    string Sha256,
    long Size,
    string FromSha256,
    long FromSize,
    string TargetSha256,
    long TargetSize,
    string Algorithm = "hdiffpatch",
    string? FromManifestSha256 = null,
    string? TargetManifestSha256 = null)
{
    public bool IsScatterBundle => string.Equals(
        Algorithm,
        "hdiffpatch-scatter-v1",
        StringComparison.OrdinalIgnoreCase);
}

/// <summary>Download and verification plan for a launcher update.</summary>
public sealed record LauncherUpdatePackage(
    string TargetVersion,
    string TargetTag,
    string FullPackageUrl,
    string TargetAssetName,
    string TargetBinaryName,
    string? TargetSha256,
    long? TargetSize,
    IReadOnlyList<LauncherUpdatePatchStep> PatchSteps,
    string RuntimeId,
    string RuntimeVariant,
    string Configuration,
    string? FullPackageSignatureUrl = null,
    string? TargetBinarySignatureUrl = null,
    string? FullPackageSha256 = null,
    long? FullPackageSize = null)
{
    public bool UsesPatch => PatchSteps.Count > 0;
}

/// <summary>Self-contained per-file patch bundle manifest (<c>files.json</c>).</summary>
public sealed class LauncherScatterPatchManifest
{
    public int FormatVersion { get; set; }

    public string? Layout { get; set; }

    public string? FromVersion { get; set; }

    public string? ToVersion { get; set; }

    public string? FromManifestSha256 { get; set; }

    public string? ToManifestSha256 { get; set; }

    public List<LauncherScatterPatchOperation> Ops { get; set; } = [];

    public List<LauncherUpdateFileEntry> TargetFiles { get; set; } = [];
}

public sealed class LauncherScatterPatchOperation
{
    public string? Path { get; set; }

    public string? Op { get; set; }

    public string? Patch { get; set; }

    public string? Blob { get; set; }

    public string? PatchSha256 { get; set; }

    public long PatchSize { get; set; }

    public string? BlobSha256 { get; set; }

    public long BlobSize { get; set; }

    public string? FromSha256 { get; set; }

    public long FromSize { get; set; }

    public string? ToSha256 { get; set; }

    public long ToSize { get; set; }
}

public sealed class LauncherUpdateFileEntry
{
    public string? Path { get; set; }

    public string? Sha256 { get; set; }

    public long Size { get; set; }

    public int? UnixMode { get; set; }
}

/// <summary>
/// Verified local hand-off from the managed installer to the update helper.
/// Absolute paths are accepted only after the helper validates their roots.
/// </summary>
public sealed class LauncherInstallPlan
{
    public int FormatVersion { get; set; }

    public string? InstallRoot { get; set; }

    public string? EntryRelativePath { get; set; }

    public string? StagedRoot { get; set; }

    public List<LauncherUpdateFileEntry> Files { get; set; } = [];

    public List<string> DeletePaths { get; set; } = [];
}
