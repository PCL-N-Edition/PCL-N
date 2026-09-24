using Nexa.Services.Minecraft.Install;
using Nexa.Services.Tasks;

namespace Nexa.Services.Tests;

internal static partial class Program
{
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
