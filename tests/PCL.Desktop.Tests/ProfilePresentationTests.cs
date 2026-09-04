using PCL.Desktop.Ui;
using PCL.Services.Accounts;
using PCL.UI.Next;

namespace PCL.Desktop.Tests;

internal static partial class Program
{
    private static void ProfilePresentationUsesAppleHierarchy()
    {
        AssertEqual("pcl/avatar/steve", LaunchProfilePresentation.Avatar(""));
        AssertEqual("pcl/avatar/steve", LaunchProfilePresentation.Avatar("00000000-0000-0000-0000-000000000000"));
        AssertEqual("pcl/avatar/alex", LaunchProfilePresentation.Avatar("00000000-0000-0000-0000-000000000001"));
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]), addProfile: true);
        AssertTrue(fixture.Service.AddProfile(new LaunchProfile
        {
            Username = "Second",
            Kind = LaunchProfileKind.ThirdParty,
            Uuid = "00000000-0000-0000-0000-000000000001",
            AuthServer = "https://example.org/authlib-injector",
        }).IsSuccess);
        XsrUiShell shell = fixture.Shell;
        foreach (XsrUiShellStyle style in Enum.GetValues<XsrUiShellStyle>())
        {
            shell.SetStyle(style);
            XsrUiScene scene = shell.Render(new XsrUiSize(810, 470));
            AssertFalse(scene.Nodes.Any(node => node.Text == "就绪" || node.Text == "账户已就绪，可以开始游戏。"));
            XsrUiSceneNode avatar = FindByKey(shell, scene, "AccountAvatar");
            AssertEqual("pcl/avatar/steve", avatar.ImageSource);
            AssertClose(72, avatar.Rect.Width);
            AssertClose(72, avatar.Rect.Height);
            XsrUiRect body = FindByKey(shell, scene, "AccountSelected").Rect;
            XsrUiRect identity = FindByKey(shell, scene, "AccountIdentity").Rect;
            AssertClose(body.Y + body.Height / 2, identity.Y + identity.Height / 2);
            AssertContains(body, identity);
            AssertEqual(22d, FindByKey(shell, scene, "AccountName").VisualStyle.FontSize);
            XsrUiSceneNode change = FindByKey(shell, scene, "AccountSwitch");
            AssertClose(112, change.Rect.Width);
            AssertClose(36, change.Rect.Height);
            XsrUiRect icon = FindByKey(shell, scene, "AccountSwitchIcon").Rect;
            AssertClose(icon.X + icon.Width + 6, FindByKey(shell, scene, "AccountSwitchText").Rect.X);
            AssertEqual("切换档案", FindByKey(shell, scene, "AccountSwitchText").Text);
            AssertEqual("切换档案", change.Label);
            AssertTrue(shell.Renderer.Focus(change.Entity));
            AssertTrue(shell.Renderer.HandleKey(XsrUiKey.Enter));
            scene = shell.Render(new XsrUiSize(810, 470));
            AssertTrue(HasKey(shell, scene, "AccountBack"));
            AssertEqual("切换档案", FindByKey(shell, scene, "AccountHeader").Text);
            AssertFalse(HasKey(shell, scene, "AccountHint"));
            AssertFalse(HasKey(shell, scene, "AccountPickerTitle"));
            AssertTrue(HasKey(shell, scene, "ProfileCheck:0"));
            AssertFalse(HasKey(shell, scene, "ProfileCheck:1"));
            AssertEqual(FindByKey(shell, scene, "account-row:0").Entity, shell.Renderer.Focused);
            XsrUiSceneNode row = FindByKey(shell, scene, "account-row:1");
            AssertClose(56, row.Rect.Height);
            AssertEqual("pcl/avatar/alex", FindByKey(shell, scene, "ProfileAvatar:1").ImageSource);
            AssertEqual("第三方 · example.org", FindByKey(shell, scene, "ProfileDetail:1").Text);
            AssertTrue(shell.Renderer.Focus(row.Entity));
            AssertTrue(shell.Renderer.HandleKey(XsrUiKey.Enter));
            scene = shell.Render(new XsrUiSize(810, 470));
            AssertEqual("Second", FindByKey(shell, scene, "AccountName").Text);
            AssertEqual("第三方 · example.org", FindByKey(shell, scene, "AccountKind").Text);
            AssertEqual("pcl/avatar/alex", FindByKey(shell, scene, "AccountAvatar").ImageSource);
            AssertEqual(change.Entity, shell.Renderer.Focused);
            AssertTrue(FindByKey(shell, scene, "AccountSwitch").IsFocusVisible);
            AssertTrue(shell.Renderer.Activate(change.Entity));
            scene = shell.Render(new XsrUiSize(810, 470));
            AssertTrue(shell.Renderer.Activate(FindByKey(shell, scene, "AccountBack").Entity));
            scene = shell.Render(new XsrUiSize(810, 470));
            AssertEqual("Second", FindByKey(shell, scene, "AccountName").Text);
            AssertFalse(HasKey(shell, scene, "AccountRows"));
            AssertTrue(fixture.Service.SelectProfile(0) is null);
            _ = shell.Render(new XsrUiSize(810, 470));
        }
    }

    private static void OperationalFeedbackRemainsBesideLaunchAction()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([Instance("available")]));
        XsrUiSize size = new(810, 470);
        AssertFalse(HasKey(fixture.Shell, fixture.Shell.Render(size), "LaunchFeedback"));
        // A rejected product intent must still produce useful feedback, never an idle widget footer.
        Emit(fixture.Intents, "ui.launch.primary");
        XsrUiScene scene = fixture.Shell.Render(size);
        XsrUiSceneNode feedback = FindByKey(fixture.Shell, scene, "LaunchFeedback");
        AssertTrue(feedback.Text!.Contains("账户档案", StringComparison.Ordinal));
        AssertContains(FindByKey(fixture.Shell, scene, "CardVersion").Rect, feedback.Rect);
        AssertFalse(HasKey(fixture.Shell, scene, "LaunchStatus"));
        AssertFalse(HasKey(fixture.Shell, scene, "AccountSummary"));
    }
}
