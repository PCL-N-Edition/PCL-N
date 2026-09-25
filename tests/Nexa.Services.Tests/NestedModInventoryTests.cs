using System.IO.Compression;
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Process;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static byte[] FabricJar(string id, byte[]? nested = null, string? reference = null, bool duplicate = false)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            var metadata = new JsonObject { ["schemaVersion"] = 1, ["id"] = id, ["version"] = "1.0.0" };
            if (reference is not null) metadata["jars"] = new JsonArray(new JsonObject { ["file"] = reference });
            using (var writer = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open())) writer.Write(metadata.ToJsonString());
            if (nested is not null)
            {
                using (var output = archive.CreateEntry("nested/child.jar").Open()) output.Write(nested);
                if (duplicate) { using var second = archive.CreateEntry("nested/child.jar").Open(); second.Write(nested); }
            }
        }
        return buffer.ToArray();
    }

    private static async ValueTask NestedFabricInventoryReadsDeclaredCandidates()
    {
        string root = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "mods"));
            await File.WriteAllBytesAsync(Path.Combine(root, "mods", "bundle.jar.disabled"), FabricJar("parent", FabricJar("child"), "nested/child.jar"));
            var inventory = await LaunchModInventoryReader.ReadAsync(root);
            AssertTrue(inventory.Complete); AssertEqual(0, inventory.UnknownFiles);
            AssertTrue(inventory.Mods.Select(mod => mod.Id).SequenceEqual(["parent", "child"]));
            AssertTrue(inventory.Mods.All(mod => !mod.Enabled));
            AssertFalse(Directory.Exists(Path.Combine(root, "nested")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async ValueTask NestedFabricInventoryRejectsIncompleteAndDeepArchives()
    {
        string root = CreateTempDirectory();
        try
        {
            string mods = Path.Combine(root, "mods"); Directory.CreateDirectory(mods);
            string path = Path.Combine(mods, "bundle.jar");
            byte[] deep = FabricJar("leaf");
            for (int depth = 0; depth < 6; depth++) deep = FabricJar("level" + depth, deep, "nested/child.jar");
            foreach (var bytes in new[]
            {
                FabricJar("parent", reference: "missing.jar"),
                FabricJar("parent", FabricJar("child"), "../outside.jar"),
                FabricJar("parent", [1, 2, 3], "nested/child.jar"),
                FabricJar("parent", FabricJar("child"), "nested/child.jar", duplicate: true),
                deep
            })
            {
                await File.WriteAllBytesAsync(path, bytes);
                var inventory = await LaunchModInventoryReader.ReadAsync(root);
                AssertFalse(inventory.Complete);
                AssertTrue(inventory.Mods.Count is > 0 and <= 5);
                AssertFalse(inventory.Mods.Any(mod => mod.Id == "leaf"));
            }
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            bool observed = false;
            try { await LaunchModInventoryReader.ReadAsync(root, canceled.Token); }
            catch (OperationCanceledException) { observed = true; }
            AssertTrue(observed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
