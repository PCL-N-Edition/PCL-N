using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.Process;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask RecoveryRecognizesLauncherModTogglesAndRestoresBothPaths()
    {
        foreach (bool isolated in new[] { true, false })
        {
            string root = CreateTempDirectory();
            try
            {
                string instance = Path.Combine(root, "versions", "test");
                string game = isolated ? instance : root;
                Directory.CreateDirectory(instance);
                Directory.CreateDirectory(Path.Combine(game, "mods"));
                string manifest = Path.Combine(instance, "test.json");
                File.WriteAllText(manifest, """{"id":"test","_minecraftVersion":"1.20.1","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.0"}]}""");
                if (!isolated)
                {
                    var metadata = new Nexa.Services.Minecraft.MinecraftInstanceMetadataStore();
                    var current = await metadata.LoadAsync(instance);
                    await metadata.SaveAsync(instance, current with { InstanceIsolation = false });
                }
                string active = Path.Combine(game, "mods", "a.jar"), disabled = active + ".disabled";
                File.WriteAllText(active, "same mod bytes");
                var (_, settings) = PolicyFixture();
                var builder = new XsrStateStoreBuilder(); MinecraftProcessStateComposition.DeclareState(builder);
                var state = builder.Build();
                var service = new InstanceRecoveryService(settings, state);
                await RecoveryCapturePlan.CaptureAsync(root, instance, game, manifest, settings.CaptureRecoverySettings(instance));
                async Task Toggle(string path, bool enabled)
                {
                    var info = new FileInfo(path);
                    var result = await InstanceContentService.SetModEnabledAsync(new(instance, info.Name, enabled, info.Length, info.LastWriteTimeUtc.Ticks), state);
                    AssertTrue(result.IsSuccess, result.Error?.Message ?? "Toggle failed");
                }
                await Toggle(active, false);
                var report = (await service.ReadAsync(new(instance))).Value!;
                var change = report.Changes.Single();
                AssertEqual(InstanceRecoveryChangeKind.Disabled, change.Kind);
                AssertEqual("mods/a.jar", change.Path);
                AssertEqual("mods/a.jar.disabled", change.RelatedPath!);
                AssertFalse((await service.RestoreAsync(new(instance, report.BaselineRevision!.Value, report.Fingerprint,
                    [change with { RelatedPath = "mods/unrelated.jar" }]))).IsSuccess);
                AssertTrue((await service.RestoreAsync(new(instance, report.BaselineRevision!.Value, report.Fingerprint, [change]))).IsSuccess);
                AssertTrue(File.Exists(active)); AssertFalse(File.Exists(disabled));
                AssertEqual(0, (await service.ReadAsync(new(instance))).Value!.Changes.Count);
                await Toggle(active, false);
                await RecoveryCapturePlan.CaptureAsync(root, instance, game, manifest, settings.CaptureRecoverySettings(instance));
                await Toggle(disabled, true);
                report = (await service.ReadAsync(new(instance))).Value!;
                AssertEqual(InstanceRecoveryChangeKind.Enabled, report.Changes.Single().Kind);
                AssertTrue((await service.RestoreAsync(new(instance, report.BaselineRevision!.Value, report.Fingerprint, report.Changes))).IsSuccess);
                AssertFalse(File.Exists(active)); AssertTrue(File.Exists(disabled));
                File.Move(disabled, active);
                File.WriteAllText(active, "different mod bytes");
                report = (await service.ReadAsync(new(instance))).Value!;
                AssertEqual(2, report.Changes.Count);
                AssertTrue(report.Changes.All(item => item.Kind is InstanceRecoveryChangeKind.Added or InstanceRecoveryChangeKind.Removed));
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
