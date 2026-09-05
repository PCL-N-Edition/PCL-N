using System.Security.Cryptography;
using System.Text;

namespace PCL.Services.Minecraft.Launch;

/// <summary>
/// Resolves the identity a launch runs with: the first account-roster profile when one exists,
/// otherwise the vanilla offline fallback. The offline identifier is the specification's v3
/// (MD5) UUID over "OfflinePlayer:" + name — an identifier derivation, not cryptography.
/// </summary>
public static class MinecraftOfflineIdentity
{
    public const string FallbackName = "Player";

    public static (string Name, string Uuid) Resolve(string? profileName, string? profileUuid)
    {
        if (!string.IsNullOrWhiteSpace(profileName) && !string.IsNullOrWhiteSpace(profileUuid))
        {
            return (profileName, profileUuid);
        }

        string name = string.IsNullOrWhiteSpace(profileName) ? FallbackName : profileName;
        return (name, UuidFromName(name));
    }

    // CA5351: the vanilla offline identifier is specified as an MD5-based v3 UUID; the hash
    // derives a player identifier and protects nothing.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "Minecraft offline UUID v3 derivation")]
    public static string UuidFromName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // CA5351: the vanilla offline identifier is specified as an MD5-based v3 UUID; the hash
        // derives a player identifier and protects nothing. Java's UUID.nameUUIDFromBytes uses
        // the RFC UUID byte order, and the N-format string is the raw hash bytes in that order
        // with the version/variant nibbles set — a Guid constructor here would re-byte-swap
        // the first fields and produce identifiers no vanilla server would recognize.
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes($"OfflinePlayer:{name}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Reproduces the byte-order bug the XSR alpha shipped: the same hash routed through the
    /// Guid constructor, which little-endian-swaps the first three fields. Migration code uses
    /// this to recognize and repair persisted offline profiles created by that build.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "Recognizing the legacy buggy derivation for migration")]
    public static string LegacyMismatchedUuid(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes($"OfflinePlayer:{name}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash).ToString("N");
    }
}
