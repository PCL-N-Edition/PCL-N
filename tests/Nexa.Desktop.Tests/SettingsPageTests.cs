using Nexa.Desktop.Ui;
using Nexa.Services.Settings;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void ProductPxmlShellAcceptsNativeWindowMetrics()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var shell = fixture.Shell;
        shell.PublishWindowMetrics(0, false, 8);
        var scene = shell.Render(new(1000, 650));
        AssertEqual(8d, shell.Tree.GetComponent<XsrUiElement>(shell.Root)!.Padding.Left);
        AssertEqual(8d, scene.Nodes.Single(node => node.Entity == shell.TitleBar).Rect.X);
        shell.PublishWindowMetrics(84, false, 0);
        AssertEqual(84d, shell.Tree.GetComponent<XsrUiElement>(shell.TitleBar)!.Padding.Left);
        shell.PublishWindowMetrics(84, true, 8);
        AssertEqual(0d, shell.Tree.GetComponent<XsrUiElement>(shell.Root)!.Padding.Left);
        AssertEqual(0d, shell.Tree.GetComponent<XsrUiElement>(shell.TitleBar)!.Padding.Left);
        shell.Render(new(1200, 800));
        shell.PublishWindowMetrics(0, false, 8);
        shell.Render(new(850, 500));
    }

    private static void SettingsPageUsesFinalNavigationAndCompactLayout()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries, fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true;
        fixture.Controller.SettingsPage = settings.Page;
        Emit(fixture.Intents, "ui.navigation.settings");
        var scene = fixture.Shell.Render(new(1000, 650));
        AssertEqual(settings.Page, fixture.Shell.Stage.Navigation.Current);
        AssertEqual(9, scene.Nodes.Count(item => fixture.Shell.Tree.Name(item.Entity).StartsWith("SettingsNav.", StringComparison.Ordinal)));
        var nav = FindByKey(fixture.Shell, scene, "SettingsNavigation");
        var body = FindByKey(fixture.Shell, scene, "SettingsSections");
        AssertEqual(44d, nav.Rect.Height);
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiSegmentedTrack>(nav.Entity) is not null);
        var pager = FindByKey(fixture.Shell, scene, "SettingsPager");
        AssertEqual(9, fixture.Shell.Tree.GetComponent<XsrUiPager>(pager.Entity)!.PageCount);
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
            if (page.Id != "platform") AssertTrue(scene.Nodes.Any(item => item.Text == "尚未可用"));
            AssertTrue(scene.Nodes.Where(item => fixture.Shell.Tree.Name(item.Entity).StartsWith("SettingsRow.", StringComparison.Ordinal)).All(item => item.Rect.Width > 0));
        }
        scene = fixture.Shell.Render(new(760, 500));
        AssertTrue(FindByKey(fixture.Shell, scene, "SettingsSections").Rect.Width > 480);
    }

    private static void VersionSettingsAreScopedAndRestoreInheritance()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        string instance = Path.GetFullPath("test-instance-settings");
        using var global = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries, fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries, fixture.Foundation.Commands, fixture.Store, fixture.Feedback, () => instance);
        fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 650));
        var input = FindByKey(fixture.Shell, scene, "SettingsInput.game.width");
        fixture.Shell.Renderer.SetTextInputValue(input.Entity, "1440");
        Emit(fixture.Intents, "ui.settings.edit", FindByKey(fixture.Shell, scene, "SettingsEdit.game.width").Entity);
        fixture.Shell.Render(new(1000, 650));
        string? ReadWidth(string? scope) => fixture.Foundation.Host.SettingsPolicy.Read(new(scope)).Value!.Values.Single(item => item.Key == "game.width").Value.Value;
        AssertTrue(SpinWait.SpinUntil(() => ReadWidth(instance) == "1440", TimeSpan.FromSeconds(5)));
        AssertTrue(ReadWidth(null) != "1440");
        fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.inherit", FindByKey(fixture.Shell, scene, "SettingsInherit.game.width").Entity);
        fixture.Shell.Render(new(1000, 650));
        AssertTrue(SpinWait.SpinUntil(() => ReadWidth(instance) == ReadWidth(null), TimeSpan.FromSeconds(5)));
        instance = Path.GetFullPath("other-root/test-instance-settings");
        scene = fixture.Shell.Render(new(1000, 650));
        input = FindByKey(fixture.Shell, scene, "SettingsInput.game.width");
        AssertEqual(ReadWidth(null), fixture.Shell.Tree.GetComponent<XsrUiTextInput>(input.Entity)!.ReadDraft());
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
        var toggle = FindByKey(fixture.Shell, scene, "SettingsOption.developer.enabled.true").Entity;
        fixture.Shell.Renderer.Focus(toggle);
        Emit(fixture.Intents, "ui.settings.choice", toggle);
        fixture.Shell.Render(new(1000, 650));
        AssertTrue(SpinWait.SpinUntil(() => fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!.Values.Single(item => item.Key == "developer.enabled").Value.Value == "true", TimeSpan.FromSeconds(5)));
        scene = fixture.Shell.Render(new(1000, 650));
        AssertEqual("SettingsOption.developer.enabled.true", fixture.Shell.Tree.Name(fixture.Shell.Renderer.Focused));
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiScroll>(list)!.OffsetY >= offset);
        bool hasInspector = false;
        fixture.Shell.Tree.Walk(settings.Page, entity => { hasInspector |= fixture.Shell.Tree.GetComponent<XsrUiText>(entity)?.Content == "XSR State Inspector"; return true; });
        AssertTrue(hasInspector);
        AssertFalse(scene.Nodes.Any(item => item.Label?.EndsWith("，尚未可用", StringComparison.Ordinal) == true && item.IsClickable));
    }

    private static void SettingsArgumentRowsSupportAddRemoveAndApply()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries, fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true;
        fixture.Controller.SettingsPage = settings.Page;
        Emit(fixture.Intents, "ui.navigation.settings");
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.game").Entity);
        fixture.Shell.Render(new(1000, 650));
        Nexa.UI.Next.XsrUiEntityId Find(string name)
        {
            Nexa.UI.Next.XsrUiEntityId found = default;
            fixture.Shell.Tree.Walk(settings.Page, entity => { if (fixture.Shell.Tree.Name(entity) == name) found = entity; return true; });
            AssertTrue(found.IsAssigned); return found;
        }
        fixture.Shell.Renderer.SetTextInputValue(Find("SettingsArgument.game.arguments.0"), "--demo");
        Emit(fixture.Intents, "ui.settings.argument.add", Find("SettingsArgumentAdd.game.arguments"));
        fixture.Shell.Render(new(1000, 650));
        AssertEqual("--demo", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(Find("SettingsArgument.game.arguments.0"))!.ReadDraft());
        fixture.Shell.Renderer.SetTextInputValue(Find("SettingsArgument.game.arguments.1"), "--fullscreen");
        Emit(fixture.Intents, "ui.settings.argument.remove", Find("SettingsArgumentRemove.game.arguments.0"));
        fixture.Shell.Render(new(1000, 650));
        AssertEqual("--fullscreen", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(Find("SettingsArgument.game.arguments.0"))!.ReadDraft());
        Emit(fixture.Intents, "ui.settings.edit", Find("SettingsEdit.game.arguments"));
        fixture.Shell.Render(new(1000, 650));
        AssertTrue(SpinWait.SpinUntil(() => fixture.Foundation.Host.Settings.GetValue<string>("LaunchAdvanceGame").Value == "--fullscreen", TimeSpan.FromSeconds(5)));
    }

    private static void SettingsPlatformReadsAndRefreshesServiceSnapshot()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries, fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true;
        fixture.Controller.SettingsPage = settings.Page;
        Emit(fixture.Intents, "ui.navigation.settings");
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.platform").Entity);
        bool Ready()
        {
            scene = fixture.Shell.Render(new(1000, 650));
            bool found = false;
            fixture.Shell.Tree.Walk(settings.Page, entity => { found |= fixture.Shell.Tree.Name(entity) == "PlatformCapability.platform.os"; return true; });
            return found;
        }
        AssertTrue(SpinWait.SpinUntil(Ready, TimeSpan.FromSeconds(5)));
        var id = fixture.Store.Resolve(Nexa.Services.Capabilities.MachineCapabilityStateContract.RevisionKey);
        long before = fixture.Store.Read<long>(id).Value;
        Emit(fixture.Intents, "ui.settings.platform.refresh", FindByKey(fixture.Shell, scene, "PlatformRefresh").Entity);
        AssertTrue(SpinWait.SpinUntil(() => { Ready(); return fixture.Store.Read<long>(id).Value > before; }, TimeSpan.FromSeconds(5)));
        AssertTrue(SpinWait.SpinUntil(Ready, TimeSpan.FromSeconds(5)));
    }

    private static void SettingsInlineSelectorCommitsBothDirections()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries, fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true;
        fixture.Controller.SettingsPage = settings.Page;
        Emit(fixture.Intents, "ui.navigation.settings");
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.game").Entity);
        scene = fixture.Shell.Render(new(1000, 650));
        var next = FindByKey(fixture.Shell, scene, "SettingsOption.game.window-mode.fullscreen");
        var selector = FindByKey(fixture.Shell, scene, "SettingsSelector.game.window-mode");
        AssertTrue(selector.Rect.Width < 150);
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiSegmentedTrack>(selector.Entity) is not null);

        var thumb = FindByKey(fixture.Shell, scene, "SettingsSelectorThumb.game.window-mode");
        var start = new XsrUiPoint(thumb.Rect.X + thumb.Rect.Width / 2, thumb.Rect.Y + thumb.Rect.Height / 2);
        var end = new XsrUiPoint(next.Rect.X + next.Rect.Width / 2, next.Rect.Y + next.Rect.Height / 2);
        fixture.Shell.Renderer.PointerPressed(start);
        fixture.Shell.Renderer.PointerMoved(end);
        fixture.Shell.Renderer.PointerReleased(end);
        fixture.Shell.Render(new(1000, 650));
        AssertTrue(SpinWait.SpinUntil(() => fixture.Foundation.Host.Settings.GetValue<int>("LaunchArgumentWindowType").Value == 0, TimeSpan.FromSeconds(5)));
        scene = fixture.Shell.Render(new(1000, 650));
        AssertFalse(scene.Nodes.Any(item => fixture.Shell.Tree.Name(item.Entity) == "SettingsChoiceMenu"));
        AssertTrue(scene.Nodes.Any(item => item.Text == "全屏"));
        Emit(fixture.Intents, "ui.settings.choice", FindByKey(fixture.Shell, scene, "SettingsOption.game.window-mode.windowed").Entity);
        fixture.Shell.Render(new(1000, 650));
        AssertTrue(SpinWait.SpinUntil(() => fixture.Foundation.Host.Settings.GetValue<int>("LaunchArgumentWindowType").Value == 1, TimeSpan.FromSeconds(5)));
    }
}
