using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Tasks;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Install;

public sealed record MinecraftInstallEditQuery(string RootDirectory, string InstanceId);
public sealed record MinecraftInstallEditSnapshot(string RootDirectory, string InstanceId, string GameVersion,
    IReadOnlyList<InstallBuildSelection> Selection, string Fingerprint)
{
    public IReadOnlyList<MinecraftInstallManagedFile> ManagedMods { get; init; } = [];
    public string ModsRelativeDirectory { get; init; } = "mods";
}
public sealed record MinecraftInstallManagedFile(string Path, string Sha256, InstallLoader? Loader = null);
public static class MinecraftInstallEditContract
{
    public static readonly XsrSemanticId Query = XsrSemanticId.Parse("minecraft.install.edit");
}

public static class MinecraftInstallEditService
{
    public static Task<MinecraftInstallEditSnapshot> ReadAsync(MinecraftInstallEditQuery query, CancellationToken token = default) =>
        Task.Run(() => ReadCoreAsync(query, token), token);

    private static async Task<MinecraftInstallEditSnapshot> ReadCoreAsync(MinecraftInstallEditQuery query, CancellationToken token)
    {
        if (!MinecraftVersionPaths.IsSafeReference(query.InstanceId)) throw new InvalidDataException("版本目录无效。");
        string root = Path.GetFullPath(query.RootDirectory);
        string path = ForgeInstallService.Contained(root, $"versions/{query.InstanceId}/{query.InstanceId}.json");
        byte[] bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        var json = JsonNode.Parse(bytes)!.AsObject();
        List<InstallBuildSelection> selections = [];
        string? game = json["_nexaInstall"]?["game"]?.ToString();
        if (json["_nexaInstall"] is JsonObject receipt)
        {
            if (Enum.TryParse<InstallLoader>(receipt["loader"]?.ToString(), out var loader) && receipt["build"] is { } build)
                selections.Add(new(loader, build.ToString()));
            foreach (var addon in receipt["addons"] as JsonArray ?? [])
                if (Enum.TryParse<InstallLoader>(addon?["loader"]?.ToString(), out var kind) && addon?["build"] is { } value)
                    selections.Add(new(kind, value.ToString()));
        }
        {
            foreach (var lib in json["libraries"] as JsonArray ?? [])
            {
                string[] coordinate = (lib?["name"]?.ToString() ?? "").Split(':');
                if (coordinate.Length < 3) continue;
                InstallLoader? loader = (coordinate[0], coordinate[1]) switch
                {
                    ("net.minecraftforge", "forge") => InstallLoader.Forge,
                    ("net.neoforged", "neoforge" or "forge") => InstallLoader.NeoForge,
                    ("com.cleanroommc", "cleanroom") => InstallLoader.Cleanroom,
                    ("net.fabricmc", "fabric-loader") => InstallLoader.Fabric,
                    ("net.legacyfabric", "fabric-loader") => InstallLoader.LegacyFabric,
                    ("org.quiltmc", "quilt-loader") => InstallLoader.Quilt,
                    ("optifine", "OptiFine") => InstallLoader.OptiFine,
                    ("com.mumfrey", "liteloader") => InstallLoader.LiteLoader,
                    ("net.labymod", "LabyMod") => InstallLoader.LabyMod,
                    _ => null,
                };
                if (loader is null) continue;
                string build = coordinate[2];
                if (loader == InstallLoader.Forge || loader == InstallLoader.NeoForge && coordinate[1] == "forge")
                { int dash = build.IndexOf('-'); if (dash > 0) { game ??= build[..dash]; build = build[(dash + 1)..]; } }
                if (loader == InstallLoader.LabyMod)
                {
                    string url = lib?["downloads"]?["artifact"]?["url"]?.ToString() ?? "";
                    int separator = build.LastIndexOf('-');
                    if (separator > 0)
                        build = (url.Contains("/snapshot/", StringComparison.Ordinal) ? "snapshot" : "production") + "+" + build[..separator] + "+" + build[(separator + 1)..];
                }
                if (loader == InstallLoader.Cleanroom) game ??= "1.12.2";
                if (loader == InstallLoader.OptiFine) game ??= build.Split('_')[0];
                int index = selections.FindIndex(item => item.Loader == loader);
                if (index < 0) selections.Add(new(loader.Value, build));
                else selections[index] = new(loader.Value, build);
            }
        }
        var arguments = (json["arguments"]?["game"] as JsonArray ?? []).OfType<JsonValue>().Select(value => value.ToString()).ToArray();
        string? Option(string name)
        {
            int index = Array.IndexOf(arguments, name);
            return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
        }
        void Select(InstallLoader loader, string version)
        {
            selections.RemoveAll(item => item.Loader == loader);
            selections.Insert(0, new(loader, version));
        }
        string? neo = Option("--fml.neoForgeVersion"), forge = Option("--fml.forgeVersion");
        if (neo is not null) Select(InstallLoader.NeoForge, neo);
        else if (forge is not null && !selections.Any(item => item.Loader is InstallLoader.Cleanroom or InstallLoader.NeoForge)) Select(InstallLoader.Forge, forge);
        if (selections.Any(item => item.Loader == InstallLoader.Cleanroom)) selections.RemoveAll(item => item.Loader == InstallLoader.Forge);
        game = Option("--fml.mcVersion") ?? game;
        game ??= json["_minecraftVersion"]?.ToString() ?? json["clientVersion"]?.ToString() ?? json["jar"]?.ToString();
        var current = json; HashSet<string> visited = [query.InstanceId];
        while (game is null && current["inheritsFrom"] is { } parent)
        {
            string id = parent.ToString();
            if (!MinecraftVersionPaths.IsSafeReference(id) || !visited.Add(id)) throw new InvalidDataException("版本继承关系无效。");
            current = await MinecraftVersionJsonReader.ReadAsync(ForgeInstallService.Contained(root, $"versions/{id}/{id}.json"), token).ConfigureAwait(false);
            if (current["inheritsFrom"] is null) game = current["id"]?.ToString();
        }
        game ??= json["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(game) || !MinecraftVersionPaths.IsSafeReference(game)) throw new InvalidDataException("无法确定 Minecraft 本体版本。");
        var managed = (json["_nexaInstall"]?["managedMods"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(file => new MinecraftInstallManagedFile(file["path"]!.ToString(), file["sha256"]!.ToString(),
                Enum.TryParse<InstallLoader>(file["loader"]?.ToString(), out var kind) ? kind : null)).ToList();
        string localMods = ForgeInstallService.Contained(root, $"versions/{query.InstanceId}/mods");
        string modsRelative = Directory.Exists(localMods) ? $"versions/{query.InstanceId}/mods" : "mods";
        string mods = ForgeInstallService.Contained(root, modsRelative);
        if (Directory.Exists(mods))
            foreach (string file in Directory.EnumerateFiles(mods, "*.jar"))
            {
                token.ThrowIfCancellationRequested();
                InstallBuildSelection? component = null;
                try
                {
                    using var archive = ZipFile.OpenRead(file);
                    var entry = archive.GetEntry("fabric.mod.json") ?? archive.GetEntry("quilt.mod.json");
                    if (entry is not null && entry.Length <= 1024 * 1024)
                    {
                        using var reader = new StreamReader(entry.Open());
                        var descriptor = JsonNode.Parse(await reader.ReadToEndAsync(token).ConfigureAwait(false))!;
                        descriptor = descriptor["quilt_loader"] ?? descriptor;
                        InstallLoader? kind = descriptor["id"]?.ToString() switch
                        { "fabric-api" => InstallLoader.FabricApi, "qsl" => InstallLoader.Qsl, "optifabric" => InstallLoader.OptiFabric, _ => null };
                        if (kind is not null && descriptor["version"] is { } version) component = new(kind.Value, version.ToString());
                    }
                    string filename = Path.GetFileNameWithoutExtension(file);
                    if (component is null && filename.StartsWith("OptiFine_", StringComparison.OrdinalIgnoreCase)) component = new(InstallLoader.OptiFine, filename[9..]);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException) { continue; }
                if (component is null) continue;
                int selectedIndex = selections.FindIndex(item => item.Loader == component.Loader);
                if (selectedIndex < 0) selections.Add(component);
                else selections[selectedIndex] = component;
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!managed.Any(item => item.Path == relative))
                {
                    await using var stream = File.OpenRead(file);
                    string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                    managed.Add(new(relative, hash, component.Loader));
                }
            }
        return new(root, query.InstanceId, game, selections.AsReadOnly(), Convert.ToHexString(SHA256.HashData(bytes)))
        { ManagedMods = managed.AsReadOnly(), ModsRelativeDirectory = modsRelative };
    }
}

public sealed partial class MinecraftInstallService
{
    private async Task<MinecraftInstallResult> ReinstallAsync(MinecraftInstallCommand command, ITaskCenterTask task, CancellationToken token)
    {
        string instance = command.InstanceName ?? throw new InvalidDataException("未指定要修改的版本。");
        var original = await MinecraftInstallEditService.ReadAsync(new(command.RootDirectory, instance), token).ConfigureAwait(false);
        if (original.GameVersion != command.GameVersion || original.Fingerprint != command.EditFingerprint)
            throw new InvalidDataException("原版本已更改，请重新打开修改页。Minecraft 本体版本不能更改。");
        var selected = new List<InstallBuildSelection>();
        if (command.Loader is { } primary) selected.Add(new(primary, command.LoaderBuild!));
        selected.AddRange((command.Addons ?? []).Select(addon => new InstallBuildSelection(addon.Kind, addon.Version)));
        var editPlan = MinecraftInstallEditPlanner.Evaluate(new(original, selected));
        if (editPlan.Kind == MinecraftInstallEditKind.Unchanged)
        {
            if (command.NewInstanceName is null || command.NewInstanceName == instance) task.Complete("没有需要修改的选项");
            return new(instance, Path.Combine(original.RootDirectory, "versions", instance));
        }
        string stage = ForgeInstallService.Contained(original.RootDirectory, ".nexa-modify/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        bool safeToRemove = false;
        try
        {
            _ = await InstallTaskJournal.CreateAsync(stage, command with { RootDirectory = original.RootDirectory }, token).ConfigureAwait(false);
            if (editPlan.Kind == MinecraftInstallEditKind.ComponentsOnly)
                await PrepareComponentEditAsync(command, original, editPlan, stage, task, token).ConfigureAwait(false);
            else
                await RunAsync(command with
                {
                    RootDirectory = stage,
                    EditFingerprint = null,
                    ReuseRoot = original.RootDirectory,
                    PreparingEdit = true,
                    ModsRelativeDirectory = original.ModsRelativeDirectory
                }, task, token, new PersistentInstallMetadataSource(stage, _metadata)).ConfigureAwait(false);
            string relativeManifest = $"versions/{instance}/{instance}.json";
            string targetManifest = ForgeInstallService.Contained(original.RootDirectory, relativeManifest);
            var current = await MinecraftInstallEditService.ReadAsync(new(original.RootDirectory, instance), token).ConfigureAwait(false);
            if (current.Fingerprint != original.Fingerprint) throw new InvalidDataException("安装期间原版本已更改，请重试。");
            string[] generatedFiles = Directory.GetFiles(stage, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(stage, file).Replace('\\', '/'))
                .Where(relative => !relative.StartsWith(InstallTaskJournal.DirectoryName + "/", StringComparison.Ordinal))
                .Where(relative => relative == relativeManifest || !(relative.StartsWith("versions/", StringComparison.Ordinal)
                    && relative.EndsWith(".json", StringComparison.Ordinal) && File.Exists(ForgeInstallService.Contained(original.RootDirectory, relative))))
                .ToArray();
            var removals = original.ManagedMods.Where(mod => editPlan.Kind != MinecraftInstallEditKind.ComponentsOnly
                    || mod.Loader is { } kind && editPlan.ChangedLoaders.Contains(kind))
                .ToDictionary(mod => mod.Path.Replace('\\', '/'), mod => mod.Sha256, MinecraftLibraryService.PathComparer);
            if (removals.Keys.Any(path => !path.StartsWith(original.ModsRelativeDirectory + "/", StringComparison.Ordinal)))
                throw new InvalidDataException("受管理 Mod 的路径无效。");
            var publication = await InstallPublicationJournal.PrepareAsync(original.RootDirectory, stage, instance, generatedFiles, removals, token).ConfigureAwait(false);
            try { await publication.ApplyAsync(token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            {
                // Cancellation compensates too. A failed compensation retains the durable stage.
                try { await publication.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception rollbackError) when (rollbackError is not OutOfMemoryException and not AccessViolationException)
                { throw new AggregateException("安装发布及回滚未完成，已保留事务备份。", error, rollbackError); }
                safeToRemove = true;
                throw;
            }
            safeToRemove = true;
            if (command.NewInstanceName is null || command.NewInstanceName == instance)
            {
                task.Complete($"已修改 {instance}");
                Installed?.Invoke(original.RootDirectory);
            }
            return new(instance, Path.GetDirectoryName(targetManifest)!);
        }
        finally
        {
            // An interrupted/failed publication owns the only durable originals. Keep it until resolved.
            if (safeToRemove || !File.Exists(Path.Combine(stage, ".publication", "progress.json")))
            {
                Management.RecoveryBlobStore.CheckLinks(stage);
                try { Directory.Delete(stage, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static async Task CopyAtomicAsync(string source, string destination, CancellationToken token)
    {
        string temporary = destination + ".nexa-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = File.Create(temporary))
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }
}
