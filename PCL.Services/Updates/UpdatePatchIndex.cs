using System.Text.Json.Serialization;

namespace PCL.Services.Updates;

/// <summary>
/// The published `patch-index.json` body: which variants exist for a release and which
/// HDiffPatch edges lead into it. Property names are the release-pipeline contract.
/// </summary>
public sealed class UpdatePatchIndexDto
{
    public int FormatVersion { get; set; }

    public string? TargetVersion { get; set; }

    public string? TargetTag { get; set; }

    public string? SourceRepo { get; set; }

    public UpdatePatchStrategyDto? Strategy { get; set; }

    public List<UpdatePatchVariantDto>? Variants { get; set; }
}

public sealed class UpdatePatchStrategyDto
{
    public int MaxDirectFromVersions { get; set; }

    public int HopInterval { get; set; }

    public string? UpgradeMode { get; set; }

    public List<string>? SelectedFromTags { get; set; }
}

public sealed class UpdatePatchVariantDto
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

    public List<UpdatePatchDto>? Patches { get; set; }
}

public sealed class UpdatePatchDto
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

/// <summary>One release's patch index together with the tag it was loaded from.</summary>
public sealed record UpdatePatchIndexSource(string ReleaseTag, UpdatePatchIndexDto Index);
