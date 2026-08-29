namespace PCL.Services.Updates;

/// <summary>
/// Signed content-addressed reconstruction map for block-based updates. Property names are
/// the wire/file contract and match the legacy blockmap exactly.
/// </summary>
public sealed class UpdateBlockMap
{
    public int FormatVersion { get; set; }

    public string? Layout { get; set; }

    public string? Algorithm { get; set; }

    /// <summary>Default full-block compression (<c>gzip</c> or <c>zstd</c>). Per-block <see cref="UpdateBlockFull.Compression"/> wins.</summary>
    public string? Compression { get; set; }

    public string? BlockBasePath { get; set; }

    /// <summary>Optional self-describing CDC bounds (blockmap format v2+).</summary>
    public UpdateChunkingParameters? Chunking { get; set; }

    public string? TargetTag { get; set; }

    public string? TargetVersion { get; set; }

    public string? RuntimeId { get; set; }

    public string? RuntimeVariant { get; set; }

    public string? Configuration { get; set; }

    public string? TargetAssetName { get; set; }

    public string? TargetManifestSha256 { get; set; }

    public List<UpdateBlockFile> TargetFiles { get; set; } = [];
}

/// <summary>CDC size bounds embedded in blockmap format v2 (<c>chunking</c>).</summary>
public sealed class UpdateChunkingParameters
{
    public int Min { get; set; }

    public int Avg { get; set; }

    public int Max { get; set; }
}

public sealed class UpdateBlockFile : UpdateFileEntry
{
    public List<UpdateBlock> Chunks { get; set; } = [];
}

public sealed class UpdateBlock
{
    public string? Sha256 { get; set; }

    public long Size { get; set; }

    /// <summary>Flat full-block path (v1 / full-only v2). Prefer <see cref="Full"/> when present.</summary>
    public long CompressedSize { get; set; }

    public string? Path { get; set; }

    /// <summary>Optional nested full representation (protocol v2 with deltas).</summary>
    public UpdateBlockFull? Full { get; set; }

    /// <summary>Optional VCDIFF representations (protocol v2). Always fall back to full.</summary>
    public List<UpdateBlockDelta>? Deltas { get; set; }

    public string? ResolveFullPath() =>
        !string.IsNullOrWhiteSpace(Full?.Path) ? Full.Path : Path;

    public long ResolveCompressedSize() =>
        Full is { CompressedSize: > 0 } ? Full.CompressedSize : CompressedSize;

    public string? ResolveCompression(string? mapDefault) =>
        !string.IsNullOrWhiteSpace(Full?.Compression)
            ? Full.Compression
            : mapDefault;
}

public sealed class UpdateBlockFull
{
    public string? Path { get; set; }

    public long CompressedSize { get; set; }

    /// <summary><c>gzip</c> (legacy default) or <c>zstd</c> (protocol v2 preferred for new blocks).</summary>
    public string? Compression { get; set; }
}

public sealed class UpdateBlockDelta
{
    public string? Algorithm { get; set; }

    public List<string> SourceChunks { get; set; } = [];

    public string? SourceSha256 { get; set; }

    public long SourceSize { get; set; }

    public string? Path { get; set; }

    public long Size { get; set; }
}

/// <summary>One file entry shared by blockmaps, patch manifests, and install plans.</summary>
public class UpdateFileEntry
{
    public string? Path { get; set; }

    public string? Sha256 { get; set; }

    public long Size { get; set; }

    public int? UnixMode { get; set; }
}
