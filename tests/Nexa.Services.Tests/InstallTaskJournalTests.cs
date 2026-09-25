using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Install;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask InstallTaskTerminalStatesDoNotResurrect()
    {
        string root = CreateTempDirectory();
        try
        {
            string stage = await MetadataTaskStage(root);
            var plan = await InstallTaskJournal.ReadAsync(root, stage, default);
            AssertEqual(InstallTaskStatus.Pending, await InstallTaskJournal.ReadStatusAsync(stage, plan, default));
            await InstallTaskJournal.WriteStatusAsync(stage, plan, InstallTaskStatus.RollbackRequested, default);
            try { await InstallTaskJournal.WriteStatusAsync(stage, plan, InstallTaskStatus.Completed, default); throw new InvalidOperationException("Rollback changed to completion."); } catch (InvalidDataException) { }
            await InstallTaskJournal.WriteStatusAsync(stage, plan, InstallTaskStatus.RolledBack, default);
            await InstallTaskJournal.WriteStatusAsync(stage, plan, InstallTaskStatus.RolledBack, default);
            try { await InstallTaskJournal.WriteStatusAsync(stage, plan, InstallTaskStatus.Pending, default); throw new InvalidOperationException("Terminal task resurrected."); } catch (InvalidDataException) { }
            string path = Path.Combine(stage, InstallTaskJournal.DirectoryName, "status.json");
            var document = JsonNode.Parse(File.ReadAllText(path))!; document["Id"] = Guid.NewGuid(); File.WriteAllText(path, document.ToJsonString());
            try { await InstallTaskJournal.ReadStatusAsync(stage, plan, default); throw new InvalidOperationException("Other task status accepted."); } catch (InvalidDataException) { }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask InstallTaskJournalPreservesChoicesAndRejectsInvalidRecords()
    {
        string root = CreateTempDirectory();
        try
        {
            string stage = Path.Combine(root, ".nexa-modify", Guid.NewGuid().ToString("N"));
            List<MinecraftInstallAddon> addons = [new(InstallLoader.FabricApi, "api-build", [new("Modrinth", "api.jar", new("https://cdn.modrinth.com/api.jar"), new('A', 40), 123)])];
            var command = new MinecraftInstallCommand(root, "1.21.1", InstallLoader.Fabric, "0.16.0", addons, "my-game", new('B', 64))
            { InheritVanilla = false, NewInstanceName = "renamed-game" };
            var saved = await InstallTaskJournal.CreateAsync(stage, command, default);
            addons.Clear();
            AssertEqual(1, saved.Command.Addons!.Count);
            var read = await InstallTaskJournal.ReadAsync(root, stage, default);
            AssertEqual("my-game", read.Command.InstanceName); AssertEqual("renamed-game", read.Command.NewInstanceName);
            AssertEqual("0.16.0", read.Command.LoaderBuild); AssertEqual(false, read.Command.InheritVanilla);
            AssertEqual("api-build", read.Command.Addons!.Single().Version);
            AssertEqual("api.jar", read.Command.Addons!.Single().Downloads!.Single().FileName);
            try { await InstallTaskJournal.CreateAsync(stage, command, default); throw new InvalidOperationException("Immutable plan replaced."); } catch (IOException) { }
            string path = Path.Combine(stage, InstallTaskJournal.DirectoryName, "plan.json"); string original = File.ReadAllText(path);
            foreach (string field in new[] { "RootDirectory", "EditFingerprint", "GameVersion" })
            {
                var document = JsonNode.Parse(original)!;
                document["Command"]![field] = field == "EditFingerprint" ? "bad" : "../other";
                File.WriteAllText(path, document.ToJsonString());
                try { await InstallTaskJournal.ReadAsync(root, stage, default); throw new InvalidOperationException("Invalid plan accepted."); } catch (InvalidDataException) { }
            }
            File.WriteAllText(path, original);
            string outside = Path.Combine(root, "outside", Path.GetFileName(stage));
            try { await InstallTaskJournal.ReadAsync(root, outside, default); throw new InvalidOperationException("Outside stage accepted."); } catch (InvalidDataException) { }
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            string canceledStage = Path.Combine(root, ".nexa-modify", Guid.NewGuid().ToString("N"));
            try { await InstallTaskJournal.CreateAsync(canceledStage, command, canceled.Token); throw new InvalidOperationException("Cancellation ignored."); } catch (OperationCanceledException) { }
            AssertFalse(File.Exists(Path.Combine(canceledStage, InstallTaskJournal.DirectoryName, "plan.json")));
        }
        finally { Directory.Delete(root, true); }
    }
}
