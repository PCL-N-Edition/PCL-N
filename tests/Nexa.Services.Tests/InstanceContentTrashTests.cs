using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.Process;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask ContentRemovalIsRecoverableAndRejectsConflicts()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = CreateVersionDirectory(root, "fixture", new JsonObject { ["id"] = "fixture", ["mainClass"] = "example.Main", ["_minecraftVersion"] = "1.20.1" });
            var builder = new XsrStateStoreBuilder(); MinecraftProcessStateComposition.DeclareState(builder);
            var store = builder.Build();
            string packs = Path.Combine(instance, "resourcepacks"); Directory.CreateDirectory(packs);
            string file = Path.Combine(packs, "sample.zip"); await File.WriteAllTextAsync(file, "original content");
            var info = new FileInfo(file);
            var command = new InstanceContentRemoveCommand(instance, "resourcepacks", info.Name, false, info.Length, info.LastWriteTimeUtc.Ticks);
            AssertFalse((await InstanceContentTrash.RemoveAsync(command with { ExpectedSize = 1 }, store)).IsSuccess);
            AssertTrue(File.Exists(file));
            AssertFalse((await InstanceContentTrash.RemoveAsync(command with { Name = "../fixture.json" }, store)).IsSuccess);
            var sessions = store.Resolve(MinecraftProcessStateComposition.SessionsKey);
            var running = new MinecraftProcessSnapshot(Guid.NewGuid(), "other", 123, MinecraftProcessState.Running, null, DateTimeOffset.UtcNow, null)
            { InstanceDirectory = Path.Combine(root, "versions", "other"), GameDirectory = instance };
            store.PublishDelta(sessions, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(0, [running], []));
            AssertFalse((await InstanceContentTrash.RemoveAsync(command, store)).IsSuccess);
            store.PublishDelta(sessions, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(1, [], [running.SessionId]));
            AssertTrue((await InstanceContentTrash.RemoveAsync(command, store)).IsSuccess);
            AssertFalse(File.Exists(file));
            // Re-read exclusively from disk: no in-memory transaction object is needed to restore.
            var read = await InstanceManagementService.ReadAsync(new(instance) { IncludeTrash = true });
            AssertEqual(1, read.Trash.Count);
            await File.WriteAllTextAsync(file, "replacement");
            var restore = new InstanceContentRestoreCommand(instance, read.Trash[0].Id);
            AssertFalse((await InstanceContentTrash.RestoreAsync(restore, store)).IsSuccess);
            AssertEqual("replacement", await File.ReadAllTextAsync(file));
            File.Delete(file);
            AssertTrue((await InstanceContentTrash.RestoreAsync(restore, store)).IsSuccess);
            AssertEqual("original content", await File.ReadAllTextAsync(file));
            AssertEqual(0, (await InstanceManagementService.ReadAsync(new(instance) { IncludeTrash = true })).Trash.Count);
            string world = Path.Combine(instance, "saves", "world"); Directory.CreateDirectory(world);
            await File.WriteAllTextAsync(Path.Combine(world, "level.dat"), "world data");
            AssertTrue((await InstanceContentTrash.RemoveAsync(new(instance, "saves", "world", true, null, Directory.GetLastWriteTimeUtc(world).Ticks), store)).IsSuccess);
            read = await InstanceManagementService.ReadAsync(new(instance) { IncludeTrash = true });
            AssertTrue((await InstanceContentTrash.RestoreAsync(new(instance, read.Trash.Single().Id), store)).IsSuccess);
            AssertEqual("world data", await File.ReadAllTextAsync(Path.Combine(world, "level.dat")));
        }
        finally { Directory.Delete(root, true); }
    }
}
