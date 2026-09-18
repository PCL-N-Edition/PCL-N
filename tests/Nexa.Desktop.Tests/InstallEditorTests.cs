using Nexa.Services.Minecraft;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void InstallEditorLocksBaseVersionAndResetsOnExit()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([Instance("playable")]), addProfile: true);
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        var snapshot = (MinecraftLibrarySnapshot)fixture.Store.ReadAppliedValue(fixture.Store.Resolve(MinecraftLibraryService.StateKey))!;
        string directory = Path.Combine(snapshot.RootDirectory, "versions", "playable");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "playable.json"), """{"id":"playable","clientVersion":"1.20.1","mainClass":"net.minecraft.client.main.Main","_nexaInstall":{"game":"1.20.1","loader":"Fabric","build":"0.16.9","addons":[]}}""");
        var size = new XsrUiSize(850, 500);
        fixture.Shell.Render(size);
        Emit(fixture.Intents, "ui.launch.modify");
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        var scene = fixture.Shell.Render(size);
        AssertEqual("修改版本", FindByKey(fixture.Shell, scene, "TitleSubpage").Text);
        AssertEqual("无需修改", FindByKey(fixture.Shell, scene, "JavaInstallStart").Text);
        AssertFalse(FindByKey(fixture.Shell, scene, "JavaInstallVersionInput").IsEnabled);
        var row = scene.Nodes.Single(node => node.Label == "Minecraft 1.20.1，不可更改");
        AssertFalse(row.IsEnabled);
        Emit(fixture.Intents, "ui.install.catalog.select", row.Entity);
        AssertTrue(fixture.Shell.Render(size).Nodes.Any(node => node.Label == "Minecraft 1.20.1，不可更改"));
        Emit(fixture.Intents, "ui.page.back");
        fixture.Shell.Render(size);
        Emit(fixture.Intents, "ui.install.java");
        scene = fixture.Shell.Render(size);
        AssertEqual("开始安装", FindByKey(fixture.Shell, scene, "JavaInstallStart").Text);
        AssertTrue(FindByKey(fixture.Shell, scene, "JavaInstallVersionInput").IsEnabled);
    }
}
