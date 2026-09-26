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
        using (var archive = System.IO.Compression.ZipFile.Open(Path.Combine(mods, "iris.jar"), System.IO.Compression.ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open()))
            writer.Write("""{"id":"iris","name":"Iris","version":"1.8.0"}""");
        Directory.CreateDirectory(Path.Combine(instance, "shaderpacks"));
        File.WriteAllText(Path.Combine(instance, "shaderpacks", "Shader.zip"), "fixture");
        string resourcepacks = Path.Combine(instance, "resourcepacks");
        Directory.CreateDirectory(resourcepacks);
        for (int i = 0; i < 220; i++) File.WriteAllText(Path.Combine(resourcepacks, $"pack-{i:D3}.zip"), "fixture");
        Directory.CreateDirectory(Path.Combine(instance, "screenshots"));
        File.WriteAllBytes(Path.Combine(instance, "screenshots", "screen.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6xN8AAAAASUVORK5CYII="));
        Directory.CreateDirectory(Path.Combine(instance, "saves", "My World"));
        File.Delete(Path.Combine(resourcepacks, "pack-000.zip"));
        using (var archive = System.IO.Compression.ZipFile.Open(Path.Combine(resourcepacks, "00§a§lPack.zip"), System.IO.Compression.ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("pack.mcmeta").Open()))
            writer.Write("""{"pack":{"description":"§aGreen §l§o§nPack\n附加§?说明","pack_format":34}}""");
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
            Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.recovery").Entity);
            AssertTrue(SpinWait.SpinUntil(() =>
            {
                scene = fixture.Shell.Render(new(1000, 650));
                return scene.Nodes.Any(node => node.Text == "版本文件");
            }, TimeSpan.FromSeconds(10)));
            AssertEqual("recovery", settings.SelectedSection);
            AssertTrue(scene.Nodes.Any(node => node.Text == "保留历史快照"));
            AssertTrue(scene.Nodes.Any(node => node.Text == "快照存储"));
            string? StorageSummary()
            {
                var label = scene.Nodes.FirstOrDefault(node => node.Text == "版本文件");
                if (!label.Entity.IsAssigned) return null;
                var row = fixture.Shell.Tree.Parent(label.Entity);
                var value = fixture.Shell.Tree.Children(row)[^1];
                return scene.Nodes.FirstOrDefault(node => node.Entity == value).Text;
            }
            string? previousStorage = StorageSummary();
            File.WriteAllBytes(Path.Combine(instance, "fresh-comparison.bin"), new byte[128 * 1024]);
            Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.overview").Entity);
            scene = fixture.Shell.Render(new(1000, 650));
            Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.recovery").Entity);
            AssertTrue(SpinWait.SpinUntil(() =>
            {
                scene = fixture.Shell.Render(new(1000, 650));
                return StorageSummary() is { } summary && summary != previousStorage;
            }, TimeSpan.FromSeconds(10)));
            string? HistorySetting(string? scope) => fixture.Foundation.Host.SettingsPolicy.Read(new(scope)).Value!.Values.Single(item => item.Key == "recovery.keep-history").Value.Value;
            AssertEqual("false", HistorySetting(instance));
            Emit(fixture.Intents, "ui.settings.choice", FindByKey(fixture.Shell, scene, "SettingsOption.recovery.keep-history.true").Entity);
            scene = fixture.Shell.Render(new(1000, 650));
            AssertTrue(SpinWait.SpinUntil(() => HistorySetting(instance) == "true", TimeSpan.FromSeconds(5)));
            AssertEqual("false", HistorySetting(null));
            scene = fixture.Shell.Render(new(1000, 650));

            AssertFalse(scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) is "SettingsNav.java" or "SettingsNav.components"));
            AssertTrue(scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "SettingsNav.shaderpacks"));
            Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.mods").Entity);
            scene = fixture.Shell.Render(new(1000, 650));
            AssertFalse(scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "ManagementModToggle.example.jar"));
            Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "ManagementContentDetails.example.jar").Entity);
            scene = fixture.Shell.Render(new(1000, 650));
            AssertTrue(scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "ManagementContentDetail"));
            Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "ManagementModToggle.example.jar").Entity);
            bool toggled = SpinWait.SpinUntil(() =>
            {
                scene = fixture.Shell.Render(new(1000, 650));
                return scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "ManagementContentDetails.example.jar.disabled");
            }, TimeSpan.FromSeconds(10));
            if (!toggled) throw new InvalidOperationException($"Toggle did not refresh: file={File.Exists(Path.Combine(mods, "example.jar.disabled"))}; page={settings.SelectedSection}; errors={string.Join(";", fixture.Feedback.Snapshot().Notifications.Select(item => item.Message))}");
            AssertTrue(File.Exists(Path.Combine(mods, "example.jar.disabled")));
            AssertFalse(File.Exists(Path.Combine(mods, "example.jar")));
            void Category(string key)
            {
                Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "ManagementModCategory." + key).Entity);
                scene = fixture.Shell.Render(new(1000, 650));
            }
            bool HasMod(string name) => scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "ManagementContentDetails." + name);
            var modSearch = FindByKey(fixture.Shell, scene, "ManagementContentSearch").Entity;
            Category("enabled");
            AssertTrue(HasMod("iris.jar"));
            AssertFalse(HasMod("example.jar.disabled"));
            Category("disabled");
            AssertFalse(HasMod("iris.jar"));
            AssertTrue(HasMod("example.jar.disabled"));
            AssertEqual(modSearch, FindByKey(fixture.Shell, scene, "ManagementContentSearch").Entity);
            fixture.Shell.Renderer.SetTextInputValue(modSearch, "Iris");
            scene = fixture.Shell.Render(new(1000, 650));
            AssertFalse(HasMod("example.jar.disabled"));
            Category("enabled");
            AssertTrue(HasMod("iris.jar"));
            fixture.Shell.Renderer.SetTextInputValue(modSearch, "");
            scene = fixture.Shell.Render(new(1000, 650));
            Category("problems");
            AssertTrue(HasMod("example.jar.disabled"));
            AssertFalse(HasMod("iris.jar"));
            Category("updates");
            AssertFalse(HasMod("iris.jar"));
            Category("unchecked");
            AssertTrue(HasMod("iris.jar"));
            Category("all");
            AssertTrue(HasMod("iris.jar") && HasMod("example.jar.disabled"));
            Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.resourcepacks").Entity);
            scene = fixture.Shell.Render(new(1000, 650));
            scene = fixture.Shell.Render(new(1000, 650));
            AssertEqual("resourcepacks", settings.SelectedSection);
            AssertTrue(scene.Nodes.Any(node => node.Text == "00Pack"));
            var packTitle = scene.Nodes.Single(node => node.Text == "00Pack");
            AssertEqual(2, packTitle.TextRuns!.Count);
            AssertTrue(packTitle.TextRuns[1].Bold);
            var firstRow = scene.Nodes.First(node => fixture.Shell.Tree.Name(node.Entity) == "ManagementContentRow");
            var icon = scene.Nodes.First(node => fixture.Shell.Tree.Name(node.Entity) == "ManagementContentIcon");
            var details = FindByKey(fixture.Shell, scene, "ManagementContentDetails.00§a§lPack.zip");
            AssertTrue(Math.Abs(icon.Rect.X - firstRow.Rect.X - 16) < .01);
            AssertTrue(Math.Abs(firstRow.Rect.X + firstRow.Rect.Width - details.Rect.X - details.Rect.Width - 16) < .01);
            var formattedName = scene.Nodes.Single(node => node.Text == "Green Pack\n附加§?说明");
            AssertTrue(packTitle.Rect.Y < formattedName.Rect.Y);
            AssertEqual(2, formattedName.TextRuns!.Count);
            AssertTrue(formattedName.TextRuns[1].Bold && formattedName.TextRuns[1].Italic && formattedName.TextRuns[1].Underline);
            AssertFalse(scene.Nodes.Any(node => node.Text?.Contains("§a", StringComparison.Ordinal) == true));
            Emit(fixture.Intents, "ui.settings.management.action", details.Entity);
            scene = fixture.Shell.Render(new(1000, 650));
            AssertTrue(scene.Nodes.Single(node => node.Text == "00Pack").TextRuns![1].Bold);
            Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "Management.返回列表").Entity);
            scene = fixture.Shell.Render(new(1000, 650));
            Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "Management.打开文件夹").Entity);
            scene = fixture.Shell.Render(new(1000, 650));
            AssertEqual(resourcepacks, openedDirectory!);
            var search = FindByKey(fixture.Shell, scene, "ManagementContentSearch");
            AssertTrue(search.Rect.Width > 250);
            AssertTrue(fixture.Shell.Renderer.PointerPressed(new(search.Rect.X + 20, search.Rect.Y + 18)));
            AssertEqual(search.Entity, fixture.Shell.Renderer.Focused);
            fixture.Shell.Renderer.PointerReleased(new(search.Rect.X + 20, search.Rect.Y + 18));
            AssertEqual(search.Entity, fixture.Shell.Renderer.Focused);
            AssertTrue(fixture.Shell.Renderer.InsertText("00Pack"));
            scene = fixture.Shell.Render(new(1000, 650));
            AssertTrue(scene.Nodes.Any(node => node.Text == "00Pack"));
            fixture.Shell.Renderer.SetTextInputValue(search.Entity, "Green Pack");
            scene = fixture.Shell.Render(new(1000, 650));
            AssertTrue(scene.Nodes.Any(node => node.Text == "Green Pack\n附加§?说明"));
            AssertFalse(scene.Nodes.Any(node => node.Text == "pack-001"));
            fixture.Shell.Renderer.SetTextInputValue(search.Entity, "pack-219");
            scene = fixture.Shell.Render(new(1000, 650));
            AssertEqual(search.Entity, fixture.Shell.Renderer.Focused);
            AssertTrue(scene.Nodes.Any(node => node.Text == "pack-219"));
            AssertFalse(scene.Nodes.Any(node => node.Text == "00Pack"));
            fixture.Shell.Renderer.SetTextInputValue(search.Entity, "");
            scene = fixture.Shell.Render(new(1000, 650));
            var list = FindByKey(fixture.Shell, scene, "ManagementContentList");
            AssertTrue(fixture.Shell.Tree.Children(list.Entity).Count < 40);
            var body = fixture.Shell.Tree.Parent(list.Entity);
            fixture.Shell.Tree.GetComponent<XsrUiScroll>(body)!.OffsetY = 100000;
            scene = fixture.Shell.Render(new(1000, 650));
            scene = fixture.Shell.Render(new(1000, 650));
            AssertTrue(fixture.Shell.Tree.Children(list.Entity).Count < 40);
            AssertTrue(scene.Nodes.Any(node => node.Text == "pack-219"));
            Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.screenshots").Entity);
            scene = fixture.Shell.Render(new(1000, 650));
            var card = FindByKey(fixture.Shell, scene, "ManagementScreenshot.screen.png");
            AssertTrue(card.Rect.Width > 180);
            var thumbnail = scene.Nodes.First(node => fixture.Shell.Tree.Name(node.Entity) == "ManagementContentIcon");
            AssertTrue(Math.Abs(thumbnail.Rect.X - card.Rect.X - 12) < .01);
            AssertTrue(Math.Abs(card.Rect.X + card.Rect.Width - thumbnail.Rect.X - thumbnail.Rect.Width - 12) < .01);
            AssertTrue(scene.Nodes.Any(node => node.RasterImage?.FitToBounds == true));
            Emit(fixture.Intents, "ui.settings.management.action", card.Entity);
            scene = fixture.Shell.Render(new(1000, 650));
            AssertTrue(scene.Nodes.Any(node => node.Text == "图像尺寸"));
            Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "Management.返回列表").Entity);
            scene = fixture.Shell.Render(new(1000, 650));
            AssertTrue(scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "ManagementScreenshot.screen.png"));
            Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.saves").Entity);
            scene = fixture.Shell.Render(new(1000, 650));
            AssertFalse(scene.Nodes.Any(node => node.Text == "移除"));
            Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "ManagementContentDetails.My World").Entity);
            scene = fixture.Shell.Render(new(1000, 650));
            AssertTrue(scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "ManagementContentDetail"));
            Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.shaderpacks").Entity);
            scene = fixture.Shell.Render(new(1000, 650));
            AssertTrue(scene.Nodes.Any(node => node.ImageSource == "nexa/content-shader"));
            Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "ManagementContentDetails.Shader.zip").Entity);
            scene = fixture.Shell.Render(new(1000, 650));
            AssertTrue(scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "ManagementContentDetail"));
            instance = vanilla;
            AssertTrue(SpinWait.SpinUntil(() =>
            {
                scene = fixture.Shell.Render(new(1000, 650));
                return scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "SettingsNav.game")
                    && !scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "SettingsNav.mods");
            }, TimeSpan.FromSeconds(10)));
            AssertEqual("overview", settings.SelectedSection);
            AssertFalse(scene.Nodes.Any(node => node.Text?.StartsWith("pack-", StringComparison.Ordinal) == true));
        }
        finally { Directory.Delete(root, true); }
    }
}

