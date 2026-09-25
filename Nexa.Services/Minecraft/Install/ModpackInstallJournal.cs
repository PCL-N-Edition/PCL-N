using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Minecraft.Install;

internal sealed record ModpackIntent(int Schema, MinecraftModpackCommand Command);
internal sealed record ModpackPublishedFile(string Path, long Length, string Sha256);
internal sealed class ModpackInstallJournal
{
    internal const string DirectoryName = ".nexa-pack-jobs";
    internal string Stage { get; }
    internal MinecraftModpackCommand Command { get; }
    internal string Archive => Path.Combine(Stage, "source.pack");
    internal string Game => Path.Combine(Stage, "game");
    internal string Instance => ForgeInstallService.Contained(Game, "versions/" + Command.Pack.InstanceId);
    internal string Destination => ForgeInstallService.Contained(Command.RootDirectory, "versions/" + Command.Pack.InstanceId);
    internal bool Complete => Marker("complete");
    internal bool Canceled => Marker("canceled");
    internal bool Prepared
    {
        get
        {
            string path = Path.Combine(Stage, "publication.json");
            if (File.Exists(path)) return true;
            RecoveryRecordAuthority.VerifyAbsent(path);
            return false;
        }
    }
    private ModpackInstallJournal(string stage, MinecraftModpackCommand command) { Stage = stage; Command = command; }
    internal static async Task<ModpackInstallJournal> CreateAsync(string root, MinecraftModpackCommand command, CancellationToken token)
    {
        string stage = Path.Combine(root, DirectoryName, Guid.NewGuid().ToString("N"));
        command = command with { RootDirectory = root };
        Validate(root, stage, command);
        Directory.CreateDirectory(stage);
        await WriteAsync(stage, "intent.json", JsonSerializer.SerializeToUtf8Bytes(new ModpackIntent(1, command), ModpackJournalJson.Default.ModpackIntent), token).ConfigureAwait(false);
        return new(stage, command);
    }
    internal static async Task<ModpackInstallJournal> OpenAsync(string root, string stage, CancellationToken token)
    {
        var intent = JsonSerializer.Deserialize(await ReadAsync(stage, "intent.json", 65536, token).ConfigureAwait(false), ModpackJournalJson.Default.ModpackIntent)
            ?? throw new InvalidDataException("整合包任务为空。");
        if (intent.Schema != 1) throw new InvalidDataException("整合包任务版本无效。");
        Validate(root, stage, intent.Command); return new(stage, intent.Command);
    }
    private static void Validate(string root, string stage, MinecraftModpackCommand command)
    {
        if (!Path.IsPathFullyQualified(root) || !MinecraftLibraryService.PathComparer.Equals(root, Path.GetFullPath(root))
            || !MinecraftLibraryService.PathComparer.Equals(Path.GetDirectoryName(stage), Path.Combine(root, DirectoryName))
            || !Guid.TryParseExact(Path.GetFileName(stage), "N", out _) || command?.Pack is null
            || !MinecraftLibraryService.PathComparer.Equals(command.RootDirectory, root)
            || !MinecraftVersionPaths.IsSafeReference(command.Pack.InstanceId)
            || command.Pack.Sha256 is not { Length: 64 } || !command.Pack.Sha256.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("整合包任务身份无效。");
        RecoveryBlobStore.CheckLinks(stage);
    }
    internal FileStream Acquire()
    {
        string path = Path.Combine(Stage, "lock"); RecoveryBlobStore.CheckLinks(path);
        return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    internal async Task<ModpackFile[]?> ReadFilesAsync(CancellationToken token)
    {
        if (!File.Exists(Path.Combine(Stage, "files.json")))
        { RecoveryRecordAuthority.VerifyAbsent(Path.Combine(Stage, "files.json")); return null; }
        return JsonSerializer.Deserialize(await ReadAsync(Stage, "files.json", 16 * 1024 * 1024, token).ConfigureAwait(false), ModpackJournalJson.Default.ModpackFileArray)
            ?? throw new InvalidDataException("整合包下载计划为空。");
    }
    internal Task SaveFilesAsync(ModpackFile[] files, CancellationToken token) => WriteAsync(Stage, "files.json", JsonSerializer.SerializeToUtf8Bytes(files, ModpackJournalJson.Default.ModpackFileArray), token);
    internal async Task PrepareAsync(CancellationToken token)
    {
        List<ModpackPublishedFile> files = [];
        long total = 0;
        foreach (string path in Directory.EnumerateFiles(Instance, "*", SearchOption.AllDirectories))
        {
            RecoveryBlobStore.CheckLinks(path);
            await using var input = File.OpenRead(path);
            if (files.Count >= 100000 || input.Length > MinecraftModpackArchive.MaxFile || (total += input.Length) > MinecraftModpackArchive.MaxExpanded)
                throw new InvalidDataException("整合包发布大小超过限制。");
            files.Add(new(Path.GetRelativePath(Instance, path).Replace('\\', '/'), input.Length, Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false))));
        }
        await WriteAsync(Stage, "publication.json", JsonSerializer.SerializeToUtf8Bytes(files.ToArray(), ModpackJournalJson.Default.ModpackPublishedFileArray), token).ConfigureAwait(false);
    }
    internal async Task PublishAsync(CancellationToken token)
    {
        if (Canceled) throw new InvalidOperationException("整合包安装已取消。");
        RecoveryBlobStore.CheckLinks(Instance); RecoveryBlobStore.CheckLinks(Destination);
        bool staged = Directory.Exists(Instance);
        string directory = staged ? Instance : Destination;
        var files = JsonSerializer.Deserialize(await ReadAsync(Stage, "publication.json", 16 * 1024 * 1024, token).ConfigureAwait(false), ModpackJournalJson.Default.ModpackPublishedFileArray)
            ?? throw new InvalidDataException("整合包发布记录为空。");
        if (files.Length is 0 or > 100000 || !files.Any(file => file.Path == Command.Pack.InstanceId + ".json")) throw new InvalidDataException("整合包发布记录无效。");
        HashSet<string> expected = new(MinecraftLibraryService.PathComparer);
        long total = 0;
        foreach (var file in files)
        {
            if (!expected.Add(file.Path) || file.Length < 0 || file.Length > MinecraftModpackArchive.MaxFile
                || (total += file.Length) > MinecraftModpackArchive.MaxExpanded
                || file.Sha256 is not { Length: 64 } || !file.Sha256.All(char.IsAsciiHexDigit)) throw new InvalidDataException("整合包发布清单无效。");
            string path = ForgeInstallService.Contained(directory, MinecraftModpackArchive.SafeRelative(file.Path)); RecoveryBlobStore.CheckLinks(path);
            await using var input = File.OpenRead(path);
            if (input.Length != file.Length || input.Length > MinecraftModpackArchive.MaxFile || Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)) != file.Sha256)
                throw new InvalidDataException("整合包发布文件已改变。");
        }
        if (staged)
        {
            foreach (string path in Directory.EnumerateFiles(Instance, "*", SearchOption.AllDirectories))
            {
                RecoveryBlobStore.CheckLinks(path);
                if (!expected.Contains(Path.GetRelativePath(Instance, path).Replace('\\', '/'))) throw new IOException("整合包暂存目录出现未知文件。");
            }
            if (Path.Exists(Destination)) throw new IOException("已存在同名版本，未覆盖。");
            Directory.CreateDirectory(Path.GetDirectoryName(Destination)!);
            token.ThrowIfCancellationRequested(); Directory.Move(Instance, Destination);
        }
        if (!Complete) await WriteAsync(Stage, "complete", "1"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
        InstallTaskCleanup.TryPrune(Stage, "intent.json", "files.json", "publication.json", "complete", "lock");
    }
    internal async Task CancelAsync()
    {
        if (Complete || Canceled) return;
        if (Prepared && !Directory.Exists(Instance)) { await PublishAsync(CancellationToken.None).ConfigureAwait(false); return; }
        await WriteAsync(Stage, "canceled", "1"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
        InstallTaskCleanup.TryPrune(Stage, "intent.json", "canceled", "lock");
    }
    private bool Marker(string name)
    {
        string path = Path.Combine(Stage, name); RecoveryBlobStore.CheckLinks(path);
        if (!File.Exists(path)) { RecoveryRecordAuthority.VerifyAbsent(path); return false; }
        using var input = File.OpenRead(path);
        if (input.ReadByte() != '1' || input.ReadByte() != -1) throw new InvalidDataException("整合包任务状态无效。");
        RecoveryRecordAuthority.Verify(path, "1"u8);
        return true;
    }
    private static async Task<byte[]> ReadAsync(string stage, string name, int limit, CancellationToken token)
    {
        string path = Path.Combine(stage, name); RecoveryBlobStore.CheckLinks(path);
        await using var input = File.OpenRead(path);
        if (input.Length > limit) throw new InvalidDataException("整合包记录过大。");
        byte[] bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (input.ReadByte() != -1) throw new IOException("整合包记录读取期间发生变化。");
        RecoveryRecordAuthority.Verify(path, bytes);
        return bytes;
    }
    private static async Task WriteAsync(string stage, string name, byte[] bytes, CancellationToken token)
    {
        if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("整合包记录过大。");
        string path = Path.Combine(stage, name), temp = path + "." + Guid.NewGuid().ToString("N");
        RecoveryBlobStore.CheckLinks(path);
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await output.WriteAsync(bytes, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true); }
            await RecoveryRecordAuthority.AuthorizeAsync(path, bytes, token).ConfigureAwait(false);
            File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ModpackIntent))]
[JsonSerializable(typeof(ModpackFile[]))]
[JsonSerializable(typeof(ModpackPublishedFile[]))]
internal partial class ModpackJournalJson : JsonSerializerContext;
