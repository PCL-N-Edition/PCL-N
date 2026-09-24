using System.Text.Json;
using Nexa.Services.Tasks;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Install;

public enum MinecraftFolderKind { Unsupported, GameRoot, Version }
public sealed record MinecraftFolderInspectQuery(string Path);
public sealed record MinecraftFolderInspection(MinecraftFolderKind Kind, string Path, string Name);
public sealed record MinecraftFolderImportCommand(string Source, string TargetRoot);
public static class MinecraftFolderImportContract
{
    public static readonly XsrSemanticId Inspect = XsrSemanticId.Parse("minecraft.folder.inspect");
    public static readonly XsrSemanticId Import = XsrSemanticId.Parse("minecraft.folder.import");
}

/// <summary>Local copies are staged outside versions; existing data is never overwritten.</summary>
public sealed class MinecraftFolderImportService(TaskCenterService tasks)
{
    public static Task<MinecraftFolderInspection> InspectAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Inspect(path), cancellationToken);

    private static MinecraftFolderInspection Inspect(string path)
    {
        string full = MinecraftLibraryService.NormalizeDirectory(path);
        if (!Directory.Exists(full)) return new(MinecraftFolderKind.Unsupported, full, Path.GetFileName(full));
        RejectLinks(full);
        if (Directory.Exists(Path.Combine(full, "versions")))
        {
            RejectLinks(Path.Combine(full, "versions"));
            return new(MinecraftFolderKind.GameRoot, full, Path.GetFileName(full));
        }
        string nested = Path.Combine(full, ".minecraft");
        if (Directory.Exists(Path.Combine(nested, "versions"))) return Inspect(nested);
        string name = Path.GetFileName(full);
        string manifest = Path.Combine(full, name + ".json");
        if (!MinecraftVersionPaths.IsSafeReference(name) || !File.Exists(manifest))
            return new(MinecraftFolderKind.Unsupported, full, name);
        if (!IsVersionManifest(manifest)) return new(MinecraftFolderKind.Unsupported, full, name);
        return new(MinecraftFolderKind.Version, full, name);
    }

    private static bool IsVersionManifest(string manifest)
    {
        RejectLinks(manifest);
        if (new FileInfo(manifest).Length > 4 * 1024 * 1024) throw new InvalidDataException("版本描述文件过大。");
        using var stream = File.OpenRead(manifest);
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var id)
            || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())
            || (!HasText(root, "mainClass") && !HasText(root, "inheritsFrom"))) return false;
        return true;
    }

    private static bool HasText(JsonElement element, string name) => element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString());

    public Task<XsrResult> ImportAsync(MinecraftFolderImportCommand command, CancellationToken cancellationToken = default) =>
        Task.Run(() => CopyAsync(command, cancellationToken), cancellationToken);

    private async Task<XsrResult> CopyAsync(MinecraftFolderImportCommand command, CancellationToken cancellationToken)
    {
        using var task = tasks.Begin(new("folder:" + Guid.NewGuid().ToString("N"), "导入本地版本", ["检查文件", "复制文件", "完成"]));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, task.CancellationToken);
        var token = linked.Token;
        string? staging = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var source = Inspect(command.Source);
            if (source.Kind != MinecraftFolderKind.Version) throw new InvalidDataException("该文件夹不是可导入的版本目录。");
            string target = MinecraftLibraryService.NormalizeDirectory(command.TargetRoot);
            RejectLinks(target);
            if (IsWithin(target, source.Path) || IsWithin(source.Path, target))
                throw new InvalidDataException("源版本与目标游戏目录不能相互包含。请直接使用原游戏目录。");
            string versions = Path.Combine(target, "versions");
            Directory.CreateDirectory(versions);
            RejectLinks(versions);
            string destination = Path.Combine(versions, source.Name);
            if (Path.Exists(destination)) throw new IOException("目标目录中已存在同名版本，未覆盖任何文件。");
            // Collect without following links, including links higher in either root path.
            var files = new List<(string Path, string Relative)>();
            var directories = new List<string>();
            var pending = new Stack<string>();
            pending.Push(source.Path);
            while (pending.TryPop(out string? directory))
            {
                token.ThrowIfCancellationRequested();
                RejectLinks(directory);
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    RejectLinks(entry);
                    string relative = Path.GetRelativePath(source.Path, entry);
                    if (Directory.Exists(entry)) { pending.Push(entry); directories.Add(relative); }
                    else files.Add((entry, relative));
                    if (files.Count + directories.Count > 100_000) throw new InvalidDataException("版本目录中的文件数量过多。");
                }
            }
            staging = Path.Combine(target, ".nexa-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            foreach (string directory in directories) Directory.CreateDirectory(Path.Combine(staging, directory));
            int count = 0;
            long lastReport = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                RejectLinks(file.Path);
                await using var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                await using var output = new FileStream(Path.Combine(staging, file.Relative), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await input.CopyToAsync(output, token).ConfigureAwait(false);
                count++;
                if (count == files.Count || System.Diagnostics.Stopwatch.GetElapsedTime(lastReport).TotalMilliseconds >= 100)
                {
                    task.Report("复制文件", file.Relative, (double)count / Math.Max(1, files.Count), count, files.Count, 0);
                    lastReport = System.Diagnostics.Stopwatch.GetTimestamp();
                }
            }
            token.ThrowIfCancellationRequested();
            if (!IsVersionManifest(Path.Combine(staging, source.Name + ".json")))
                throw new InvalidDataException("复制期间版本描述发生变化，导入未提交。");
            // Recheck the destination immediately before committing; Move never overwrites.
            RejectLinks(target);
            RejectLinks(versions);
            Directory.Move(staging, destination);
            staging = null;
            task.Complete("版本已导入，源文件保留。");
            return XsrResult.Success();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { task.Canceled(); return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException)
        { task.Fail(error.Message); return XsrResult.Failure(MinecraftErrors.InvalidRequest(error.Message)); }
        finally
        {
            if (staging is not null && Directory.Exists(staging))
            {
                try { RejectLinks(staging); Directory.Delete(staging, true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Only our uncommitted staging remains. */ }
            }
        }
    }

    private static bool IsWithin(string path, string parent) => MinecraftLibraryService.PathComparer.Equals(path, parent)
        || path.StartsWith(parent + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void RejectLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("暂不支持导入含符号链接或目录联接的文件夹。");
    }
}
