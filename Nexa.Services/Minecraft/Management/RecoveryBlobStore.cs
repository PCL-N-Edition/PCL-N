using System.IO.Compression;
using System.Security.Cryptography;

namespace Nexa.Services.Minecraft.Management;

internal sealed record RecoveryBlob(string Sha256, long Length);

/// <summary>One capture/restore transaction's actual uncompressed byte budget.</summary>
internal sealed class RecoveryByteBudget(long limit)
{
    private long _remaining = limit >= 0 ? limit : throw new ArgumentOutOfRangeException(nameof(limit));
    internal int ReadSize(int maximum) => (int)Math.Min(maximum - 1L, Math.Max(0, Interlocked.Read(ref _remaining))) + 1;
    internal void Consume(int count)
    {
        if (Interlocked.Add(ref _remaining, -count) < 0)
            throw new InvalidDataException("快照内容超过本次操作的大小限制。");
    }
}

/// <summary>
/// Immutable, locally compressed content objects. This is not a snapshot manifest: callers must
/// commit a complete manifest separately and restore only through staging files, never live files.
/// </summary>
internal sealed class RecoveryBlobStore
{
    internal const long MaxFileBytes = 512L * 1024 * 1024;
    internal const long MaxTransactionBytes = 8L * 1024 * 1024 * 1024;
    private readonly string _root;
    private readonly string _objects;

    internal RecoveryBlobStore(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("快照目录必须为绝对路径。", nameof(directory));
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        _objects = Path.Combine(_root, "objects");
    }

    internal async Task<RecoveryBlob> StoreAsync(Stream source, long expectedLength, RecoveryByteBudget budget, CancellationToken token = default)
    {
        ValidateLength(expectedLength);
        EnsureDirectory(_objects);
        string temporary = Path.Combine(_objects, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            string hash;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await using (var compressed = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
                    hash = await CopyAndHashAsync(source, compressed, expectedLength, budget, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            var blob = new RecoveryBlob(hash, expectedLength);
            // Multiple producers may stage concurrently. Publication and reuse verification are
            // protected across processes; the permanent lock file must never be deleted on release.
            await using var lease = await AcquireAsync(".objects.lock", token).ConfigureAwait(false);
            string destination = BlobPath(blob);
            CheckLinks(destination);
            if (File.Exists(destination))
            {
                try
                {
                    await CopyVerifiedAsync(blob, Stream.Null, new(expectedLength), token).ConfigureAwait(false);
                    return blob;
                }
                catch (InvalidDataException)
                {
                    // The new object has already been hashed from the caller's source. Replacing
                    // a corrupt object with these identical bytes repairs existing references.
                }
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            return blob;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal async Task CopyVerifiedAsync(RecoveryBlob blob, Stream stagingDestination, RecoveryByteBudget budget, CancellationToken token = default)
    {
        ValidateLength(blob.Length);
        string path = BlobPath(blob);
        CheckLinks(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > blob.Length + 65536)
            throw new InvalidDataException("快照对象缺失或大小异常。");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var compressed = new BrotliStream(input, CompressionMode.Decompress);
        string actual = await CopyAndHashAsync(compressed, stagingDestination, blob.Length, budget, token).ConfigureAwait(false);
        if (!string.Equals(actual, blob.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("快照对象校验失败，未允许提交恢复文件。");
    }

    private static async Task<string> CopyAndHashAsync(Stream source, Stream destination, long expectedLength, RecoveryByteBudget budget, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long total = 0;
        try
        {
            while (true)
            {
                // At the boundary read only one extra byte to distinguish EOF from a false size.
                int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(budget.ReadSize(buffer.Length), expectedLength - total + 1)), token).ConfigureAwait(false);
                if (read == 0) break;
                if (read > expectedLength - total) throw new InvalidDataException("快照文件的实际长度与声明不一致。");
                budget.Consume(read);
                hash.AppendData(buffer.AsSpan(0, read));
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                total += read;
            }
            if (total != expectedLength) throw new InvalidDataException("快照文件读取不完整。");
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private string BlobPath(RecoveryBlob blob)
    {
        if (blob.Sha256 is not { Length: 64 } || blob.Sha256.Any(character => character is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new InvalidDataException("快照对象标识无效。");
        return Path.Combine(_objects, blob.Sha256 + ".br");
    }

    internal async Task<FileStream> AcquireManifestLeaseAsync(CancellationToken token)
    {
        EnsureDirectory(_root);
        return await AcquireAsync(".manifest.lock", token).ConfigureAwait(false);
    }

    private async Task<FileStream> AcquireAsync(string name, CancellationToken token)
    {
        string path = Path.Combine(_root, name);
        long started = Environment.TickCount64;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            CheckLinks(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 - started < 5000)
            { await Task.Delay(25, token).ConfigureAwait(false); }
        }
    }

    private static void ValidateLength(long length)
    {
        if (length is < 0 or > MaxFileBytes) throw new InvalidDataException("快照中的单个文件超过大小限制。");
    }

    private static void EnsureDirectory(string path)
    {
        CheckLinks(path); Directory.CreateDirectory(path); CheckLinks(path);
    }

    internal static void CheckLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("快照路径不能包含链接。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
