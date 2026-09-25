using Nexa.Services.Minecraft.Install;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask LoaderInstallerCachePinsIdentityAndRejectsChanges()
    {
        string root = CreateTempDirectory();
        try
        {
            var request = new MinecraftLoaderInstallRequest(root, "1.20.1", "test", InstallLoader.Forge, "47.4.20", new());
            string source = Path.Combine(root, "source.jar"), copy = Path.Combine(root, "copy.jar");
            await File.WriteAllTextAsync(source, "verified installer");
            using (var cache = new LoaderInstallerCache(root, request))
            {
                AssertFalse(await cache.RestoreAsync(copy, default));
                await cache.SaveAsync(source, default);
            }
            File.Delete(source);
            using (var cache = new LoaderInstallerCache(root, request)) AssertTrue(await cache.RestoreAsync(copy, default));
            AssertEqual("verified installer", await File.ReadAllTextAsync(copy));
            using (var other = new LoaderInstallerCache(root, request with { Build = "47.4.21" })) AssertFalse(await other.RestoreAsync(copy, default));
            string cached = Directory.GetFiles(Path.Combine(root, ".task", "loader"), "installer.jar", SearchOption.AllDirectories).Single();
            await File.WriteAllTextAsync(cached, "changed installer");
            File.Delete(copy);
            using var corrupt = new LoaderInstallerCache(root, request);
            try { await corrupt.RestoreAsync(copy, default); throw new InvalidOperationException("Changed installer accepted."); }
            catch (InvalidDataException) { }
            AssertFalse(File.Exists(copy));
        }
        finally { Directory.Delete(root, true); }
    }
}
