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
        Console.WriteLine("[diag] depth=" + fixture.Shell.Stage.Navigation.Depth + " keys=" + string.Join(",", scene.Nodes
            .Select(n => fixture.Shell.Tree.Name(n.Entity))
            .Where(n => n.Length > 0)));
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
        MinecraftLaunchProgressSnapshot current = fixture.Store
            .ReadAppliedValue(fixture.Store.Resolve(MinecraftLaunchProgressState.SnapshotKey))
            is MinecraftLaunchProgressSnapshot snapshot ? snapshot : new MinecraftLaunchProgressSnapshot(true, "login", 0.25d, "offline", string.Empty, false, null);
        fixture.Store.Publish(
            fixture.Store.Resolve(MinecraftLaunchProgressState.SnapshotKey),
            current with { IsLaunched = true, Progress = 1d });
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
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            fixture.Shell.Render(new XsrUiSize(850, 500));
            return fixture.Shell.Stage.Navigation.Depth == 1;
        }, TimeSpan.FromSeconds(2)));
    }

    private static void LaunchOverlayPromptsBeforeJavaDownload()
    {
        RecordingStartRoute recording = new();
        using LaunchPageFixture fixture = ComposeLaunchOverlayFixture(recording);
        SelectFirstAccountAndLaunch(fixture, recording);

        // The pipeline pauses at the acquisition gate: the decision is presented by the shared
        // window-internal modal layer rather than embedded into the launching card.
        fixture.Store.Publish(fixture.Store.Resolve(MinecraftLaunchProgressState.AcquireComponentKey), "java-runtime-gamma");
        fixture.Store.Publish(fixture.Store.Resolve(MinecraftLaunchProgressState.AcquireMajorKey), 17);
        fixture.Store.Publish(fixture.Store.Resolve(MinecraftLaunchProgressState.AcquirePendingKey), true);
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(850, 500));
        AssertFalse(HasKey(fixture.Shell, scene, "LaunchingAcquirePrompt"));
        AssertTrue(FindByKey(fixture.Shell, scene, "DialogMessage").Text!
            .Contains("java-runtime-gamma", StringComparison.Ordinal));
        AssertTrue(HasKey(fixture.Shell, scene, "LaunchingHintBox"));
        AssertEqual(XsrUiSemanticRole.Dialog, FindByKey(fixture.Shell, scene, "DialogCard").Role);
        AssertRectClose(new XsrUiRect(0, 0, 850, 500), FindByKey(fixture.Shell, scene, "DialogLayer").Rect);
        AssertFalse(FindByKey(fixture.Shell, scene, "LaunchingCancelButton").IsAccessible);
        AssertFalse(FindByKey(fixture.Shell, scene, "LaunchingCancelButton").IsClickable);

        // Approving forwards the decision to the pipeline command.
        fixture.Shell.Renderer.ReducedMotion = true;
        AssertTrue(fixture.Shell.Renderer.Activate(
            FindByKey(fixture.Shell, scene, "DialogAccept").Entity));
        AssertTrue(SpinWait.SpinUntil(() => recording.LastDecision == true, TimeSpan.FromSeconds(2)));
        AssertFalse(HasKey(fixture.Shell, fixture.Shell.Render(new XsrUiSize(850, 500)), "DialogCard"));
    }

    private static void LaunchOverlayCancelHidesOverlay()
    {
        RecordingStartRoute recording = new();
        using LaunchPageFixture fixture = ComposeLaunchOverlayFixture(recording);
        SelectFirstAccountAndLaunch(fixture, recording);
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(850, 500));
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "LaunchingCancelButton").Entity));
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            fixture.Shell.Render(new XsrUiSize(850, 500));
            return fixture.Shell.Stage.Navigation.Depth == 1;
        }, TimeSpan.FromSeconds(2)));
    }
}
