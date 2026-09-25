using Nexa.Desktop.Ui;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void InstanceManagementShowsContentAndBoundsRealizedRows()
    {
        string root = Path.Combine(Path.GetTempPath(), "nexa-management-ui-" + Guid.NewGuid().ToString("N"));
        string instance = Path.Combine(root, "versions", "fabric");
        Directory.CreateDirectory(instance);
        File.WriteAllText(Path.Combine(instance, "fabric.json"), """{"id":"fabric","_minecraftVersion":"1.20.1","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.0"}]}""");
        string mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(mods, "example.jar"), "fixture");
        string resourcepacks = Path.Combine(instance, "resourcepacks");
        Directory.CreateDirectory(resourcepacks);
        for (int i = 0; i < 220; i++) File.WriteAllText(Path.Combine(resourcepacks, $"pack-{i:D3}.zip"), "fixture");
        string vanilla = Path.Combine(root, "versions", "vanilla");
        Directory.CreateDirectory(vanilla);
        File.WriteAllText(Path.Combine(vanilla, "vanilla.json"), """{"id":"vanilla","_minecraftVersion":"1.20.1"}""");
        try
        {
            using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
            using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries, fixture.Foundation.Commands, fixture.Store, fixture.Feedback, () => instance);
            string? openedDirectory = null;
            settings.OpenManagementDirectory = value => openedDirectory = value;
            fixture.Shell.Renderer.ReducedMotion = true;
            fixture.Shell.Stage.Navigation.Replace(settings.Page);
            var scene = fixture.Shell.Render(new(1000, 650));
            AssertTrue(SpinWait.SpinUntil(() =>
            {
                scene = fixture.Shell.Render(new(1000, 650));
                return scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "SettingsNav.mods");
            }, TimeSpan.FromSeconds(10)));
            AssertEqual("overview", settings.SelectedSection);
            AssertFalse(scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "SettingsNav.shaderpacks"));
            Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.mods").Entity);
            scene = fixture.Shell.Render(new(1000, 650));
            Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "ManagementModToggle.example.jar").Entity);
            bool toggled = SpinWait.SpinUntil(() =>
            {
                scene = fixture.Shell.Render(new(1000, 650));
                return scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "ManagementModToggle.example.jar.disabled");
            }, TimeSpan.FromSeconds(10));
            if (!toggled) throw new InvalidOperationException($"Toggle did not refresh: file={File.Exists(Path.Combine(mods, "example.jar.disabled"))}; page={settings.SelectedSection}; errors={string.Join(";", fixture.Feedback.Snapshot().Notifications.Select(item => item.Message))}");
            AssertTrue(File.Exists(Path.Combine(mods, "example.jar.disabled")));
            AssertFalse(File.Exists(Path.Combine(mods, "example.jar")));
            Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.resourcepacks").Entity);
            scene = fixture.Shell.Render(new(1000, 650));
            scene = fixture.Shell.Render(new(1000, 650));
            AssertEqual("resourcepacks", settings.SelectedSection);
            AssertTrue(scene.Nodes.Any(node => node.Text == "pack-000.zip"));
            Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "Management.打开文件夹").Entity);
            scene = fixture.Shell.Render(new(1000, 650));
            AssertEqual(resourcepacks, openedDirectory!);
            var list = FindByKey(fixture.Shell, scene, "ManagementContentList");
            AssertTrue(fixture.Shell.Tree.Children(list.Entity).Count < 40);
            var body = fixture.Shell.Tree.Parent(list.Entity);
            fixture.Shell.Tree.GetComponent<XsrUiScroll>(body)!.OffsetY = 100000;
            scene = fixture.Shell.Render(new(1000, 650));
            scene = fixture.Shell.Render(new(1000, 650));
            AssertTrue(fixture.Shell.Tree.Children(list.Entity).Count < 40);
            AssertTrue(scene.Nodes.Any(node => node.Text == "pack-219.zip"));
            instance = vanilla;
            AssertTrue(SpinWait.SpinUntil(() =>
            {
                scene = fixture.Shell.Render(new(1000, 650));
                return scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "SettingsNav.components")
                    && !scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "SettingsNav.mods");
            }, TimeSpan.FromSeconds(10)));
            AssertEqual("overview", settings.SelectedSection);
            AssertFalse(scene.Nodes.Any(node => node.Text?.StartsWith("pack-", StringComparison.Ordinal) == true));
        }
        finally { Directory.Delete(root, true); }
    }
}

