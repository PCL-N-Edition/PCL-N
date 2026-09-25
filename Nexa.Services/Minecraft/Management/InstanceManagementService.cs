using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Process;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceManagementQuery(string InstanceDirectory)
{
    public bool IncludeRecoveryStorage { get; init; }
    public bool IncludeTrash { get; init; }
}
public sealed record InstanceManagementPage(string Id, string Label, string? Directory = null);
public sealed record InstanceContentEntry(string Name, bool IsDirectory, long? Size)
{
    public bool? Enabled { get; init; }
    public long ModifiedUtcTicks { get; init; }
}
public sealed record InstanceContentSnapshot(string PageId, IReadOnlyList<InstanceContentEntry> Entries, bool Complete, string? Error);
public sealed record InstanceManagementSnapshot(string InstanceDirectory, string GameDirectory, string GameVersion,
    IReadOnlyList<InstallBuildSelection> Components, IReadOnlyList<InstanceManagementPage> Pages,
    bool ModInventoryComplete, string ModpackVersion)
{
    public IReadOnlyList<InstanceContentSnapshot> Contents { get; init; } = [];
    public string Description { get; init; } = "";
    public InstanceRecoveryStorage? RecoveryStorage { get; init; }
    public InstanceRecoveryReport? RecoveryComparison { get; init; }
    public IReadOnlyList<InstanceTrashedContent> Trash { get; init; } = [];
}

public static class InstanceManagementContract
{
    public static readonly XsrSemanticId Query = XsrSemanticId.Parse("minecraft.instance.management.query");
    public static readonly XsrSemanticId SetModEnabled = XsrSemanticId.Parse("minecraft.instance.mod.set-enabled");
    public static readonly XsrSemanticId RemoveContent = XsrSemanticId.Parse("minecraft.instance.content.remove");
    public static readonly XsrSemanticId RestoreContent = XsrSemanticId.Parse("minecraft.instance.content.restore");
}

public static class InstanceManagementService
{
    public static Task<InstanceManagementSnapshot> ReadAsync(InstanceManagementQuery query, CancellationToken token = default) =>
        Task.Run(async () =>
        {
            if (!Path.IsPathFullyQualified(query.InstanceDirectory)) throw new InvalidDataException("请选择有效的版本目录。");
            string instance = Path.TrimEndingDirectorySeparator(Path.GetFullPath(query.InstanceDirectory));
            var versions = Directory.GetParent(instance);
            if (versions?.Name != "versions" || versions.Parent is null
                || !MinecraftVersionPaths.IsSafeReference(Path.GetFileName(instance)))
                throw new InvalidDataException("版本必须位于游戏目录的 versions 下。");
            for (string? current = instance; current is not null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("不能通过链接读取版本管理目录。");
            var metadata = await new MinecraftInstanceMetadataStore().LoadAsync(instance, token).ConfigureAwait(false);
            string gameDirectory = metadata.InstanceIsolation ? instance : versions.Parent.FullName;
            var edit = await MinecraftInstallEditService.ReadAsync(new(versions.Parent.FullName, Path.GetFileName(instance)), token).ConfigureAwait(false);
            var inventory = await LaunchModInventoryReader.ReadAsync(gameDirectory, token).ConfigureAwait(false);
            var pages = Pages(gameDirectory, edit.Selection, inventory);
            return new InstanceManagementSnapshot(instance, gameDirectory, edit.GameVersion, edit.Selection,
                pages, inventory.Complete, metadata.ModpackVersion)
            {
                Description = metadata.Description,
                Trash = query.IncludeTrash ? InstanceContentTrash.Read(instance, gameDirectory) : [],
                RecoveryStorage = query.IncludeRecoveryStorage ? await InstanceRecoveryStorageReader.ReadAsync(instance, gameDirectory, token).ConfigureAwait(false) : null,
                Contents = Array.AsReadOnly(pages.Where(page => page.Directory is not null)
                    .Select(page => ReadContent(page, token)).ToArray()),
            };
        }, token);

    internal static InstanceContentSnapshot ReadContent(InstanceManagementPage page, CancellationToken token, int limit = 10000)
    {
        if (limit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(limit));
        List<InstanceContentEntry> entries = [];
        bool complete = true;
        try
        {
            string directory = page.Directory!;
            if (!Directory.Exists(directory)) return new(page.Id, [], true, null);
            for (string? current = directory; current is not null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("不能读取链接目录。");
            foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (entries.Count == limit) { complete = false; break; }
                entries.Add(new(item.Name, item is DirectoryInfo, item is FileInfo file ? file.Length : null)
                {
                    ModifiedUtcTicks = item.LastWriteTimeUtc.Ticks,
                    Enabled = page.Id == "mods" && item is FileInfo ? InstanceContentService.ModEnabled(item.Name) : null,
                });
            }
            return new(page.Id, Array.AsReadOnly(entries.OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray()), complete, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(page.Id, [], false, "无法读取此目录，请检查访问权限后重试。"); }
    }

    internal static IReadOnlyList<InstanceManagementPage> Pages(string gameDirectory,
        IReadOnlyList<InstallBuildSelection> components, LaunchModInventory inventory)
    {
        bool modLoader = components.Any(item => item.Loader is InstallLoader.Forge or InstallLoader.NeoForge
            or InstallLoader.Cleanroom or InstallLoader.Fabric or InstallLoader.LegacyFabric or InstallLoader.Quilt or InstallLoader.LiteLoader);
        var enabled = inventory.Mods.Where(item => item.Enabled).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        bool shaders = components.Any(item => item.Loader == InstallLoader.OptiFine)
            || modLoader && enabled.Overlaps(["iris", "oculus", "optifine", "angelica"]);
        bool schematics = modLoader && enabled.Overlaps(["litematica", "schematica", "worldedit", "axiom", "syncmatica", "baritone"]);
        List<InstanceManagementPage> pages = [new("overview", "总览"), new("game", "游戏设置"), new("recovery", "快照与存储")];
        if (modLoader) pages.Add(new("mods", "模组", Path.Combine(gameDirectory, "mods")));
        pages.Add(new("resourcepacks", "资源包", Path.Combine(gameDirectory, "resourcepacks")));
        if (shaders) pages.Add(new("shaderpacks", "光影包", Path.Combine(gameDirectory, "shaderpacks")));
        if (schematics) pages.Add(new("schematics", "蓝图", Path.Combine(gameDirectory, "schematics")));
        pages.AddRange([new("saves", "存档", Path.Combine(gameDirectory, "saves")),
            new("screenshots", "截图", Path.Combine(gameDirectory, "screenshots")),
            new("servers", "服务器"), new("modpack", "整合包与导出"), new("trash", "已移除内容")]);
        return pages.AsReadOnly();
    }
}
