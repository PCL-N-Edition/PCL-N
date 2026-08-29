using System.Text.Json.Serialization;

namespace PCL.Services.Updates;

/// <summary>
/// Self-contained per-file patch bundle manifest (<c>files.json</c> inside a scatter
/// bundle). Property names are the release pipeline's contract.
/// </summary>
public sealed class UpdateScatterPatchManifest
{
    public int FormatVersion { get; set; }

    public string? Layout { get; set; }

    public string? FromVersion { get; set; }

    public string? ToVersion { get; set; }

    public string? FromManifestSha256 { get; set; }

    public string? ToManifestSha256 { get; set; }

    public List<UpdateScatterPatchOperation> Ops { get; set; } = [];

    public List<UpdateFileEntry> TargetFiles { get; set; } = [];
}

/// <summary>
/// One scatter patch operation: <c>hdiff</c> applies an HDiffPatch payload over the current
/// file, <c>add</c>/<c>replace</c> stage a verified blob, and <c>delete</c> marks a managed
/// file as gone.
/// </summary>
public sealed class UpdateScatterPatchOperation
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
