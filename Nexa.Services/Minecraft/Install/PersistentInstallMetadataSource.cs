using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Minecraft.Install;

/// <summary>Task-local, immutable metadata. Corruption stops recovery instead of silently fetching new facts.</summary>
internal sealed class PersistentInstallMetadataSource(string stage, IMinecraftInstallMetadataSource source) : IMinecraftInstallMetadataSource
{
    private const int MaxRecordBytes = 16 * 1024 * 1024;
    private const long MaxCacheBytes = 64L * 1024 * 1024;
    public Task<JsonObject> FetchVanillaVersionJsonAsync(string gameVersion, CancellationToken cancellationToken) =>
        GetAsync("vanilla:" + gameVersion, token => source.FetchVanillaVersionJsonAsync(gameVersion, token), cancellationToken);
    public Task<JsonObject> FetchLoaderProfileJsonAsync(InstallLoader loader, string gameVersion, string build, CancellationToken cancellationToken) =>
        GetAsync("loader:" + JsonSerializer.Serialize(new[] { loader.ToString(), gameVersion, build }, MetadataJsonContext.Default.StringArray),
            token => source.FetchLoaderProfileJsonAsync(loader, gameVersion, build, token), cancellationToken);
    public Task<JsonObject> FetchAssetIndexJsonAsync(string indexUrl, CancellationToken cancellationToken) =>
        GetAsync("assets:" + indexUrl, token => source.FetchAssetIndexJsonAsync(indexUrl, token), cancellationToken);

    internal async Task<IReadOnlyList<InstallDownload>> FetchAddonDownloadsAsync(string game, MinecraftInstallAddon addon,
        Func<CancellationToken, Task<IReadOnlyList<InstallDownload>>> fetch, CancellationToken token)
    {
        string request = "addon:" + JsonSerializer.Serialize(new[] { addon.Kind.ToString(), game, addon.Version }, MetadataJsonContext.Default.StringArray);
        var document = await GetAsync(request, async cancellation =>
        {
            var fetched = await fetch(cancellation).ConfigureAwait(false);
            if (fetched.Count is < 1 or > 16) throw new InvalidDataException("安装附属组件下载数量无效。");
            var downloads = fetched.ToArray();
            ValidateDownloads(downloads);
            return new JsonObject { ["downloads"] = JsonSerializer.SerializeToNode(downloads, MetadataJsonContext.Default.InstallDownloadArray) };
        }, token).ConfigureAwait(false);
        var result = document["downloads"]?.Deserialize(MetadataJsonContext.Default.InstallDownloadArray)
            ?? throw new InvalidDataException("安装附属组件记录缺少下载信息。");
        ValidateDownloads(result);
        return Array.AsReadOnly(result);
    }

    private static void ValidateDownloads(InstallDownload[] downloads)
    {
        if (downloads.Length is < 1 or > 16) throw new InvalidDataException("安装附属组件下载数量无效。");
        foreach (var item in downloads)
            if (item is null || string.IsNullOrWhiteSpace(item.Source) || item.Source.Length > 64 || item.Source.Any(char.IsControl)
                || !MinecraftVersionPaths.IsSafeReference(item.FileName)
                || item.Url is not { IsAbsoluteUri: true } url || url.Scheme != Uri.UriSchemeHttps || url.UserInfo.Length != 0 || url.AbsoluteUri.Length > 8192
                || item.Size is < 0 or > RecoveryBlobStore.MaxFileBytes
                || item.Sha1 is { } hash && (hash.Length != 40 || !hash.All(char.IsAsciiHexDigit)))
                throw new InvalidDataException("安装附属组件下载身份无效。");
    }

    private async Task<JsonObject> GetAsync(string request, Func<CancellationToken, Task<JsonObject>> fetch, CancellationToken token)
    {
        if (request.Length > 16384) throw new InvalidDataException("安装元数据请求过长。");
        string root = Directory.GetParent(stage)?.Parent?.FullName ?? throw new InvalidDataException("安装任务目录无效。");
        _ = await InstallTaskJournal.ReadAsync(root, stage, token).ConfigureAwait(false);
        string directory = Path.Combine(stage, InstallTaskJournal.DirectoryName, "metadata");
        RecoveryBlobStore.CheckLinks(directory); Directory.CreateDirectory(directory);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request)));
        string path = Path.Combine(directory, key + ".json");
        // Serialize first publication across independent service objects and launcher processes.
        await using var lease = await LockAsync(Path.Combine(directory, ".lock"), token).ConfigureAwait(false);
        RecoveryBlobStore.CheckLinks(path);
        if (File.Exists(path)) return await ReadAsync(path, request, token).ConfigureAwait(false);
        JsonObject payload = (JsonObject)(await fetch(token).ConfigureAwait(false)).DeepClone();
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(payload, RecoveryJsonContext.Default.JsonObject);
        string hash = Convert.ToHexString(SHA256.HashData(content));
        var envelope = new JsonObject { ["version"] = 1, ["request"] = request, ["sha256"] = hash, ["payload"] = payload };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, RecoveryJsonContext.Default.JsonObject);
        if (bytes.Length > MaxRecordBytes) throw new InvalidDataException("安装元数据超过单项大小限制。");
        int count = 0; long total = bytes.Length;
        foreach (string existing in Directory.EnumerateFiles(directory, "*.json"))
        {
            RecoveryBlobStore.CheckLinks(existing); total += new FileInfo(existing).Length;
            if (++count >= 32 || total > MaxCacheBytes) throw new InvalidDataException("安装元数据超过任务大小限制。");
        }
        string temporary = Path.Combine(directory, key + "." + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            { await output.WriteAsync(bytes, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true); }
            token.ThrowIfCancellationRequested(); RecoveryBlobStore.CheckLinks(path); File.Move(temporary, path, false);
        }
        finally { RecoveryBlobStore.CheckLinks(temporary); if (File.Exists(temporary)) File.Delete(temporary); }
        return (JsonObject)payload.DeepClone();
    }

    private static async Task<JsonObject> ReadAsync(string path, string request, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (input.Length > MaxRecordBytes) throw new InvalidDataException("安装元数据超过单项大小限制。");
        byte[] bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (input.ReadByte() != -1) throw new InvalidDataException("安装元数据在读取时发生变化。");
        var envelope = JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException("安装元数据无效。");
        if (envelope["version"]?.GetValue<int>() != 1 || envelope["request"]?.GetValue<string>() != request || envelope["payload"] is not JsonObject payload)
            throw new InvalidDataException("安装元数据与任务请求不匹配。");
        string actual = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, RecoveryJsonContext.Default.JsonObject)));
        if (actual != envelope["sha256"]?.GetValue<string>()) throw new InvalidDataException("安装元数据校验失败，已保留任务。");
        return (JsonObject)payload.DeepClone();
    }

    private static async Task<FileStream> LockAsync(string path, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested(); RecoveryBlobStore.CheckLinks(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 199) { await Task.Delay(50, token).ConfigureAwait(false); }
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
[System.Text.Json.Serialization.JsonSerializable(typeof(InstallDownload[]))]
internal sealed partial class MetadataJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
