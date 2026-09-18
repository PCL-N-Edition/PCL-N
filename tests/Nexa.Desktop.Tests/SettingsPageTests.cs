using Nexa.Desktop.Ui;
using Nexa.Services.Settings;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void SettingsPageUsesFinalNavigationAndCompactLayout()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries, fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true;
        fixture.Controller.SettingsPage = settings.Page;
        Emit(fixture.Intents, "ui.navigation.settings");
        var scene = fixture.Shell.Render(new(1000, 650));
        AssertEqual(settings.Page, fixture.Shell.Stage.Navigation.Current);
        AssertEqual(8, scene.Nodes.Count(item => fixture.Shell.Tree.Name(item.Entity).StartsWith("SettingsNav.", StringComparison.Ordinal)));
        var nav = FindByKey(fixture.Shell, scene, "SettingsNavigation");
        var body = FindByKey(fixture.Shell, scene, "SettingsSections");
        AssertEqual(44d, nav.Rect.Height);
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiSegmentedTrack>(nav.Entity) is not null);
        var pager = FindByKey(fixture.Shell, scene, "SettingsPager");
        AssertEqual(8, fixture.Shell.Tree.GetComponent<XsrUiPager>(pager.Entity)!.PageCount);
        fixture.Shell.Renderer.SelectPagerPage(pager.Entity, 1);
        scene = fixture.Shell.Render(new(1000, 650));
        AssertEqual("appearance", settings.SelectedSection);
        AssertTrue(body.Rect.Width > 650);
        AssertTrue(body.Rect.Y > nav.Rect.Y);
        AssertFalse(scene.Nodes.Any(item => item.Text?.Contains("尚未迁移", StringComparison.Ordinal) == true));
        foreach (var page in SettingsCatalog.GlobalPages)
        {
            var button = FindByKey(fixture.Shell, scene, "SettingsNav." + page.Id);
            Emit(fixture.Intents, "ui.settings.section", button.Entity);
            scene = fixture.Shell.Render(new(1000, 650));
            AssertEqual(page.Id, settings.SelectedSection);
            AssertTrue(scene.Nodes.Any(item => item.Text == "尚未可用"));
            AssertTrue(scene.Nodes.Where(item => fixture.Shell.Tree.Name(item.Entity).StartsWith("SettingsRow.", StringComparison.Ordinal)).All(item => item.Rect.Width > 0));
        }
        scene = fixture.Shell.Render(new(760, 500));
        AssertTrue(FindByKey(fixture.Shell, scene, "SettingsSections").Rect.Width > 480);
    }

    private static void SettingsPageSavesThroughServicesAndPreservesDraftFocus()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries, fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true;
        fixture.Controller.SettingsPage = settings.Page;
        Emit(fixture.Intents, "ui.navigation.settings");
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.game").Entity);
        scene = fixture.Shell.Render(new(1000, 650));
        var input = FindByKey(fixture.Shell, scene, "SettingsInput.game.width");
        fixture.Shell.Renderer.SetTextInputValue(input.Entity, "1280");
        fixture.Shell.Renderer.Focus(input.Entity);
        fixture.Shell.Render(new(1000, 650));
        AssertEqual("1280", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(input.Entity)!.ReadDraft());
        AssertEqual(input.Entity, fixture.Shell.Renderer.Focused);
        Emit(fixture.Intents, "ui.settings.edit", FindByKey(fixture.Shell, scene, "SettingsEdit.game.width").Entity);
        fixture.Shell.Render(new(1000, 650));
        AssertTrue(SpinWait.SpinUntil(() => fixture.Foundation.Host.Settings.GetValue<int>("LaunchArgumentWindowWidth").Value == 1280, TimeSpan.FromSeconds(5)));
        fixture.Shell.Render(new(1000, 650));
        AssertEqual("1280", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(input.Entity)!.ReadDraft());
        AssertFalse(fixture.Shell.Tree.Children(input.Entity).Any());
    }

    private static void SettingsDeveloperToggleKeepsPositionAndFocus()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries, fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true;
        fixture.Controller.SettingsPage = settings.Page;
        Emit(fixture.Intents, "ui.navigation.settings");
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.advanced").Entity);
        scene = fixture.Shell.Render(new(1000, 650));
        var list = FindByKey(fixture.Shell, scene, "SettingsSections").Entity;
        fixture.Shell.Tree.GetComponent<XsrUiScroll>(list)!.OffsetY = 100000;
        fixture.Shell.Tree.MarkDirty(list, XsrUiDirtyKinds.Layout);
        scene = fixture.Shell.Render(new(1000, 650));
        double offset = fixture.Shell.Tree.GetComponent<XsrUiScroll>(list)!.OffsetY;
        var toggle = FindByKey(fixture.Shell, scene, "SettingsEdit.developer.enabled").Entity;
        fixture.Shell.Renderer.Focus(toggle);
        Emit(fixture.Intents, "ui.settings.edit", toggle);
        fixture.Shell.Render(new(1000, 650));
        AssertTrue(SpinWait.SpinUntil(() => fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!.Values.Single(item => item.Key == "developer.enabled").Value.Value == "true", TimeSpan.FromSeconds(5)));
        scene = fixture.Shell.Render(new(1000, 650));
        AssertEqual("SettingsEdit.developer.enabled", fixture.Shell.Tree.Name(fixture.Shell.Renderer.Focused));
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiScroll>(list)!.OffsetY >= offset);
        bool hasInspector = false;
        fixture.Shell.Tree.Walk(settings.Page, entity => { hasInspector |= fixture.Shell.Tree.GetComponent<XsrUiText>(entity)?.Content == "XSR State Inspector"; return true; });
        AssertTrue(hasInspector);
        AssertFalse(scene.Nodes.Any(item => item.Label?.EndsWith("，尚未可用", StringComparison.Ordinal) == true && item.IsClickable));
    }

    private static void SettingsChoiceMenuIsAnchoredAndCommitsSelection()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries, fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true;
        fixture.Controller.SettingsPage = settings.Page;
        Emit(fixture.Intents, "ui.navigation.settings");
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.game").Entity);
        scene = fixture.Shell.Render(new(1000, 650));
        var origin = FindByKey(fixture.Shell, scene, "SettingsEdit.game.window-mode");
        Emit(fixture.Intents, "ui.settings.edit", origin.Entity);
        scene = fixture.Shell.Render(new(1000, 650));
        var menu = FindByKey(fixture.Shell, scene, "SettingsChoiceMenu");
        AssertTrue(menu.Rect.Height is > 64 and < 100);
        AssertTrue(menu.Rect.X + menu.Rect.Width <= 1000);
        AssertTrue(Math.Abs(menu.Rect.Y - origin.Rect.Y - origin.Rect.Height - 4) < 2);
        Emit(fixture.Intents, "ui.settings.choice", FindByKey(fixture.Shell, scene, "SettingsChoice.fullscreen").Entity);
        fixture.Shell.Render(new(1000, 650));
        AssertTrue(SpinWait.SpinUntil(() => fixture.Foundation.Host.Settings.GetValue<int>("LaunchArgumentWindowType").Value == 0, TimeSpan.FromSeconds(5)));
        scene = fixture.Shell.Render(new(1000, 650));
        AssertFalse(scene.Nodes.Any(item => fixture.Shell.Tree.Name(item.Entity) == "SettingsChoiceMenu"));
        AssertEqual(origin.Entity, fixture.Shell.Renderer.Focused);
        Emit(fixture.Intents, "ui.settings.edit", origin.Entity);
        fixture.Shell.Render(new(1000, 650));
        fixture.Shell.Renderer.PointerPressed(new(500, 100));
        fixture.Shell.Renderer.PointerReleased(new(500, 100));
        scene = fixture.Shell.Render(new(1000, 650));
        AssertFalse(scene.Nodes.Any(item => fixture.Shell.Tree.Name(item.Entity) == "SettingsChoiceMenu"));
    }
}
