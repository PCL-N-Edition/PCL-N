using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.Process;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask RecoveryRestoreCommandHonorsSelectionAndRejectsStalePreview()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(root, "versions", "test");
            Directory.CreateDirectory(Path.Combine(instance, "mods"));
            Directory.CreateDirectory(Path.Combine(instance, "saves"));
            string manifest = Path.Combine(instance, "test.json");
            File.WriteAllText(manifest, """{"id":"test"}""");
            string first = Path.Combine(instance, "mods", "a.jar"), second = Path.Combine(instance, "mods", "b.jar");
            File.WriteAllText(first, "a"); File.WriteAllText(second, "b");
            string world = Path.Combine(instance, "saves", "world.dat"); File.WriteAllText(world, "world");
            var (_, settings) = PolicyFixture();
            var builder = new XsrStateStoreBuilder(); MinecraftProcessStateComposition.DeclareState(builder);
            var state = builder.Build();
            var service = new InstanceRecoveryService(settings, state);
            await RecoveryCapturePlan.CaptureAsync(root, instance, instance, manifest, settings.CaptureRecoverySettings(instance));
            File.WriteAllText(first, "changed-a"); File.WriteAllText(second, "changed-b");
            var report = (await service.ReadAsync(new(instance))).Value!;
            var one = report.Changes.Single(item => item.Path == "mods/a.jar");
            var command = new InstanceRecoveryRestoreCommand(instance, report.BaselineRevision!.Value, report.Fingerprint, [one]);
            AssertFalse((await service.RestoreAsync(command with { Changes = [one with { Area = "root" }] })).IsSuccess);
            AssertFalse((await service.RestoreAsync(command with { Changes = [one, one] })).IsSuccess);
            var sessions = state.Resolve(MinecraftProcessStateComposition.SessionsKey);
            var running = new MinecraftProcessSnapshot(Guid.NewGuid(), "test", 123, MinecraftProcessState.Running, null, DateTimeOffset.UtcNow, null)
            { InstanceDirectory = instance, GameDirectory = instance };
            state.PublishDelta(sessions, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(0, [running], []));
            AssertFalse((await service.RestoreAsync(command)).IsSuccess);
            state.PublishDelta(sessions, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(1, [], [running.SessionId]));
            var result = await service.RestoreAsync(command);
            AssertTrue(result.IsSuccess, result.Error?.Message ?? "Restore failed");
            AssertEqual("a", File.ReadAllText(first)); AssertEqual("changed-b", File.ReadAllText(second));
            AssertEqual("world", File.ReadAllText(world));
            AssertFalse((await service.RestoreAsync(command)).IsSuccess);
            report = (await service.ReadAsync(new(instance))).Value!;
            AssertTrue((await service.RestoreAsync(new(instance, report.BaselineRevision!.Value, report.Fingerprint, report.Changes))).IsSuccess);
            AssertEqual("b", File.ReadAllText(second));
        }
        finally { Directory.Delete(root, true); }
    }
}
