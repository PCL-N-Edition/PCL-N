using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

[assembly: SuppressMessage("Cryptographic Do Not Use", "CA5350:DoNotUseWeakCryptographicAlgorithms",
    Scope = "member",
    Target = "~M:Nexa.Services.Minecraft.Downloads.MinecraftFileVerifier.VerifyAsync(Nexa.Services.Minecraft.Downloads.MinecraftExpectedFile,System.Threading.CancellationToken)",
    Justification = "SHA-1 is the integrity fact Mojang and loader metadata ship; this detects corruption, it is not a security decision.")]

namespace Nexa.Services.Minecraft.Downloads;

/// <summary>What a file on disk must satisfy to count as present. Null facts are unchecked.</summary>
public sealed record MinecraftExpectedFile(string Path, long? Size, string? Sha1);

/// <summary>
/// One integrity rule shared by every consumer — install, launch file completion, and future
/// repair/verify passes: a known SHA-1 must match; otherwise a known size must match;
/// otherwise the file only needs to exist with content. Existence-with-content alone can
/// never certify a truncated or corrupted artifact, which is exactly how half-installs used
/// to pass as successful.
/// </summary>
public static class MinecraftFileVerifier
{
    public static async ValueTask<bool> VerifyAsync(
        MinecraftExpectedFile file,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file.Path);
        FileInfo info = new(file.Path);
        if (!info.Exists || info.Length == 0)
        {
            return false;
        }

        if (file.Size is { } expectedSize && info.Length != expectedSize)
        {
            return false;
        }

        if (file.Sha1 is { Length: > 0 } expectedSha1)
        {
            await using FileStream stream = File.OpenRead(file.Path);
            byte[] hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    Convert.ToHexString(hash),
                    expectedSha1,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
