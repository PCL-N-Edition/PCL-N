using PCL.Desktop.Ui;
using PCL.Services.Accounts;
using PCL.Services.Composition;
using PCL.Services.Minecraft;
using PCL.Services.Minecraft.Launch;
using PCL.UI.Next;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Desktop.Tests;

// XSR-712: the launching page replicates the legacy launching card as its own navigation
// page — reset facts on entry, narration from the launch progress cells, and the page
// closing (navigation pop) on failure or cancel.
internal static partial class Program
{
    private static LaunchPageFixture ComposeLaunchOverlayFixture(RecordingStartRoute recording)
    {
        using MinecraftRuntime runtime = CreateRecordingRuntime(recording);
        LaunchPageFixture fixture = new(new ImmediateInstanceSource([Instance("playable")]), runtime,
            addProfile: true, ownsMinecraftRuntime: false);
        AssertTrue(fixture.Service.AddProfile(new LaunchProfile { Username = "Selected" }).IsSuccess);
        return fixture;
    }

    private static void SelectFirstAccountAndLaunch(LaunchPageFixture fixture, RecordingStartRoute recording)
    {
        Emit(fixture.Intents, "ui.account.switch");
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(850, 500));
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "account-row:1").Entity));
        AssertTrue(SpinWait.SpinUntil(() => fixture.Service.SelectedIndex == 1, TimeSpan.FromSeconds(2)));
        scene = fixture.Shell.Render(new XsrUiSize(850, 500));
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "LaunchButton").Entity));
        AssertTrue(SpinWait.SpinUntil(() => recording.LastCommand is not null, TimeSpan.FromSeconds(2)));
    }

    private static void LaunchOverlayShowsResetFactsWhenLaunchStarts()
    {
        RecordingStartRoute recording = new();
        using LaunchPageFixture fixture = ComposeLaunchOverlayFixture(recording);
        SelectFirstAccountAndLaunch(fixture, recording);
        AssertTrue(SpinWait.SpinUntil(
            () => fixture.Shell.Stage.Navigation.Depth == 2, TimeSpan.FromSeconds(2)));
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(850, 500));
        AssertEqual("正在启动", FindByKey(fixture.Shell, scene, "LaunchingTitle").Text);
        AssertEqual("初始化", FindByKey(fixture.Shell, scene, "LaunchingStageValue").Text);
        AssertEqual("等待账户档案", FindByKey(fixture.Shell, scene, "LaunchingMethodValue").Text);
        AssertEqual("0%", FindByKey(fixture.Shell, scene, "LaunchingPercentValue").Text);
        AssertFalse(HasKey(fixture.Shell, scene, "LaunchingSpeedRow"));
        AssertTrue(HasKey(fixture.Shell, scene, "LaunchingCancelButton"));
    }

    private static void LaunchOverlayNarratesProgressCells()
    {
        RecordingStartRoute recording = new()
        {
            ProgressStore = null,
        };
        using LaunchPageFixture fixture = ComposeLaunchOverlayFixture(recording);
        recording.ProgressStore = fixture.Store;
        recording.Stage = "login";
        recording.StageProgress = 0.25d;
        recording.Method = "offline";
        SelectFirstAccountAndLaunch(fixture, recording);
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(850, 500));
        AssertEqual("登录", FindByKey(fixture.Shell, scene, "LaunchingStageValue").Text);
        AssertEqual("25%", FindByKey(fixture.Shell, scene, "LaunchingPercentValue").Text);
        AssertEqual("离线模式", FindByKey(fixture.Shell, scene, "LaunchingMethodValue").Text);

        // The fill bar layout follows the renderer-presented fraction, not the raw state:
        // the backend drives the presented value, and the test drives it directly here.
        XsrUiSceneNode fill = FindByKey(fixture.Shell, scene, "LaunchProgressFill");
        XsrUiSceneNode track = FindByKey(fixture.Shell, scene, "LaunchProgressTrack");
        fixture.Shell.Renderer.SetProgressPresentation(fill.Entity, 0.25d);
        scene = fixture.Shell.Render(new XsrUiSize(850, 500));
        fill = FindByKey(fixture.Shell, scene, "LaunchProgressFill");
        AssertClose(track.Rect.Width * 0.25d, fill.Rect.Width);

        // The launched report switches the title to the legacy launched wording.
        fixture.Store.Publish(fixture.Store.Resolve(MinecraftLaunchProgressState.LaunchedKey), true);
        scene = fixture.Shell.Render(new XsrUiSize(850, 500));
        AssertEqual("游戏已启动", FindByKey(fixture.Shell, scene, "LaunchingTitle").Text);
    }

    private static void LaunchOverlayClosesOnFailure()
    {
        RecordingStartRoute recording = new()
        {
            Outcome = XsrResult.Failure(MinecraftErrors.LaunchFailed("the demo launch failed.")),
        };
        using LaunchPageFixture fixture = ComposeLaunchOverlayFixture(recording);
        SelectFirstAccountAndLaunch(fixture, recording);
        AssertTrue(SpinWait.SpinUntil(
            () => fixture.Shell.Stage.Navigation.Depth == 1, TimeSpan.FromSeconds(2)));
    }

    private static void LaunchOverlayCancelHidesOverlay()
    {
        RecordingStartRoute recording = new();
        using LaunchPageFixture fixture = ComposeLaunchOverlayFixture(recording);
        SelectFirstAccountAndLaunch(fixture, recording);
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(850, 500));
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "LaunchingCancelButton").Entity));
        AssertTrue(SpinWait.SpinUntil(
            () => fixture.Shell.Stage.Navigation.Depth == 1, TimeSpan.FromSeconds(2)));
    }
}
