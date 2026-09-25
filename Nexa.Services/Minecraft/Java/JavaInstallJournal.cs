using System.Text.Json;
using System.Text.Json.Serialization;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Minecraft.Java;

internal sealed record JavaInstallIntent(int Schema, string Root, string Component);

internal sealed class JavaInstallJournal
{
    internal const string DirectoryName = ".nexa-java-jobs";
    internal string Stage { get; }
    internal string Root { get; }
    internal string Payload => Path.Combine(Stage, "payload");
    private string Backup => Path.Combine(Stage, "previous");
    internal JavaInstallIntent Intent { get; }
    internal bool Completed => Marker("complete");
    internal bool Ready => Marker("ready");
    internal bool Canceled => Marker("canceled");
    internal bool CancelRequested => Marker("cancel-requested");
    private JavaInstallJournal(string root, string stage, JavaInstallIntent intent) { Root = root; Stage = stage; Intent = intent; }

    internal static async Task<JavaInstallJournal> CreateAsync(string root, string component, CancellationToken token)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!SafeName(component)) throw new InvalidDataException("Java 组件名称无效。");
        string stage = Path.Combine(root, DirectoryName, Guid.NewGuid().ToString("N"));
        RecoveryBlobStore.CheckLinks(stage); Directory.CreateDirectory(stage);
        var intent = new JavaInstallIntent(1, root, component);
        await WriteAsync(Path.Combine(stage, "intent.json"), JsonSerializer.SerializeToUtf8Bytes(intent, JavaInstallJsonContext.Default.JavaInstallIntent), token).ConfigureAwait(false);
        return new(root, stage, intent);
    }

    internal static async Task<JavaInstallJournal> OpenAsync(string root, string stage, CancellationToken token)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!MinecraftLibraryService.PathComparer.Equals(Path.GetDirectoryName(stage), Path.Combine(root, DirectoryName))
            || !Guid.TryParseExact(Path.GetFileName(stage), "N", out _)) throw new InvalidDataException("Java 恢复目录无效。");
        RecoveryBlobStore.CheckLinks(stage);
        var bytes = await ReadAsync(Path.Combine(stage, "intent.json"), 4096, token).ConfigureAwait(false);
        var intent = JsonSerializer.Deserialize(bytes, JavaInstallJsonContext.Default.JavaInstallIntent);
        if (intent is null || intent.Schema != 1 || !MinecraftLibraryService.PathComparer.Equals(root, intent.Root) || !SafeName(intent.Component))
            throw new InvalidDataException("Java 恢复请求无效。");
        return new(root, stage, intent);
    }

    internal async Task<JavaRuntimeDownloadPlan?> ReadPlanAsync(CancellationToken token)
    {
        string path = Path.Combine(Stage, "plan.json"); RecoveryBlobStore.CheckLinks(path);
        if (!File.Exists(path)) return null;
        var bytes = await ReadAsync(path, 8 * 1024 * 1024, token).ConfigureAwait(false);
        var plan = JsonSerializer.Deserialize(bytes, JavaInstallJsonContext.Default.JavaRuntimeDownloadPlan) ?? throw new InvalidDataException("Java 文件计划为空。");
        Validate(plan); return plan;
    }

    internal async Task SavePlanAsync(JavaRuntimeDownloadPlan plan, CancellationToken token)
    {
        Validate(plan);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan, JavaInstallJsonContext.Default.JavaRuntimeDownloadPlan);
        if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("Java 文件计划过大。");
        await WriteAsync(Path.Combine(Stage, "plan.json"), bytes, token).ConfigureAwait(false);
    }

    private void Validate(JavaRuntimeDownloadPlan plan)
    {
        if (!SafeName(plan.ComponentName) || !MinecraftLibraryService.PathComparer.Equals(plan.TargetDirectory, Path.Combine(Root, plan.ComponentName))
            || plan.Files is null || plan.Files.Count is 0 or > 20000) throw new InvalidDataException("Java 文件计划无效。");
        long total = 0; HashSet<string> paths = new(MinecraftLibraryService.PathComparer);
        foreach (var file in plan.Files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.RelativePath) || !paths.Add(file.TargetPath)
                || Path.IsPathRooted(file.RelativePath) || file.RelativePath.Replace('\\', '/').Split('/').Any(part => part is "" or "." or "..")
                || !MinecraftLibraryService.PathComparer.Equals(ForgeInstallService.Contained(plan.TargetDirectory, file.RelativePath), file.TargetPath)
                || file.Sha1 is not { Length: 40 } || !file.Sha1.All(char.IsAsciiHexDigit)
                || file.Size is < 0 or > 512L * 1024 * 1024 || (total += file.Size) > 4L * 1024 * 1024 * 1024
                || !Uri.TryCreate(file.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0)
                throw new InvalidDataException("Java 文件身份或路径无效。");
        }
    }

    internal JavaRuntimeDownloadFile StagedFile(JavaRuntimeDownloadFile file) => file with { TargetPath = ForgeInstallService.Contained(Payload, file.RelativePath) };
    internal Task MarkReadyAsync(CancellationToken token) => WriteAsync(Path.Combine(Stage, "ready"), "1"u8.ToArray(), token);
    internal Task CompleteExistingAsync(CancellationToken token) => WriteAsync(Path.Combine(Stage, "complete"), "1"u8.ToArray(), token);
    internal Task MarkCanceledAsync() => WriteAsync(Path.Combine(Stage, "canceled"), "1"u8.ToArray(), CancellationToken.None);

    internal async Task PublishAsync(JavaRuntimeDownloadPlan plan, Func<string, JavaRuntimeDownloadFile, CancellationToken, Task<bool>> verify)
    {
        Validate(plan);
        RecoveryBlobStore.CheckLinks(Payload); RecoveryBlobStore.CheckLinks(Backup); RecoveryBlobStore.CheckLinks(plan.TargetDirectory);
        if (!Ready || Canceled || CancelRequested) throw new InvalidDataException("Java 发布状态无效。");
        if (Directory.Exists(Payload))
        {
            foreach (var file in plan.Files)
                if (!await verify(StagedFile(file).TargetPath, file, CancellationToken.None).ConfigureAwait(false)) throw new InvalidDataException("Java 暂存校验失败。");
            if (Directory.Exists(plan.TargetDirectory))
            {
                if (Directory.Exists(Backup)) throw new IOException("Java 发布目标出现冲突，已保留备份。");
                Directory.Move(plan.TargetDirectory, Backup);
            }
            Directory.Move(Payload, plan.TargetDirectory);
        }
        foreach (var file in plan.Files)
            if (!await verify(file.TargetPath, file, CancellationToken.None).ConfigureAwait(false)) throw new InvalidDataException("Java 发布结果校验失败。");
        if (!Completed) await WriteAsync(Path.Combine(Stage, "complete"), "1"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
    }

    internal async Task CancelAsync(Func<string, JavaRuntimeDownloadFile, CancellationToken, Task<bool>> verify)
    {
        if (Completed || Canceled) return;
        if (!CancelRequested) await WriteAsync(Path.Combine(Stage, "cancel-requested"), "1"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
        var plan = await ReadPlanAsync(CancellationToken.None).ConfigureAwait(false);
        if (Ready && plan is not null)
        {
            RecoveryBlobStore.CheckLinks(Backup); RecoveryBlobStore.CheckLinks(Payload); RecoveryBlobStore.CheckLinks(plan.TargetDirectory);
            if (!Directory.Exists(Payload) && Directory.Exists(plan.TargetDirectory))
            {
                foreach (var file in plan.Files)
                    if (!await verify(file.TargetPath, file, CancellationToken.None).ConfigureAwait(false)) throw new IOException("Java 文件已改变，未覆盖后续修改。");
                Directory.Move(plan.TargetDirectory, Payload);
            }
            if (Directory.Exists(Backup))
            {
                if (Directory.Exists(plan.TargetDirectory)) throw new IOException("Java 原目录恢复发生冲突。");
                Directory.Move(Backup, plan.TargetDirectory);
            }
        }
        await MarkCanceledAsync().ConfigureAwait(false);
    }

    private bool Marker(string name)
    {
        string path = Path.Combine(Stage, name); RecoveryBlobStore.CheckLinks(path);
        if (!File.Exists(path)) return false;
        using var input = File.OpenRead(path);
        if (input.ReadByte() != '1' || input.ReadByte() != -1) throw new InvalidDataException("Java 检查点无效。");
        return true;
    }
    private static bool SafeName(string? text) => text is { Length: > 0 and <= 128 } && text is not "." and not ".."
        && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    private static async Task<byte[]> ReadAsync(string path, int limit, CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(path);
        await using var input = File.OpenRead(path);
        if (input.Length > limit) throw new InvalidDataException("Java 记录过大。");
        byte[] bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (input.ReadByte() != -1) throw new IOException("Java 记录读取期间发生变化。");
        return bytes;
    }
    private static async Task WriteAsync(string path, byte[] bytes, CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(path);
        if (File.Exists(path)) throw new InvalidDataException("Java 检查点不能覆盖。");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await output.WriteAsync(bytes, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true); }
            token.ThrowIfCancellationRequested(); File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(JavaInstallIntent))]
[JsonSerializable(typeof(JavaRuntimeDownloadPlan))]
internal partial class JavaInstallJsonContext : JsonSerializerContext;
