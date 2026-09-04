using PCL.Desktop.Ui;
using PCL.Services.Accounts;
using PCL.Services.Composition;
using PCL.UI.Next;
using PCL.Xsr;

namespace PCL.Desktop.Tests;

internal static partial class Program
{
    private static void LaunchWidgetsPreserveOriginalContent()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        XsrUiShell shell = fixture.Shell;
        shell.Renderer.ReducedMotion = true;
        XsrUiSize size = new(810, 470);
        XsrUiScene scene = shell.Render(size);
        AssertEqual("关于 PCL N Edition", FindByKey(shell, scene, "AboutTitle").Text);
        AssertFalse(HasKey(shell, scene, "WidgetPosition"));
        XsrUiSceneNode aboutIndicator = FindByKey(shell, scene, "WidgetAboutIndicator");
        XsrUiSceneNode triviaIndicator = FindByKey(shell, scene, "WidgetTriviaIndicator");
        AssertTrue(aboutIndicator.Label!.Contains("当前卡片", StringComparison.Ordinal));
        AssertClose(aboutIndicator.Rect.X, triviaIndicator.Rect.X);
        AssertClose(6, aboutIndicator.Rect.Width);
        AssertClose(16, aboutIndicator.Rect.Height);
        AssertClose(6, triviaIndicator.Rect.Width);
        AssertClose(6, triviaIndicator.Rect.Height);
        AssertClose(aboutIndicator.Rect.Y + aboutIndicator.Rect.Height + 8, triviaIndicator.Rect.Y);
        AssertClose(30, FindByKey(shell, scene, "WidgetIndicators").Rect.Height);
        XsrUiRect viewport = FindByKey(shell, scene, "LaunchWidgetPager").Rect;
        AssertTrue(aboutIndicator.Rect.X >= viewport.X + viewport.Width);
        AssertClose(16, FindByKey(shell, scene, "WidgetAboutDot").Rect.Height);
        AssertClose(6, FindByKey(shell, scene, "WidgetTriviaDot").Rect.Height);
        AssertTrue(FindByKey(shell, scene, "AboutMessage").VisualStyle.WrapText);
        AssertTrue(shell.Renderer.Activate(triviaIndicator.Entity));
        scene = shell.Render(size);
        AssertEqual("你知道吗？", FindByKey(shell, scene, "TriviaTitle").Text);
        AssertTrue(FindByKey(shell, scene, "WidgetTriviaIndicator").Label!.Contains("当前卡片", StringComparison.Ordinal));
        AssertClose(6, FindByKey(shell, scene, "WidgetAboutDot").Rect.Height);
        AssertClose(16, FindByKey(shell, scene, "WidgetTriviaDot").Rect.Height);
        AssertClose(6, FindByKey(shell, scene, "WidgetAboutIndicator").Rect.Height);
        AssertClose(16, FindByKey(shell, scene, "WidgetTriviaIndicator").Rect.Height);
        AssertFalse(HasKey(shell, scene, "AboutMessage"));
        string hint = FindByKey(shell, scene, "TriviaMessage").Text!;
        AssertTrue(LaunchWidgetHints.BuiltIn.Contains(hint));
        AssertTrue(shell.Renderer.Activate(FindByKey(shell, scene, "TriviaPage").Entity));
        scene = shell.Render(size);
        AssertTrue(FindByKey(shell, scene, "TriviaMessage").Text != hint);
        XsrUiRect pager = FindByKey(shell, scene, "LaunchWidgetPager").Rect;
        AssertTrue(shell.Renderer.PointerScroll(new XsrUiPoint(pager.X + 10, pager.Y + 10), -1));
        scene = shell.Render(size);
        AssertTrue(HasKey(shell, scene, "AboutMessage"));
        AssertEqual(0d, scene.Nodes.Single(node => node.Role == XsrUiSemanticRole.TitleBar).VisualStyle.CornerRadius);
        AssertEqual(0d, scene.Nodes.Single(node => node.Role == XsrUiSemanticRole.Navigation).VisualStyle.CornerRadius);
        AssertTrue(scene.Nodes.Single(node => node.Entity == shell.NavigationToggle).VisualStyle.NavigationLayout);
    }

    private static void RailAnimationRetainsContentAndCardContainment()
    {
        ControllableInstanceSource source = new();
        using LaunchPageFixture fixture = new(source, addProfile: true);
        source.Complete(0, [Instance("a-very-long-version-name-with-a-loader-and-many-mods-1.21.1")]);
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        XsrUiEntityId page = fixture.Shell.Stage.Navigation.Current;
        XsrSemanticId destination = fixture.Shell.SelectedNavigationId;
        foreach (XsrUiShellStyle style in Enum.GetValues<XsrUiShellStyle>())
            foreach (bool reduced in new[] { false, true })
                foreach (XsrUiSize size in new[] { new XsrUiSize(810, 470), new XsrUiSize(850, 500), new XsrUiSize(1280, 800) })
                {
                    fixture.Shell.SetStyle(style);
                    fixture.Shell.Renderer.ReducedMotion = reduced;
                    _ = fixture.Shell.Render(size);
                    AssertTrue(fixture.Shell.Renderer.Activate(fixture.Shell.NavigationToggle));
                    foreach (double progress in new[] { 0d, .5, 1, .5, 0 })
                    {
                        fixture.Shell.SetRailPresentationProgress(progress);
                        XsrUiScene scene = fixture.Shell.Render(size);
                        AssertEqual(page, fixture.Shell.Stage.Navigation.Current);
                        AssertEqual(destination, fixture.Shell.SelectedNavigationId);
                        AssertEqual(1, source.Count);
                        AssertEqual("Player", FindByKey(fixture.Shell, scene, "AccountName").Text);
                        XsrUiRect version = FindByKey(fixture.Shell, scene, "CardVersion").Rect;
                        AssertClose(184, version.Height);
                        foreach (string key in new[] { "VersionContent", "VersionHeaderRow", "InstanceListButton",
                    "InstanceRow", "InstanceRowContent", "VersionName", "InstanceModify", "InstanceSettings", "LaunchButton" })
                            AssertContains(version, FindByKey(fixture.Shell, scene, key).Rect);
                        XsrUiRect about = FindByKey(fixture.Shell, scene, "CardAbout").Rect;
                        AssertClose(version.Y + version.Height + 12, about.Y);
                        AssertTrue(about.Height > 0);
                    }
                    AssertTrue(fixture.Shell.Renderer.Activate(fixture.Shell.NavigationToggle));
                }
    }

    private static void AccountRosterUpdatesAtFrameBoundary()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]), addProfile: true);
        _ = fixture.Shell.Render(new XsrUiSize(850, 500));
        XsrUiEntityId[] dirty = [.. fixture.Shell.Tree.DirtyEntities()];
        Task.Run(() => fixture.Service.AddProfile(new LaunchProfile { Username = "Worker" })).GetAwaiter().GetResult();
        AssertTrue(dirty.SequenceEqual(fixture.Shell.Tree.DirtyEntities()));
        Emit(fixture.Intents, "ui.account.switch");
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(850, 500));
        XsrUiEntityId row = FindByKey(fixture.Shell, scene, "account-row:1").Entity;
        AssertTrue(fixture.Shell.Renderer.Focus(row));
        _ = fixture.Shell.Render(new XsrUiSize(850, 500));
        XsrUiScene unchanged = fixture.Shell.Render(new XsrUiSize(850, 500));
        AssertEqual(row, FindByKey(fixture.Shell, unchanged, "account-row:1").Entity);
        AssertEqual(row, fixture.Shell.Renderer.Focused);
        AssertTrue(fixture.Shell.Renderer.HandleKey(XsrUiKey.Enter));
        AssertTrue(SpinWait.SpinUntil(() => fixture.Service.SelectedIndex == 1, TimeSpan.FromSeconds(2)));
        _ = fixture.Shell.Render(new XsrUiSize(850, 500));
        Task.Run(() => fixture.Service.RemoveProfile(1)).GetAwaiter().GetResult();
        scene = fixture.Shell.Render(new XsrUiSize(850, 500));
        AssertEqual("Player", FindByKey(fixture.Shell, scene, "AccountName").Text);
        AssertFalse(fixture.Shell.Renderer.Activate(row));
    }

    private static void AccountRosterScrollsWithinCard()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        for (int i = 0; i < 30; i++)
            AssertTrue(fixture.Service.AddProfile(new LaunchProfile { Username = $"Player {i}" }).IsSuccess);
        Emit(fixture.Intents, "ui.account.switch");
        XsrUiSize size = new(810, 470);
        XsrUiScene scene = fixture.Shell.Render(size);
        XsrUiRect viewport = FindByKey(fixture.Shell, scene, "AccountRows").Rect;
        AssertTrue(HasKey(fixture.Shell, scene, "account-row:0"));
        AssertFalse(HasKey(fixture.Shell, scene, "account-row:29"));
        AssertTrue(fixture.Shell.Renderer.PointerScroll(new XsrUiPoint(viewport.X + 10, viewport.Y + 10), 10000));
        scene = fixture.Shell.Render(size);
        AssertFalse(HasKey(fixture.Shell, scene, "account-row:0"));
        AssertTrue(HasKey(fixture.Shell, scene, "account-row:29"));
        HashSet<XsrUiEntityId> accountRows = [];
        fixture.Shell.Tree.Walk(FindByKey(fixture.Shell, scene, "AccountRows").Entity, entity =>
        {
            accountRows.Add(entity);
            return true;
        });
        foreach (XsrUiSceneNode node in scene.Nodes.Where(node => node.ClipRect is not null && accountRows.Contains(node.Entity)))
            AssertContains(viewport, node.ClipRect!.Value);
        AssertContains(FindByKey(fixture.Shell, scene, "CardAccount").Rect, viewport);
        XsrUiRect footer = FindByKey(fixture.Shell, scene, "AccountSummary").Rect;
        XsrUiEntityId hit = fixture.Shell.Renderer.HitTest(new XsrUiPoint(footer.X + 2, footer.Y + 2));
        AssertFalse(fixture.Shell.Tree.Name(hit).StartsWith("account-row:", StringComparison.Ordinal));
    }

    private static void SelectedProfileIsUsedByLaunch()
    {
        RecordingStartRoute recording = new();
        using MinecraftRuntime runtime = CreateRecordingRuntime(recording);
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([Instance("playable")]), runtime,
            addProfile: true, ownsMinecraftRuntime: false);
        AssertTrue(fixture.Service.AddProfile(new LaunchProfile { Username = "Selected" }).IsSuccess);
        Emit(fixture.Intents, "ui.account.switch");
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(850, 500));
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "account-row:1").Entity));
        AssertTrue(SpinWait.SpinUntil(() => fixture.Service.SelectedIndex == 1, TimeSpan.FromSeconds(2)));
        scene = fixture.Shell.Render(new XsrUiSize(850, 500));
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "LaunchButton").Entity));
        AssertTrue(SpinWait.SpinUntil(() => recording.LastCommand is not null, TimeSpan.FromSeconds(2)));
        AssertEqual(1, recording.LastCommand!.AccountIndex);
    }

    private static void UnavailableLaunchCannotBeInvoked()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([Instance("playable")]));
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(850, 500));
        XsrUiSceneNode button = FindByKey(fixture.Shell, scene, "LaunchButton");
        AssertEqual("未选择档案", button.Text);
        AssertFalse(button.IsEnabled);
        AssertFalse(button.IsClickable);
        AssertFalse(fixture.Shell.Renderer.Activate(button.Entity));
        AssertFalse(fixture.Shell.Renderer.Focus(button.Entity));
        AssertFalse(fixture.Shell.Renderer.PointerPressed(new XsrUiPoint(button.Rect.X + 8, button.Rect.Y + 8)));
        AssertTrue(fixture.Service.AddProfile(new LaunchProfile { Username = "Now ready" }).IsSuccess);
        scene = fixture.Shell.Render(new XsrUiSize(850, 500));
        AssertEqual("启动游戏", FindByKey(fixture.Shell, scene, "LaunchButton").Text);
        AssertTrue(FindByKey(fixture.Shell, scene, "LaunchButton").IsEnabled);
    }

    private static void VersionSubpagesHaveIndependentRoutesAndRestoreFocus()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([Instance("playable")]), addProfile: true);
        XsrUiSize size = new(850, 500);
        foreach ((string entry, string key, string title) in new[]
        {
            ("InstanceListButton", "VersionListPage", "版本列表"),
            ("InstanceSettings", "VersionSettingsPage", "版本设置"),
            ("InstanceModify", "VersionModifyPage", "版本修改"),
        })
        {
            XsrUiScene home = fixture.Shell.Render(size);
            XsrUiEntityId source = FindByKey(fixture.Shell, home, entry).Entity;
            AssertTrue(fixture.Shell.Renderer.Focus(source));
            AssertTrue(fixture.Shell.Renderer.HandleKey(XsrUiKey.Enter));
            XsrUiScene page = fixture.Shell.Render(size);
            AssertTrue(HasKey(fixture.Shell, page, key));
            AssertEqual(title, FindByKey(fixture.Shell, page, "TitleSubpage").Text);
            AssertFalse(HasKey(fixture.Shell, page, "TitleMain"));
            AssertFalse(HasKey(fixture.Shell, page, "SubpageNavigation"));
            AssertContains(page.Nodes.Single(node => node.Role == XsrUiSemanticRole.TitleBar).Rect,
                FindByKey(fixture.Shell, page, "TitleBack").Rect);
            AssertFalse(HasKey(fixture.Shell, page, "LaunchButton"));
            AssertEqual(2, fixture.Shell.Stage.Navigation.Depth);
            AssertTrue(FindByKey(fixture.Shell, page, "TitleBack").IsFocusVisible);
            AssertTrue(fixture.Shell.Renderer.HandleKey(XsrUiKey.Enter));
            AssertEqual(source, fixture.Shell.Renderer.Focused);
            AssertEqual(1, fixture.Shell.Stage.Navigation.Depth);
            AssertTrue(HasKey(fixture.Shell, fixture.Shell.Render(size), "LaunchPage"));
        }

        XsrUiScene scene = fixture.Shell.Render(size);
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "InstanceSettings").Entity));
        scene = fixture.Shell.Render(size);
        Emit(fixture.Intents, "ui.launch.modify");
        scene = fixture.Shell.Render(size);
        AssertTrue(HasKey(fixture.Shell, scene, "VersionModifyPage"));
        AssertFalse(HasKey(fixture.Shell, scene, "VersionSettingsPage"));
        AssertEqual(3, fixture.Shell.Stage.Navigation.Depth);
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "TitleBack").Entity));
        scene = fixture.Shell.Render(size);
        AssertTrue(HasKey(fixture.Shell, scene, "VersionSettingsPage"));
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "TitleBack").Entity));
        AssertEqual(1, fixture.Shell.Stage.Navigation.Depth);
        AssertTrue(HasKey(fixture.Shell, fixture.Shell.Render(size), "LaunchPage"));

        Emit(fixture.Intents, "ui.launch.modify");
        Emit(fixture.Intents, "ui.navigation.settings");
        AssertEqual(1, fixture.Shell.Stage.Navigation.Depth);
        AssertFalse(HasKey(fixture.Shell, fixture.Shell.Render(size), "VersionModifyPage"));
    }

    private static void PointerFocusDoesNotDrawKeyboardFocusRings()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([Instance("playable")]), addProfile: true);
        XsrUiSize size = new(850, 500);
        XsrUiSceneNode modify = FindByKey(fixture.Shell, fixture.Shell.Render(size), "InstanceModify");
        XsrUiPoint point = new(modify.Rect.X + modify.Rect.Width - 18, modify.Rect.Y + 18);
        AssertTrue(fixture.Shell.Renderer.PointerPressed(point));
        XsrUiSceneNode pressed = FindByKey(fixture.Shell, fixture.Shell.Render(size), "InstanceModify");
        AssertTrue(pressed.IsFocused);
        AssertFalse(pressed.IsFocusVisible);
        AssertTrue(fixture.Shell.Renderer.PointerReleased(point));
        XsrUiScene page = fixture.Shell.Render(size);
        AssertFalse(FindByKey(fixture.Shell, page, "TitleBack").IsFocusVisible);
        AssertTrue(fixture.Shell.Renderer.HandleKey(XsrUiKey.Tab));
        AssertTrue(fixture.Shell.Render(size).Nodes.Single(node => node.IsFocused).IsFocusVisible);
    }

    private static void CapsulesOccupyPresentedWidth()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([Instance("1.21.1")]), addProfile: true);
        fixture.Shell.Renderer.ReducedMotion = false;
        foreach (XsrUiSize size in new[] { new XsrUiSize(810, 470), new XsrUiSize(850, 500), new XsrUiSize(1280, 800) })
        {
            XsrUiScene scene = fixture.Shell.Render(size);
            XsrUiEntityId modify = FindByKey(fixture.Shell, scene, "InstanceModify").Entity;
            XsrUiEntityId settings = FindByKey(fixture.Shell, scene, "InstanceSettings").Entity;
            AssertEqual("设置", FindByKey(fixture.Shell, scene, "InstanceSettings").Text);
            AssertEqual("lucide/settings", FindByKey(fixture.Shell, scene, "InstanceSettings").ImageSource);
            foreach (double progress in new[] { 0d, .5, 1, .5, 0 })
            {
                fixture.Shell.Renderer.SetCapsulePresentationProgress(modify, progress);
                fixture.Shell.Renderer.SetCapsulePresentationProgress(settings, progress);
                scene = fixture.Shell.Render(size);
                XsrUiRect name = FindByKey(fixture.Shell, scene, "VersionName").Rect;
                XsrUiRect left = FindByKey(fixture.Shell, scene, "InstanceModify").Rect;
                XsrUiRect right = FindByKey(fixture.Shell, scene, "InstanceSettings").Rect;
                AssertClose(36 + 36 * progress, left.Width);
                AssertClose(36 + 36 * progress, right.Width);
                AssertClose(left.Y + 18, name.Y + name.Height / 2);
                AssertClose(left.Y, right.Y);
                AssertClose(left.X + left.Width + 8, right.X);
                AssertTrue(name.Width > 0 && name.X + name.Width <= left.X);
                AssertContains(FindByKey(fixture.Shell, scene, "InstanceRow").Rect, right);
            }
            // The empty expanded-width space must not be an invisible button hit target.
            XsrUiRect collapsed = FindByKey(fixture.Shell, scene, "InstanceModify").Rect;
            AssertFalse(fixture.Shell.Renderer.PointerPressed(new XsrUiPoint(collapsed.X - 3, collapsed.Y + 18)));
        }
    }

    private static void AssertContains(XsrUiRect outer, XsrUiRect inner)
    {
        const double tolerance = .001;
        if (inner.X < outer.X - tolerance || inner.Y < outer.Y - tolerance
            || inner.X + inner.Width > outer.X + outer.Width + tolerance
            || inner.Y + inner.Height > outer.Y + outer.Height + tolerance)
            throw new InvalidOperationException($"Rectangle {inner} escapes {outer}.");
    }
}
