using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Process;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask InstallRecoveryContinuesPreparationAndRejectsConcurrentExecution()
    {
        string root = CreateTempDirectory();
        try
        {
            var metadata = new FakeMetadata { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson() };
            using var fixture = new InstallFixture(metadata, loaderInstaller: new FixtureLoaderInstaller());
            AssertTrue((await fixture.Install.InstallAsync(new(root, "1.20.1"))).IsSuccess);
            var original = await MinecraftInstallEditService.ReadAsync(new(root, "1.20.1"));
            Guid id = Guid.NewGuid(); string stage = Path.Combine(root, ".nexa-modify", id.ToString("N"));
            var command = new MinecraftInstallCommand(root, "1.20.1", InstallLoader.Forge, "47.4.20", [], "1.20.1", original.Fingerprint) { InheritVanilla = false };
            await InstallTaskJournal.CreateAsync(stage, command, default);
            string instance = Path.Combine(root, "versions", "1.20.1");
            Directory.CreateDirectory(Path.Combine(instance, "saves")); File.WriteAllText(Path.Combine(instance, "saves", "world"), "keep");
            using (var lease = new FileStream(Path.Combine(stage, InstallTaskJournal.DirectoryName, "execution.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                try { await fixture.Install.ResumeModificationAsync(root, id); throw new InvalidOperationException("Concurrent execution accepted."); } catch (IOException) { }
            }
            var state = fixture.Store.Resolve(MinecraftProcessStateComposition.SessionsKey);
            var process = new MinecraftProcessSnapshot(Guid.NewGuid(), "1.20.1", 1, MinecraftProcessState.Running, null, DateTimeOffset.UtcNow, null)
            { InstanceDirectory = instance };
            fixture.Store.PublishDelta(state, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(0, [process], []));
            bool blocked = false;
            try { await fixture.Install.ResumeModificationAsync(root, id); } catch (InvalidOperationException) { blocked = true; }
            AssertTrue(blocked);
            AssertEqual(original.Fingerprint, (await MinecraftInstallEditService.ReadAsync(new(root, "1.20.1"))).Fingerprint);
            fixture.Store.PublishDelta(state, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(fixture.Store.ReadCollection<MinecraftProcessSnapshot>(state).Revision, [], [process.SessionId]));
            var result = await fixture.Install.ResumeModificationAsync(root, id);
            AssertEqual("1.20.1", result.InstanceId);
            AssertEqual(InstallLoader.Forge, (await MinecraftInstallEditService.ReadAsync(new(root, result.InstanceId))).Selection[0].Loader);
            AssertEqual("keep", File.ReadAllText(Path.Combine(instance, "saves", "world")));
            AssertTrue(File.Exists(Path.Combine(stage, ".publication", "progress.json")));
            int reads = metadata.VanillaReads;
            await fixture.Install.ResumeModificationAsync(root, id);
            AssertEqual(reads, metadata.VanillaReads);
        }
        finally { Directory.Delete(root, true); }
    }
}
