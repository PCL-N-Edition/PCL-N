using Nexa.Services.Minecraft.Management;
using Nexa.Services.Settings;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask RecoveryDiffReportsFilesAndPrivateSettingsWithoutChangingBaseline()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(root, "versions", "test");
            Directory.CreateDirectory(Path.Combine(instance, "mods"));
            Directory.CreateDirectory(Path.Combine(instance, "config"));
            string manifest = Path.Combine(instance, "test.json");
            File.WriteAllText(manifest, """{"id":"test"}""");
            File.WriteAllText(Path.Combine(instance, "test.jar"), "client");
            File.WriteAllText(Path.Combine(instance, "mods", "old.jar"), "old");
            File.WriteAllText(Path.Combine(instance, "config", "game.toml"), "old");
            var (_, settings) = PolicyFixture();
            var service = new InstanceRecoveryService(settings, new XsrStateStoreBuilder().Build());
            var absent = await service.ReadAsync(new(instance));
            AssertTrue(absent.IsSuccess);
            AssertTrue(absent.Value!.BaselineRevision is null);
            AssertFalse(Directory.Exists(Path.Combine(instance, "Nexa", "Recovery")));
            var baseline = await RecoveryCapturePlan.CaptureAsync(root, instance, instance, manifest, settings.CaptureRecoverySettings(instance));
            var unchanged = await service.ReadAsync(new(instance));
            AssertTrue(unchanged.IsSuccess); AssertEqual(0, unchanged.Value!.Changes.Count);
            string objects = Path.Combine(instance, "Nexa", "Recovery", "objects");
            int objectCount = Directory.GetFiles(objects, "*.br").Length;
            File.Delete(Path.Combine(instance, "mods", "old.jar"));
            File.WriteAllText(Path.Combine(instance, "mods", "new.jar"), "new");
            File.WriteAllText(Path.Combine(instance, "config", "game.toml"), "new");
            File.WriteAllText(manifest, "{broken");
            AssertTrue(settings.Set(new("game.jvm", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "-Dprivate=secret"), instance)).IsSuccess);
            var changed = await service.ReadAsync(new(instance));
            AssertTrue(changed.IsSuccess);
            var report = changed.Value!;
            AssertEqual(5, report.Changes.Count);
            AssertTrue(report.Changes.Any(item => item.Kind == InstanceRecoveryChangeKind.Removed && item.Path == "mods/old.jar"));
            AssertTrue(report.Changes.Any(item => item.Kind == InstanceRecoveryChangeKind.Added && item.Path == "mods/new.jar"));
            AssertTrue(report.Changes.Any(item => item.Kind == InstanceRecoveryChangeKind.Modified && item.Path == "test.json"));
            AssertTrue(report.Changes.Any(item => item.Category == "启动设置" && item.SettingKey == "game.jvm"));
            AssertFalse(report.Changes.Any(item => item.Path.Contains("secret", StringComparison.Ordinal)));
            AssertFalse(unchanged.Value.Fingerprint == report.Fingerprint);
            AssertEqual(report.Fingerprint, (await service.ReadAsync(new(instance))).Value!.Fingerprint);
            AssertEqual(objectCount, Directory.GetFiles(objects, "*.br").Length);
            AssertEqual(baseline.Revision, (await new RecoverySnapshotStore(instance, instance).ReadAsync())!.Revision);
            File.Delete(manifest);
            AssertTrue((await service.ReadAsync(new(instance))).Value!.Changes.Any(item => item.Path == "test.json" && item.Kind == InstanceRecoveryChangeKind.Removed));
            string other = Path.Combine(root, "other-root", "versions", "test");
            AssertTrue((await service.ReadAsync(new(other))).Value!.BaselineRevision is null);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask RecoveryDiffHandlesSharedScopeAndNeverClaimsOtherVersionFiles()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(root, "versions", "test"); Directory.CreateDirectory(instance);
            string manifest = Path.Combine(instance, "test.json"); File.WriteAllText(manifest, """{"id":"test"}""");
            Directory.CreateDirectory(Path.Combine(root, "mods")); File.WriteAllText(Path.Combine(root, "mods", "a.jar"), "a");
            var (_, settings) = PolicyFixture();
            await RecoveryCapturePlan.CaptureAsync(root, instance, root, manifest, settings.CaptureRecoverySettings(instance));
            var service = new InstanceRecoveryService(settings, new XsrStateStoreBuilder().Build());
            string unrelated = Path.Combine(root, "versions", "other"); Directory.CreateDirectory(unrelated);
            File.WriteAllText(Path.Combine(unrelated, "other.json"), """{"id":"other"}""");
            AssertEqual(0, (await service.ReadAsync(new(instance))).Value!.Changes.Count);
            File.WriteAllText(Path.Combine(root, "mods", "a.jar"), "b");
            var changed = (await service.ReadAsync(new(instance))).Value!;
            AssertEqual(1, changed.Changes.Count);
            AssertEqual("mods/a.jar", changed.Changes[0].Path);
            AssertEqual("模组", changed.Changes[0].Category);
            using var operation = await InstanceRecoveryOperationGate.EnterOperationAsync(root);
            AssertFalse((await service.ReadAsync(new(instance))).IsSuccess);
        }
        finally { Directory.Delete(root, true); }
    }
}
