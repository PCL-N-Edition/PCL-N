using System.Text.Json.Nodes;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.Process;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask ModTogglePreservesFilesAndGuardsIdentity()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = CreateVersionDirectory(root, "test", new JsonObject
            {
                ["id"] = "test",
                ["_minecraftVersion"] = "1.20.1",
                ["libraries"] = new JsonArray(new JsonObject { ["name"] = "net.fabricmc:fabric-loader:0.16.0" }),
            });
            string mods = Path.Combine(instance, "mods"); Directory.CreateDirectory(mods);
            string source = Path.Combine(mods, "example.jar"); File.WriteAllText(source, "user data");
            XsrStateStoreBuilder builder = new(); MinecraftProcessStateComposition.DeclareState(builder);
            var store = builder.Build();
            InstanceModEnabledCommand Command(string name, bool enabled)
            {
                var file = new FileInfo(Path.Combine(mods, name));
                return new(instance, name, enabled, file.Length, file.LastWriteTimeUtc.Ticks);
            }
            var original = Command("example.jar", false);
            AssertTrue((await InstanceContentService.SetModEnabledAsync(original, store)).IsSuccess);
            AssertFalse(File.Exists(source));
            AssertEqual("user data", File.ReadAllText(source + ".disabled"));
            AssertTrue((await InstanceContentService.SetModEnabledAsync(Command("example.jar.disabled", true), store)).IsSuccess);
            File.WriteAllText(source + ".disabled", "keep target");
            AssertFalse((await InstanceContentService.SetModEnabledAsync(Command("example.jar", false), store)).IsSuccess);
            AssertEqual("keep target", File.ReadAllText(source + ".disabled"));
            File.Delete(source + ".disabled");
            var stale = Command("example.jar", false);
            File.AppendAllText(source, " changed");
            AssertFalse((await InstanceContentService.SetModEnabledAsync(stale, store)).IsSuccess);
            AssertFalse((await InstanceContentService.SetModEnabledAsync(stale with { Name = "../example.jar" }, store)).IsSuccess);
            AssertFalse((await InstanceContentService.SetModEnabledAsync(stale with { Name = "config.txt" }, store)).IsSuccess);
            var sessions = store.Resolve(MinecraftProcessStateComposition.SessionsKey);
            var running = new MinecraftProcessSnapshot(Guid.NewGuid(), "test", 123, MinecraftProcessState.Running, null, DateTimeOffset.UtcNow, null)
            { InstanceDirectory = Path.Combine(root, "other-root", "versions", "test"), GameDirectory = Path.Combine(root, "other-root") };
            store.PublishDelta(sessions, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(0, [running], []));
            AssertTrue((await InstanceContentService.SetModEnabledAsync(Command("example.jar", false), store)).IsSuccess);
            long revision = store.ReadCollection<MinecraftProcessSnapshot>(sessions).Revision;
            store.PublishDelta(sessions, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(revision, [running with { InstanceDirectory = instance }], []));
            AssertFalse((await InstanceContentService.SetModEnabledAsync(Command("example.jar.disabled", true), store)).IsSuccess);
            revision = store.ReadCollection<MinecraftProcessSnapshot>(sessions).Revision;
            store.PublishDelta(sessions, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(revision, [running with { GameDirectory = instance }], []));
            AssertFalse((await InstanceContentService.SetModEnabledAsync(Command("example.jar.disabled", true), store)).IsSuccess);
            revision = store.ReadCollection<MinecraftProcessSnapshot>(sessions).Revision;
            store.PublishDelta(sessions, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(revision, [running with { State = MinecraftProcessState.Exited }], []));
            using var stop = new CancellationTokenSource(); stop.Cancel();
            AssertFalse((await InstanceContentService.SetModEnabledAsync(Command("example.jar.disabled", true), store, stop.Token)).IsSuccess);
            AssertTrue(File.Exists(source + ".disabled"));
            AssertTrue((await InstanceContentService.SetModEnabledAsync(Command("example.jar.disabled", true), store)).IsSuccess);
        }
        finally { Directory.Delete(root, true); }
    }
}
