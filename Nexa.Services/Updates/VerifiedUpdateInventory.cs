using System.Text.Json;

namespace Nexa.Services.Updates;

/// <summary>Deletion authority from a signed previous release, separate from the mutable install plan.</summary>
public sealed class VerifiedUpdateInventory
{
    internal sealed record Entry(string Path, long Size, string Sha256);
    internal IReadOnlyDictionary<string, Entry> Entries { get; }

    private VerifiedUpdateInventory(Dictionary<string, Entry> entries) => Entries = entries;

    public static async Task<VerifiedUpdateInventory> VerifyAsync(ReadOnlyMemory<byte> blockMap,
        ReadOnlyMemory<byte> signature, UpdateBuildIdentity installedIdentity, IUpdateSignatureVerifier verifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installedIdentity);
        ArgumentNullException.ThrowIfNull(verifier);
        if (blockMap.Length is 0 or > 16 * 1024 * 1024 || signature.Length is 0 or > 1024 * 1024)
            throw new InvalidDataException("已安装版本的签名清单大小不合法。");
        // Own the bytes across verification and parsing; callers cannot mutate a verified buffer.
        using var content = new MemoryStream(blockMap.ToArray(), writable: false);
        using var detached = new MemoryStream(signature.ToArray(), writable: false);
        await verifier.VerifyAsync(content, detached, cancellationToken).ConfigureAwait(false);
        content.Position = 0;
        var map = await JsonSerializer.DeserializeAsync(content, UpdateJsonContext.Default.UpdateBlockMap, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("已安装版本清单为空。");
        if (!UpdateVersion.TryParse(map.TargetVersion, out var version)
            || !UpdateVersion.TryParse(installedIdentity.Version, out var expected) || version != expected
            || !string.Equals(map.RuntimeId, installedIdentity.RuntimeId, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(map.RuntimeVariant)
            || UpdateBuildIdentity.NormalizeRuntimeVariant(map.RuntimeVariant) != installedIdentity.NormalizedRuntimeVariant
            || !string.Equals(map.Configuration, installedIdentity.Configuration, StringComparison.OrdinalIgnoreCase)
            || map.FormatVersion is < 1 or > 2 || map.TargetFiles is null || map.TargetFiles.Count > 65536)
            throw new InvalidDataException("已安装版本清单与当前运行版本不匹配。");
        var entries = new Dictionary<string, Entry>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var file in map.TargetFiles)
        {
            string path = UpdateStaging.NormalizeRelativePath(file.Path);
            if (path.Length == 0 || path.Split('/').Any(part => part is "" or "." or "..")
                || Path.IsPathRooted(file.Path ?? "") || path.Contains(':')
                || file.Size < 0 || file.Sha256 is not { Length: 64 } || !file.Sha256.All(char.IsAsciiHexDigit)
                || !entries.TryAdd(path, new Entry(path, file.Size, file.Sha256)))
                throw new InvalidDataException("已安装版本清单包含不合法的文件信息。");
        }
        return new VerifiedUpdateInventory(entries);
    }
}
