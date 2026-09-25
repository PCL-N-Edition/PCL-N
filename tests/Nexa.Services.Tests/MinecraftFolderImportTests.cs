using Nexa.Services.Minecraft.Install;
using Nexa.Services.Tasks;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask FolderImportCopiesOnlyRequiredParentArtifacts()
    {
        string temporary = CreateTempDirectory();
        try
        {
            string sourceRoot = Path.Combine(temporary, "source"), target = Path.Combine(temporary, "target");
            Directory.CreateDirectory(target);
            string leaf = CreateVersionDirectory(sourceRoot, "leaf", new System.Text.Json.Nodes.JsonObject
            { ["id"] = "leaf", ["inheritsFrom"] = "base" });
            string parent = CreateVersionDirectory(sourceRoot, "base", new System.Text.Json.Nodes.JsonObject
            { ["id"] = "base", ["mainClass"] = "main" });
            await File.WriteAllTextAsync(Path.Combine(parent, "base.jar"), "core");
            Directory.CreateDirectory(Path.Combine(parent, "saves"));
            await File.WriteAllTextAsync(Path.Combine(parent, "saves", "private-world"), "do not copy");
            var service = new MinecraftFolderImportService(NewTaskCenter(out _));
            var result = await service.ImportAsync(new(leaf, target));
            AssertTrue(result.IsSuccess, result.Error?.Message ?? "import");
            AssertEqual("core", await File.ReadAllTextAsync(Path.Combine(target, "versions", "base", "base.jar")));
            AssertFalse(Directory.Exists(Path.Combine(target, "versions", "base", "saves")));
            var instance = (await new Nexa.Services.Minecraft.MinecraftInstanceDiscovery().DiscoverAsync(target)).Single(item => item.Id == "leaf");
            var resolved = await Nexa.Services.Minecraft.Launch.MinecraftVersionJsonReader.ResolveAsync(instance, target);
            AssertEqual(1, resolved.Inherited.Count);
            AssertEqual(0, Directory.GetDirectories(target, ".nexa-import-*").Length);
            string secondLeaf = CreateVersionDirectory(sourceRoot, "second", new System.Text.Json.Nodes.JsonObject
            { ["id"] = "second", ["inheritsFrom"] = "base" });
            AssertTrue((await service.ImportAsync(new(secondLeaf, target))).IsSuccess);
            AssertEqual("core", await File.ReadAllTextAsync(Path.Combine(target, "versions", "base", "base.jar")));

            string conflict = Path.Combine(temporary, "conflict");
            string conflictParent = CreateVersionDirectory(conflict, "base", new System.Text.Json.Nodes.JsonObject
            { ["id"] = "base", ["mainClass"] = "different.Main" });
            AssertFalse((await service.ImportAsync(new(leaf, conflict))).IsSuccess);
            AssertFalse(Directory.Exists(Path.Combine(conflict, "versions", "leaf")));
            AssertTrue((await File.ReadAllTextAsync(Path.Combine(conflictParent, "base.json"))).Contains("different.Main", StringComparison.Ordinal));
            AssertEqual(0, Directory.GetDirectories(conflict, ".nexa-import-*").Length);
            foreach (string ancestor in new[] { "missing", "leaf" })
            {
                await File.WriteAllTextAsync(Path.Combine(parent, "base.json"), "{\"id\":\"base\",\"inheritsFrom\":\"" + ancestor + "\"}");
                string failedRoot = Path.Combine(temporary, ancestor); Directory.CreateDirectory(failedRoot);
                AssertFalse((await service.ImportAsync(new(leaf, failedRoot))).IsSuccess);
                AssertEqual(0, Directory.GetDirectories(Path.Combine(failedRoot, "versions")).Length);
                AssertEqual(0, Directory.GetDirectories(failedRoot, ".nexa-import-*").Length);
            }
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static async ValueTask FolderImportRequiresResolvableParents()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "nexa-parent-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(temporary, "leaf"), target = Path.Combine(temporary, "target");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "leaf.json"), "{\"id\":\"leaf\",\"inheritsFrom\":\"1.20.1\"}");
            var service = new MinecraftFolderImportService(NewTaskCenter(out _));
            var missing = await service.ImportAsync(new(source, target));
            AssertFalse(missing.IsSuccess);
            AssertTrue(missing.Error!.Message.Contains("缺少父版本 1.20.1", StringComparison.Ordinal));
            AssertFalse(Directory.Exists(Path.Combine(target, "versions", "leaf")));
            string parent = Path.Combine(target, "versions", "1.20.1"); Directory.CreateDirectory(parent);
            string manifest = "{\"id\":\"1.20.1\",\"mainClass\":\"main\"}";
            await File.WriteAllTextAsync(Path.Combine(parent, "1.20.1.json"), manifest);
            AssertTrue((await service.ImportAsync(new(source, target))).IsSuccess);
            AssertEqual(manifest, await File.ReadAllTextAsync(Path.Combine(parent, "1.20.1.json")));
            var instance = (await new Nexa.Services.Minecraft.MinecraftInstanceDiscovery().DiscoverAsync(target)).Single(item => item.Id == "leaf");
            var resolved = await Nexa.Services.Minecraft.Launch.MinecraftVersionJsonReader.ResolveAsync(instance, target);
            AssertEqual(1, resolved.Inherited.Count);
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static async ValueTask FolderImportPreservesSourceAndRejectsConflicts()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "nexa-import-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string source = Path.Combine(temporary, "example");
            string target = Path.Combine(temporary, "target");
            Directory.CreateDirectory(Path.Combine(source, "config", "empty"));
            Directory.CreateDirectory(target);
            await File.WriteAllTextAsync(Path.Combine(source, "example.json"), "{\"id\":\"example\",\"mainClass\":\"net.minecraft.client.main.Main\"}");
            await File.WriteAllTextAsync(Path.Combine(source, "config", "options.txt"), "keep");
            var inspection = await MinecraftFolderImportService.InspectAsync(source);
            AssertEqual(MinecraftFolderKind.Version, inspection.Kind);
            var tasks = NewTaskCenter(out var store);
            var service = new MinecraftFolderImportService(tasks);
            var result = await service.ImportAsync(new(source, target));
            AssertTrue(result.IsSuccess, result.Error?.Message ?? "copy");
            string installed = Path.Combine(target, "versions", "example");
            AssertEqual("keep", await File.ReadAllTextAsync(Path.Combine(installed, "config", "options.txt")));
            AssertTrue(Directory.Exists(Path.Combine(installed, "config", "empty")), "empty directories preserved");
            AssertTrue(File.Exists(Path.Combine(source, "example.json")), "source preserved");
            AssertEqual(0, Directory.GetDirectories(target, ".nexa-import-*").Length);
            AssertEqual(TaskCenterEntryState.Finished, store.ReadCollection<TaskCenterEntry>(store.Resolve(TaskCenterStateContract.EntriesKey)).Items.Single().State);
            await File.WriteAllTextAsync(Path.Combine(source, "config", "options.txt"), "changed");
            result = await service.ImportAsync(new(source, target));
            AssertTrue(!result.IsSuccess, "duplicate refused");
            AssertEqual("keep", await File.ReadAllTextAsync(Path.Combine(installed, "config", "options.txt")));
            inspection = await MinecraftFolderImportService.InspectAsync(target);
            AssertEqual(MinecraftFolderKind.GameRoot, inspection.Kind);
            AssertTrue(!(await service.ImportAsync(new(source, source))).IsSuccess, "recursive copy refused");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await service.ImportAsync(new(source, target), cancelled.Token); throw new InvalidOperationException("Cancellation ignored."); }
            catch (OperationCanceledException) { }
            AssertEqual(0, Directory.GetDirectories(target, ".nexa-import-*").Length);
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static async ValueTask FolderImportRejectsMalformedManifests()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "nexa-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string manifest = Path.Combine(temporary, Path.GetFileName(temporary) + ".json");
            await File.WriteAllTextAsync(manifest, "{\"id\":\"not-a-version\"}");
            AssertEqual(MinecraftFolderKind.Unsupported, (await MinecraftFolderImportService.InspectAsync(temporary)).Kind);
            Directory.CreateDirectory(Path.Combine(temporary, ".minecraft", "versions"));
            AssertEqual(Path.Combine(temporary, ".minecraft"), (await MinecraftFolderImportService.InspectAsync(temporary)).Path);
        }
        finally { Directory.Delete(temporary, true); }
    }
}
