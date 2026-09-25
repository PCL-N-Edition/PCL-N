using System.Security.Cryptography;

namespace Nexa.Services.Minecraft.Process;

internal static class GameOptionsFingerprint
{
    public static string? Read(string directory)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(directory, "options.txt"), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] bytes = new byte[65537];
            int count = 0, read;
            while (count < bytes.Length && (read = stream.Read(bytes.AsSpan(count))) > 0) count += read;
            return count > 65536 ? null : "options-1:" + Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, count)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
}
