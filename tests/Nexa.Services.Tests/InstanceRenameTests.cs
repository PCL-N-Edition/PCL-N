using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Settings;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask InstanceRenamePreservesDataSettingsAndDependencies()
    {
        string root = CreateTempDirectory();
        try
        {
            var (_, settings) = PolicyFixture();
            using var fixture = new InstallFixture(new() { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson() }, settingsPolicy: settings);
            AssertTrue((await fixture.Install.InstallAsync(new(root, "1.20.1"))).IsSuccess);
            string source = Path.Combine(root, "versions", "1.20.1"), target = Path.Combine(root, "versions", "My Game");
            Directory.CreateDirectory(Path.Combine(source, "saves", "world"));
            File.WriteAllText(Path.Combine(source, "saves", "world", "level.dat"), "world");
            AssertTrue(settings.Set(new("game.jvm", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "-XX:+UseG1GC"), source)).IsSuccess);
            string dependent = Path.Combine(root, "versions", "dependent"); Directory.CreateDirectory(dependent);
            File.WriteAllText(Path.Combine(dependent, "dependent.json"), """{"id":"dependent","inheritsFrom":"1.20.1","jar":"1.20.1"}""");
            var original = await MinecraftInstallEditService.ReadAsync(new(root, "1.20.1"));
            var result = await fixture.Install.InstallAsync(new(root, "1.20.1", InstanceName: "1.20.1", EditFingerprint: original.Fingerprint) { NewInstanceName = "My Game" });
            AssertTrue(result.IsSuccess);
            AssertEqual("My Game", result.Value!.InstanceId);
            AssertFalse(Directory.Exists(source));
            AssertEqual("world", File.ReadAllText(Path.Combine(target, "saves", "world", "level.dat")));
            AssertTrue(File.Exists(Path.Combine(target, "My Game.jar")));
            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(dependent, "dependent.json")))!;
            AssertEqual("My Game", json["inheritsFrom"]!.GetValue<string>());
            AssertEqual("My Game", json["jar"]!.GetValue<string>());
            AssertEqual("-XX:+UseG1GC", Effective(settings, "game.jvm", target).Value.Value!);
            AssertEqual(SettingsLayer.Builtin, Effective(settings, "game.jvm", source).Source);
            var renamed = await MinecraftInstallEditService.ReadAsync(new(root, "My Game"));
            AssertEqual("1.20.1", renamed.GameVersion);
            // A case-only rename also updates the actual manifest/JAR filenames.
            await MinecraftInstanceRenamer.RenameAsync(root, "My Game", "my game", renamed.GameVersion, renamed.Fingerprint, settings, fixture.Store, default);
            AssertTrue(File.Exists(Path.Combine(root, "versions", "my game", "my game.json")));
            AssertTrue(File.Exists(Path.Combine(root, "versions", "my game", "my game.jar")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask InstanceRenameRejectsActiveGamesAndRollsBackSettingsFailure()
    {
        string root = CreateTempDirectory();
        try
        {
            var port = new PolicyFailingPort();
            var (_, settings) = PolicyFixture(port);
            using var fixture = new InstallFixture(new() { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson() }, settingsPolicy: settings);
            AssertTrue((await fixture.Install.InstallAsync(new(root, "1.20.1"))).IsSuccess);
            string source = Path.Combine(root, "versions", "1.20.1"), target = Path.Combine(root, "versions", "renamed");
            AssertTrue(settings.Set(new("game.width", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "1440"), source)).IsSuccess);
            var original = await MinecraftInstallEditService.ReadAsync(new(root, "1.20.1"));
            Task Rename(string fingerprint) => MinecraftInstanceRenamer.RenameAsync(root, "1.20.1", "renamed", "1.20.1", fingerprint, settings, fixture.Store, default);
            try { await Rename("stale"); throw new InvalidOperationException("Stale edit accepted."); } catch (IOException) { }
            Directory.CreateDirectory(target);
            try { await Rename(original.Fingerprint); throw new InvalidOperationException("Collision accepted."); } catch (IOException) { }
            Directory.Delete(target);
            var state = fixture.Store.Resolve(MinecraftProcessStateComposition.SessionsKey);
            var process = new MinecraftProcessSnapshot(Guid.NewGuid(), "1.20.1", 1, MinecraftProcessState.Running, null, DateTimeOffset.UtcNow, null) { InstanceDirectory = source };
            fixture.Store.PublishDelta(state, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(0, [process], []));
            bool blocked = false;
            try { await Rename(original.Fingerprint); } catch (InvalidOperationException) { blocked = true; }
            AssertTrue(blocked);
            AssertFalse((await fixture.Install.InstallAsync(new(root, "1.20.1", InstanceName: "1.20.1", EditFingerprint: original.Fingerprint)
            { NewInstanceName = "renamed" })).IsSuccess);
            AssertEqual(original.Fingerprint, (await MinecraftInstallEditService.ReadAsync(new(root, "1.20.1"))).Fingerprint);
            fixture.Store.PublishDelta(state, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(fixture.Store.ReadCollection<MinecraftProcessSnapshot>(state).Revision, [], [process.SessionId]));
            port.Fail = true;
            try { await Rename(original.Fingerprint); throw new InvalidOperationException("Settings failure accepted."); } catch (IOException) { }
            AssertTrue(File.Exists(Path.Combine(source, "1.20.1.jar")));
            AssertEqual(original.Fingerprint, (await MinecraftInstallEditService.ReadAsync(new(root, "1.20.1"))).Fingerprint);
            AssertFalse(Directory.Exists(target));
            AssertEqual("1440", Effective(settings, "game.width", source).Value.Value!);
        }
        finally { Directory.Delete(root, true); }
    }
}
