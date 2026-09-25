using System.Security.Cryptography;
using System.Text;
using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Minecraft.Install;

/// <summary>Pins the exact verified installer before any processor executes.</summary>
internal sealed class LoaderInstallerCache : IDisposable
{
    private readonly string _directory;
    private readonly FileStream _lease;
    private string Artifact => Path.Combine(_directory, "installer.jar");
    private string Receipt => Path.Combine(_directory, "sha256");

    internal LoaderInstallerCache(string root, MinecraftLoaderInstallRequest request)
    {
        string identity = request.Loader + "\n" + request.Game + "\n" + request.Build + "\n" + request.LocalInstaller?.Sha256;
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        _directory = ForgeInstallService.Contained(root, ".task/loader/" + key);
        RecoveryBlobStore.CheckLinks(_directory); Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "lock"); RecoveryBlobStore.CheckLinks(path);
        _lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal async Task<bool> RestoreAsync(string destination, CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(Receipt); RecoveryBlobStore.CheckLinks(Artifact);
        if (!File.Exists(Receipt)) { RecoveryRecordAuthority.VerifyAbsent(Receipt); return false; }
        if (new FileInfo(Receipt).Length != 64) throw new InvalidDataException("安装器身份记录无效。");
        byte[] receipt = new byte[64];
        await using (var receiptStream = File.OpenRead(Receipt))
        {
            await receiptStream.ReadExactlyAsync(receipt, token).ConfigureAwait(false);
            if (receiptStream.ReadByte() != -1) throw new InvalidDataException("安装器身份记录已改变。");
        }
        RecoveryRecordAuthority.Verify(Receipt, receipt);
        string expected = Encoding.ASCII.GetString(receipt);
        if (!expected.All(char.IsAsciiHexDigit) || !File.Exists(Artifact)) throw new InvalidDataException("安装器缓存不完整。");
        await using var source = File.OpenRead(Artifact);
        if (source.Length > 512L * 1024 * 1024) throw new InvalidDataException("安装器缓存过大。");
        string actual = Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false));
        if (actual != expected) throw new InvalidDataException("安装器缓存已改变，已停止恢复。");
        source.Position = 0;
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(output, token).ConfigureAwait(false);
        return true;
    }

    internal async Task SaveAsync(string sourcePath, CancellationToken token)
    {
        string temporary = Path.Combine(_directory, "installer.part"), receiptPart = Path.Combine(_directory, "receipt.part");
        RecoveryBlobStore.CheckLinks(temporary); RecoveryBlobStore.CheckLinks(receiptPart);
        RecoveryBlobStore.CheckLinks(Artifact); RecoveryBlobStore.CheckLinks(Receipt);
        if (File.Exists(Receipt)) throw new InvalidDataException("不能覆盖已有的安装器身份。");
        RecoveryRecordAuthority.VerifyAbsent(Receipt);
        await using (var source = File.OpenRead(sourcePath))
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (source.Length > 512L * 1024 * 1024) throw new InvalidDataException("安装器过大。");
            await source.CopyToAsync(output, token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
        }
        string digest;
        await using (var source = File.OpenRead(temporary)) digest = Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false));
        await using (var output = new FileStream(receiptPart, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await output.WriteAsync(Encoding.ASCII.GetBytes(digest), token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
        }
        token.ThrowIfCancellationRequested();
        File.Move(temporary, Artifact, true);
        await RecoveryRecordAuthority.AuthorizeAsync(Receipt, Encoding.ASCII.GetBytes(digest), token).ConfigureAwait(false);
        File.Move(receiptPart, Receipt);
    }

    public void Dispose() => _lease.Dispose();
}
