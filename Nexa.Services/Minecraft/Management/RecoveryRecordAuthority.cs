using System.Security.Cryptography;
using System.Text;

namespace Nexa.Services.Minecraft.Management;

/// <summary>Local provenance for recovery records; never stored in an imported game directory.</summary>
internal static class RecoveryRecordAuthority
{
    private static string AuthorityRoot
    {
        get
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify);
            if (!Path.IsPathFullyQualified(profile)) throw new IOException("无法定位本机安装授权存储。");
            return Path.Combine(profile, "Nexa", "InstallRecoveryAuthority", "v1");
        }
    }

    internal static void VerifyAbsent(string recordPath)
    {
        string receipt = ReceiptPath(recordPath);
        RecoveryBlobStore.CheckLinks(receipt);
        if (File.Exists(receipt)) throw new InvalidDataException("本机已授权的恢复记录丢失，已停止自动恢复并保留文件。");
    }

    internal static void Verify(string recordPath, ReadOnlySpan<byte> bytes)
    {
        string path = ReceiptPath(recordPath);
        RecoveryBlobStore.CheckLinks(path);
        if (!File.Exists(path)) throw new InvalidDataException("此恢复记录没有本机授权，已保留文件。请重新发起安装。");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> expected = stackalloc byte[32];
        input.ReadExactly(expected);
        if (input.ReadByte() != -1 || !CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(bytes)))
            throw new InvalidDataException("恢复记录与本机授权不一致，已停止自动恢复并保留文件。");
    }

    internal static async Task AuthorizeAsync(string recordPath, ReadOnlyMemory<byte> bytes, CancellationToken token)
        => await WriteDigestAsync(recordPath, SHA256.HashData(bytes.Span), token).ConfigureAwait(false);

    internal static async Task AuthorizeFileAsync(string path, CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(path);
        await using var input = File.OpenRead(path);
        byte[] digest = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
        await WriteDigestAsync(path, digest, token).ConfigureAwait(false);
    }

    internal static async Task<bool> IsAuthorizedFileAsync(string path, CancellationToken token)
    {
        string receipt = ReceiptPath(path);
        RecoveryBlobStore.CheckLinks(receipt);
        if (!File.Exists(receipt)) return false;
        byte[] expected = new byte[32];
        await using (var authority = File.OpenRead(receipt))
        {
            await authority.ReadExactlyAsync(expected, token).ConfigureAwait(false);
            if (authority.ReadByte() != -1) throw new InvalidDataException("恢复授权无效。");
        }
        RecoveryBlobStore.CheckLinks(path);
        await using var input = File.OpenRead(path);
        byte[] actual = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static async Task WriteDigestAsync(string recordPath, byte[] digest, CancellationToken token)
    {
        string path = ReceiptPath(recordPath);
        RecoveryBlobStore.CheckLinks(path);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(AuthorityRoot);
        else Directory.CreateDirectory(AuthorityRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await output.WriteAsync(digest, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            RecoveryBlobStore.CheckLinks(path);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string ReceiptPath(string recordPath)
    {
        string identity = Path.GetFullPath(recordPath);
        // Bind root, task kind, GUID and record identity, not just attacker-copyable content.
        for (var directory = Directory.GetParent(identity); directory is not null; directory = directory.Parent)
        {
            if (directory.Name is not (".nexa-modify" or ".nexa-install-jobs" or ".nexa-pack-jobs" or ".nexa-rename")) continue;
            string root = Path.TrimEndingDirectorySeparator(directory.Parent!.FullName);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (AuthorityRoot.Equals(root, comparison) || AuthorityRoot.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                throw new InvalidDataException("游戏目录不能包含安装授权存储。");
        }
        if (OperatingSystem.IsWindows()) identity = identity.ToUpperInvariant();
        return Path.Combine(AuthorityRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
    }
}
