// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Application.Updates;

public enum LauncherDistributionLayout
{
    Scatter,
    SingleFile
}

/// <summary>Identity stamped into the launcher build and used to select an update asset.</summary>
public sealed record LauncherBuildIdentity(
    string Version,
    string RuntimeId,
    string RuntimeVariant,
    string Configuration)
{
    public LauncherDistributionLayout DistributionLayout { get; init; } = LauncherDistributionLayout.Scatter;

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
    long? FullPackageSize = null,
    string? BlockMapUrl = null,
    string? BlockMapSignatureUrl = null,
    string? BlockMapFallbackUrl = null,
    string? BlockMapFallbackSignatureUrl = null)
{
    public bool UsesPatch => PatchSteps.Count > 0;

    public bool SupportsBlockMap => !string.IsNullOrWhiteSpace(BlockMapUrl) &&
                                    !string.IsNullOrWhiteSpace(BlockMapSignatureUrl);
}

/// <summary>Signed content-addressed reconstruction map for Cloudflare updater.</summary>
public sealed class LauncherUpdateBlockMap
{
    public int FormatVersion { get; set; }

    public string? Layout { get; set; }

    public string? Algorithm { get; set; }

    /// <summary>Default full-block compression (<c>gzip</c> or <c>zstd</c>). Per-block <see cref="LauncherUpdateBlockFull.Compression"/> wins.</summary>
    public string? Compression { get; set; }

    public string? BlockBasePath { get; set; }

    /// <summary>Optional self-describing CDC bounds (blockmap format v2+).</summary>
    public LauncherUpdateChunkingParameters? Chunking { get; set; }

    public string? TargetTag { get; set; }

    public string? TargetVersion { get; set; }

    public string? RuntimeId { get; set; }

    public string? RuntimeVariant { get; set; }

    public string? Configuration { get; set; }

    public string? TargetAssetName { get; set; }

    public string? TargetManifestSha256 { get; set; }

    public List<LauncherUpdateBlockFile> TargetFiles { get; set; } = [];
}

/// <summary>CDC size bounds embedded in blockmap v2 (<c>chunking</c>).</summary>
public sealed class LauncherUpdateChunkingParameters
{
    public int Min { get; set; }

    public int Avg { get; set; }

    public int Max { get; set; }
}

public sealed class LauncherUpdateBlockFile : LauncherUpdateFileEntry
{
    public List<LauncherUpdateBlock> Chunks { get; set; } = [];
}

public sealed class LauncherUpdateBlock
{
    public string? Sha256 { get; set; }

    public long Size { get; set; }

    /// <summary>Flat full-block path (v1 / full-only v2). Prefer <see cref="Full"/> when present.</summary>
    public long CompressedSize { get; set; }

    public string? Path { get; set; }

    /// <summary>Optional nested full representation (protocol v2 with deltas).</summary>
    public LauncherUpdateBlockFull? Full { get; set; }

    /// <summary>Optional VCDIFF representations (protocol v2). Always fall back to full.</summary>
    public List<LauncherUpdateBlockDelta>? Deltas { get; set; }

    public string? ResolveFullPath() =>
        !string.IsNullOrWhiteSpace(Full?.Path) ? Full.Path : Path;

    public long ResolveCompressedSize() =>
        Full is { CompressedSize: > 0 } ? Full.CompressedSize : CompressedSize;

    public string? ResolveCompression(string? mapDefault) =>
        !string.IsNullOrWhiteSpace(Full?.Compression)
            ? Full.Compression
            : mapDefault;
}

public sealed class LauncherUpdateBlockFull
{
    public string? Path { get; set; }

    public long CompressedSize { get; set; }

    /// <summary><c>gzip</c> (legacy default) or <c>zstd</c> (protocol v2 preferred for new blocks).</summary>
    public string? Compression { get; set; }
}

public sealed class LauncherUpdateBlockDelta
{
    public string? Algorithm { get; set; }

    public List<string> SourceChunks { get; set; } = [];

    public string? SourceSha256 { get; set; }

    public long SourceSize { get; set; }

    public string? Path { get; set; }

    public long Size { get; set; }
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

public class LauncherUpdateFileEntry
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
