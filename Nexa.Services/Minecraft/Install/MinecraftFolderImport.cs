using System.Text.Json;
using Nexa.Services.Tasks;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Install;

public enum MinecraftFolderKind { Unsupported, GameRoot, Version, Jar, Modpack }
public sealed record MinecraftFolderInspectQuery(string Path);
public sealed record MinecraftFolderInspection(MinecraftFolderKind Kind, string Path, string Name)
{
    public LocalJarArtifact? Jar { get; init; }
    public MinecraftModpackPreview? Modpack { get; init; }
}
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
        Task.Run(async () =>
        {
            if (File.Exists(path) && new[] { ".mrpack", ".zip" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                var pack = await MinecraftModpackArchive.InspectAsync(path, cancellationToken).ConfigureAwait(false);
                return new MinecraftFolderInspection(MinecraftFolderKind.Modpack, pack.Path, pack.Name) { Modpack = pack };
            }
            if (File.Exists(path) && Path.GetExtension(path).Equals(".jar", StringComparison.OrdinalIgnoreCase))
            {
                var jar = await MinecraftLocalJarService.InspectAsync(path, cancellationToken).ConfigureAwait(false);
                return new MinecraftFolderInspection(MinecraftFolderKind.Jar, jar.Path, Path.GetFileName(jar.Path)) { Jar = jar };
            }
            return Inspect(path);
        }, cancellationToken);

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

    private static void ValidateInheritance(string manifest, string target, CancellationToken token, string? stagedRoot = null)
    {
        var visited = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(Path.GetFullPath(manifest)) || visited.Count > 100)
                throw new InvalidDataException("版本继承链存在循环或过深，导入未提交。");
            if (!IsVersionManifest(manifest)) throw new InvalidDataException("父版本描述无效，导入未提交。");
            using var stream = File.OpenRead(manifest);
            using var document = JsonDocument.Parse(stream);
            if (!HasText(document.RootElement, "inheritsFrom")) return;
            string parent = document.RootElement.GetProperty("inheritsFrom").GetString()!;
            if (!MinecraftVersionPaths.IsSafeReference(parent)) throw new InvalidDataException("父版本名称无效。");
            manifest = (stagedRoot is null ? null : MinecraftVersionPaths.ResolveJsonPath(stagedRoot, null, parent))
                ?? MinecraftVersionPaths.ResolveJsonPath(target, Path.GetDirectoryName(manifest), parent)
                ?? throw new InvalidDataException($"缺少父版本 {parent}，尚未导入。请先将父版本导入目标游戏目录。");
        }
    }

    public Task<XsrResult> ImportAsync(MinecraftFolderImportCommand command, CancellationToken cancellationToken = default) =>
        Task.Run(() => CopyAsync(command, cancellationToken), cancellationToken);

    private static async Task StageParentsAsync(string manifest, string? sourceRoot, string target, string staging,
        HashSet<string> visited, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsVersionManifest(manifest)) throw new InvalidDataException("父版本描述无效，导入未提交。");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifest, token).ConfigureAwait(false));
        if (!HasText(document.RootElement, "inheritsFrom")) return;
        string parent = document.RootElement.GetProperty("inheritsFrom").GetString()!;
        if (!MinecraftVersionPaths.IsSafeReference(parent) || !visited.Add(parent) || visited.Count > 100)
            throw new InvalidDataException("版本继承链存在无效名称、循环或过深，导入未提交。");
        string? existing = MinecraftVersionPaths.ResolveJsonPath(target, null, parent);
        string? source = sourceRoot is null ? null : MinecraftVersionPaths.ResolveJsonPath(sourceRoot, null, parent);
        if (existing is not null)
        {
            if (!IsVersionManifest(existing)) throw new InvalidDataException("目标父版本描述无效。");
            if (source is not null)
            {
                if (!IsVersionManifest(source)) throw new InvalidDataException("来源父版本描述无效。");
                byte[] first = await File.ReadAllBytesAsync(source, token).ConfigureAwait(false);
                byte[] second = await File.ReadAllBytesAsync(existing, token).ConfigureAwait(false);
                if (!first.SequenceEqual(second)) throw new InvalidDataException($"父版本 {parent} 与目标目录中的版本冲突，未覆盖已有文件。");
            }
            await StageParentsAsync(existing, sourceRoot, target, staging, visited, token).ConfigureAwait(false);
            return;
        }
        if (source is null) throw new InvalidDataException($"缺少父版本 {parent}，来源和目标目录均未找到，导入未提交。");
        if (!IsVersionManifest(source)) throw new InvalidDataException("来源父版本描述无效。");
        string destination = Path.Combine(staging, "versions", parent);
        Directory.CreateDirectory(destination);
        string copied = Path.Combine(destination, parent + ".json");
        await CopyFileAsync(source, copied, token).ConfigureAwait(false);
        string jar = Path.Combine(Path.GetDirectoryName(source)!, parent + ".jar");
        if (File.Exists(jar)) await CopyFileAsync(jar, Path.Combine(destination, parent + ".jar"), token).ConfigureAwait(false);
        await StageParentsAsync(copied, sourceRoot, target, staging, visited, token).ConfigureAwait(false);
    }

    private static async Task CopyFileAsync(string source, string target, CancellationToken token)
    {
        RejectLinks(source);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, token).ConfigureAwait(false);
    }

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
            using var recoveryOperation = await Management.InstanceRecoveryOperationGate.EnterOperationAsync(target, token).ConfigureAwait(false);
            RejectLinks(target);
            if (IsWithin(target, source.Path) || IsWithin(source.Path, target))
                throw new InvalidDataException("源版本与目标游戏目录不能相互包含。请直接使用原游戏目录。");
            string versions = Path.Combine(target, "versions");
            Directory.CreateDirectory(versions);
            RejectLinks(versions);
            string destination = Path.Combine(versions, source.Name);
            if (Path.Exists(destination)) throw new IOException("目标目录中已存在同名版本，未覆盖任何文件。");
            staging = Path.Combine(target, ".nexa-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(staging, "versions"));
            var sourceParent = Directory.GetParent(source.Path);
            string? sourceRoot = sourceParent?.Name == "versions" ? sourceParent.Parent?.FullName : null;
            await StageParentsAsync(Path.Combine(source.Path, source.Name + ".json"), sourceRoot, target, staging,
                new HashSet<string>(MinecraftLibraryService.PathComparer) { source.Name }, token).ConfigureAwait(false);
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
            string leafStaging = Path.Combine(staging, "versions", source.Name);
            Directory.CreateDirectory(leafStaging);
            foreach (string directory in directories) Directory.CreateDirectory(Path.Combine(leafStaging, directory));
            int count = 0;
            long lastReport = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                RejectLinks(file.Path);
                await using var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                await using var output = new FileStream(Path.Combine(leafStaging, file.Relative), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await input.CopyToAsync(output, token).ConfigureAwait(false);
                count++;
                if (count == files.Count || System.Diagnostics.Stopwatch.GetElapsedTime(lastReport).TotalMilliseconds >= 100)
                {
                    task.Report("复制文件", file.Relative, (double)count / Math.Max(1, files.Count), count, files.Count, 0);
                    lastReport = System.Diagnostics.Stopwatch.GetTimestamp();
                }
            }
            token.ThrowIfCancellationRequested();
            if (!IsVersionManifest(Path.Combine(leafStaging, source.Name + ".json")))
                throw new InvalidDataException("复制期间版本描述发生变化，导入未提交。");
            ValidateInheritance(Path.Combine(leafStaging, source.Name + ".json"), target, token, staging);
            // Recheck the destination immediately before committing; Move never overwrites.
            RejectLinks(target);
            RejectLinks(versions);
            foreach (string dependency in Directory.EnumerateDirectories(Path.Combine(staging, "versions")))
            {
                if (MinecraftLibraryService.PathComparer.Equals(dependency, leafStaging)) continue;
                token.ThrowIfCancellationRequested();
                Directory.Move(dependency, Path.Combine(versions, Path.GetFileName(dependency)));
            }
            ValidateInheritance(Path.Combine(leafStaging, source.Name + ".json"), target, token);
            Directory.Move(leafStaging, destination);
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
