using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask RecoveryPlanIncludesDependenciesAndProtectsUserWorlds()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(root, "versions", "fabric");
            string parent = Path.Combine(root, "versions", "renamed-base");
            Directory.CreateDirectory(instance); Directory.CreateDirectory(parent);
            string manifest = Path.Combine(instance, "fabric.json");
            File.WriteAllText(manifest, """{"id":"fabric","inheritsFrom":"1.20.1"}""");
            File.WriteAllText(Path.Combine(parent, "base.json"), """{"id":"1.20.1"}""");
            File.WriteAllText(Path.Combine(parent, "renamed-base.jar"), "client");
            foreach (string name in new[] { "mods/a.jar", "mods/b.jar.disabled", "config/nested/a.toml", "resourcepacks/a.zip", "shaderpacks/a.zip", "options.txt", "Nexa/InstanceMetadata.json", "saves/world/level.dat", "screenshots/image.png", "logs/latest.log", "Nexa/Recovery/old.txt" })
            {
                string path = Path.Combine(instance, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, name);
            }
            var plan = await RecoveryCapturePlan.BuildAsync(root, instance, instance, manifest);
            AssertEqual(10, plan.Count);
            AssertTrue(plan.Any(item => item.Area == "root" && item.RelativePath.Replace('\\', '/') == "versions/renamed-base/base.json"));
            AssertTrue(plan.Any(item => item.Area == "root" && item.RelativePath.Replace('\\', '/') == "versions/renamed-base/renamed-base.jar"));
            AssertFalse(plan.Any(item => item.RelativePath.Contains("level.dat") || item.RelativePath.Contains("image.png") || item.RelativePath.Contains("latest.log") || item.RelativePath.Contains("old.txt")));
            var store = new RecoverySnapshotStore(instance, instance);
            await RecoveryCapturePlan.CaptureAsync(root, instance, instance, manifest, "{}");
            AssertEqual(plan.Count, (await store.ReadAsync())!.Files.Count);
            foreach (string invalid in new[] { "options.txt", "versions/base/saves/level.dat", "versions/base/base.exe" })
            {
                try { await store.CaptureAsync([new("root", invalid)], "{}"); throw new InvalidOperationException("Unrestricted root source accepted."); }
                catch (InvalidDataException) { }
            }
            File.WriteAllText(Path.Combine(parent, "base.json"), """{"id":"1.20.1","inheritsFrom":"fabric"}""");
            try { await RecoveryCapturePlan.BuildAsync(root, instance, instance, manifest); throw new InvalidOperationException("Cyclic inheritance accepted."); }
            catch (InvalidDataException) { }
            File.WriteAllText(manifest, """{"id":"fabric","inheritsFrom":"missing"}""");
            try { await RecoveryCapturePlan.BuildAsync(root, instance, instance, manifest); throw new InvalidOperationException("Missing inheritance accepted."); }
            catch (FileNotFoundException) { }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask RecoveryPlanUsesSharedGameDirectoryAndBoundsManifestReads()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(root, "versions", "base"); Directory.CreateDirectory(instance);
            string manifest = Path.Combine(instance, "base.json"); File.WriteAllText(manifest, """{"id":"base"}""");
            File.WriteAllText(Path.Combine(instance, "base.jar"), "jar");
            Directory.CreateDirectory(Path.Combine(root, "mods")); File.WriteAllText(Path.Combine(root, "mods", "shared.jar"), "mod");
            Directory.CreateDirectory(Path.Combine(instance, "mods")); File.WriteAllText(Path.Combine(instance, "mods", "unused.jar"), "unused");
            var plan = await RecoveryCapturePlan.BuildAsync(root, instance, root, manifest);
            AssertEqual(3, plan.Count);
            AssertTrue(plan.Any(item => item.Area == "game" && item.RelativePath.EndsWith("shared.jar", StringComparison.Ordinal)));
            AssertFalse(plan.Any(item => item.RelativePath.EndsWith("unused.jar", StringComparison.Ordinal)));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await RecoveryCapturePlan.BuildAsync(root, instance, root, manifest, cancelled.Token); throw new InvalidOperationException("Cancelled plan accepted."); }
            catch (OperationCanceledException) { }
            using (var oversized = File.OpenWrite(manifest)) oversized.SetLength(4 * 1024 * 1024 + 1);
            try { await RecoveryCapturePlan.BuildAsync(root, instance, root, manifest); throw new InvalidOperationException("Oversized manifest accepted."); }
            catch (InvalidDataException) { }
        }
        finally { Directory.Delete(root, true); }
    }
}
