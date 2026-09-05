using PCL.Desktop.Ui;
using PCL.Services.Accounts;
using PCL.UI.Next;

namespace PCL.Desktop.Tests;

internal static partial class Program
{
    private static void SkinRoutePublishesIntoRenderedProfile()
    {
        using Stream resource = typeof(PCL.UI.Next.Backend.Avalonia.AvaloniaUiShellHost).Assembly
            .GetManifestResourceStream("PCL.UI.Next.Backend.Avalonia.Assets.Avatars.Steve.png")!;
        using MemoryStream buffer = new(); resource.CopyTo(buffer);
        using ProfileSkinHttp handler = new();
        using HttpClient http = new(handler);
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]), accountHttp: http, enableSkins: true);
        AssertTrue(fixture.Service.AddProfile(new LaunchProfile
        {
            Username = "Skin fixture",
            Kind = LaunchProfileKind.Offline,
            SkinAddress = "https://skins.example/head.png",
        }).IsSuccess);
        XsrUiSize size = new(810, 470);
        XsrUiShell shell = fixture.Shell;
        XsrUiScene scene = shell.Render(size);
        XsrUiEntityId avatar = FindByKey(shell, scene, "AccountAvatar").Entity;
        AssertTrue(FindByKey(shell, scene, "AccountAvatar").RasterImage is null);
        AssertTrue(SpinWait.SpinUntil(() => Volatile.Read(ref handler.Calls) == 1, TimeSpan.FromSeconds(2)));
        // Completion may request a frame, but must never mutate the render-thread tree.
        handler.Result.SetResult(buffer.ToArray());
        fixture.Onboarding.Skins!.WhenIdle.GetAwaiter().GetResult();
        AssertTrue(shell.StateBridge!.PendingCount > 0);
        AssertEqual(XsrUiDirtyKinds.None, shell.Tree.DirtyKinds(avatar));
        AssertTrue(shell.Tree.GetComponent<XsrUiImage>(avatar)!.Raster is null);
        foreach (XsrUiShellStyle style in Enum.GetValues<XsrUiShellStyle>())
        {
            shell.SetStyle(style); scene = shell.Render(size);
            XsrUiRasterImage raster = FindByKey(shell, scene, "AccountAvatar").RasterImage!;
            AssertTrue(raster.Image.Bytes.Span.SequenceEqual(buffer.ToArray()));
            AssertEqual(2, raster.Layers.Count);
            AssertEqual(new XsrUiRect(8, 8, 8, 8), raster.Layers[0].Source);
            AssertEqual(new XsrUiRect(40, 8, 8, 8), raster.Layers[1].Source);
            AssertTrue(shell.Renderer.Activate(FindByKey(shell, scene, "AccountSwitch").Entity));
            scene = shell.Render(size);
            AssertEqual(raster.Image.Key, FindByKey(shell, scene, "ProfileAvatar:0").RasterImage!.Image.Key);
            AssertTrue(shell.Renderer.Activate(FindByKey(shell, scene, "AccountBack").Entity));
            scene = shell.Render(size);
        }
        AssertTrue(fixture.Service.AddProfile(new LaunchProfile { Username = "Default", Kind = LaunchProfileKind.Offline }).IsSuccess);
        AssertTrue(fixture.Service.SelectProfile(1) is null);
        scene = shell.Render(size);
        AssertTrue(FindByKey(shell, scene, "AccountAvatar").RasterImage is null);
        AssertEqual("pcl/avatar/steve", FindByKey(shell, scene, "AccountAvatar").ImageSource);
        fixture.Onboarding.Skins.WhenIdle.GetAwaiter().GetResult();
    }

    private sealed class ProfileSkinHttp : HttpMessageHandler
    {
        public int Calls;
        public TaskCompletionSource<byte[]> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            AssertEqual("https://skins.example/head.png", request.RequestUri!.AbsoluteUri);
            AssertTrue(request.Headers.Authorization is null);
            return new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(await Result.Task.WaitAsync(cancellationToken)) };
        }
    }

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
            AssertClose(36, change.Rect.Width);
            AssertClose(36, change.Rect.Height);
            AssertEqual("lucide/arrow-right-left", change.ImageSource);
            AssertEqual("切换档案", change.Text);
            AssertTrue(change.VisualStyle.HoverExpand);
            XsrUiSceneNode wardrobe = FindByKey(shell, scene, "AccountWardrobe");
            AssertEqual("lucide/shirt", wardrobe.ImageSource);
            AssertClose(36, wardrobe.Rect.Width);
            AssertClose(change.Rect.X + change.Rect.Width + 8, wardrobe.Rect.X);
            AssertEqual("切换档案", change.Label);
            AssertTrue(shell.Renderer.Focus(change.Entity));
            AssertTrue(shell.Renderer.HandleKey(XsrUiKey.Enter));
            scene = shell.Render(new XsrUiSize(810, 470));
            AssertTrue(HasKey(shell, scene, "AccountBack"));
            AssertEqual("切换档案", FindByKey(shell, scene, "AccountHeader").Text);
            AssertFalse(HasKey(shell, scene, "AccountHint"));
            AssertFalse(HasKey(shell, scene, "AccountPickerTitle"));
            AssertTrue(FindByKey(shell, scene, "account-row:0").IsSelected);
            AssertEqual("lucide/trash-2", FindByKey(shell, scene, "ProfileDelete:0").ImageSource);
            AssertTrue(FindByKey(shell, scene, "ProfileDelete:1").IsClickable);
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

    private static void OperationalFeedbackUsesLowerLeftNotification()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([Instance("available")]));
        XsrUiSize size = new(810, 470);
        AssertFalse(HasKey(fixture.Shell, fixture.Shell.Render(size), "LaunchFeedback"));
        // A rejected product intent uses the one shared window-internal lower-left surface,
        // never an ad-hoc status line inside a page card.
        Emit(fixture.Intents, "ui.launch.primary");
        XsrUiScene scene = fixture.Shell.Render(size);
        XsrUiSceneNode feedback = scene.Nodes.Single(node =>
            node.Role == XsrUiSemanticRole.Status
            && node.Label!.Contains("账户档案", StringComparison.Ordinal));
        AssertEqual("Warn", feedback.Label![..4]);
        AssertClose(XsrUiShell.CollapsedRailWidth + 18, feedback.Rect.X);
        AssertClose(size.Height - 18, feedback.Rect.Y + feedback.Rect.Height);
        AssertFalse(FindByKey(fixture.Shell, scene, "CardVersion").Rect.Contains(
            new XsrUiPoint(feedback.Rect.X, feedback.Rect.Y)));
        AssertFalse(HasKey(fixture.Shell, scene, "LaunchStatus"));
        AssertFalse(HasKey(fixture.Shell, scene, "LaunchFeedback"));
        AssertFalse(HasKey(fixture.Shell, scene, "AccountSummary"));
    }

    private static void AccountCapsulesAndWardrobeRoutePreserveGeometry()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]), addProfile: true);
        XsrUiShell shell = fixture.Shell;
        XsrUiSize size = new(810, 470);
        XsrUiScene scene = shell.Render(size);
        XsrUiSceneNode change = FindByKey(shell, scene, "AccountSwitch");
        XsrUiPoint anchor = new(change.Rect.X + change.Rect.Width / 2, change.Rect.Y + change.Rect.Height / 2);
        foreach (double progress in new[] { 0, .2, .5, .8, 1 })
        {
            shell.Renderer.SetCapsulePresentationProgress(change.Entity, progress);
            scene = shell.Render(size);
            XsrUiRect button = FindByKey(shell, scene, "AccountSwitch").Rect;
            AssertTrue(button.Contains(anchor));
            AssertContains(FindByKey(shell, scene, "CardAccount").Rect, button);
            AssertClose(button.X + button.Width + 8, FindByKey(shell, scene, "AccountWardrobe").Rect.X);
        }
        XsrUiSceneNode wardrobe = FindByKey(shell, scene, "AccountWardrobe");
        shell.Renderer.SetCapsulePresentationProgress(wardrobe.Entity, 1);
        scene = shell.Render(size);
        AssertContains(FindByKey(shell, scene, "CardAccount").Rect, FindByKey(shell, scene, "AccountActions").Rect);
        AssertTrue(shell.Renderer.Activate(wardrobe.Entity));
        scene = shell.Render(size);
        AssertTrue(HasKey(shell, scene, "AccountWardrobePage"));
        AssertEqual("更衣橱", FindByKey(shell, scene, "TitleSubpage").Text);
        AssertTrue(FindByKey(shell, scene, "TitleSubpage").Rect.Height >= 28);
        AssertTrue(shell.Renderer.Activate(FindByKey(shell, scene, "TitleBack").Entity));
        scene = shell.Render(size);
        AssertEqual(wardrobe.Entity, shell.Renderer.Focused);
        AssertTrue(FindByKey(shell, scene, "AccountHeader").Rect.Height >= 26);
    }
}
