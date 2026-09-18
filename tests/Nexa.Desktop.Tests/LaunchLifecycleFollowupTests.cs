using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft.Crash;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Process;
using Nexa.UI.Next;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void JavaChoiceAndLaunchProgressStayInteractive()
    {
        var recording = new RecordingStartRoute { Hang = true };
        using var fixture = ComposeLaunchOverlayFixture(recording);
        fixture.Shell.Renderer.ReducedMotion = true;
        SelectFirstAccountAndLaunch(fixture, recording);
        Emit(fixture.Intents, "ui.page.back");
        new MinecraftLaunchProgressPublisher(fixture.Store).Report(new("get_java", 0.4));
        var scene = fixture.Shell.Render(new(850, 500));
        AssertFalse(FindByKey(fixture.Shell, scene, "LaunchButton").IsClickable);
        AssertEqual("正在启动…", FindByKey(fixture.Shell, scene, "LaunchButtonText").Text);
        AssertTrue(HasKey(fixture.Shell, scene, "LaunchButtonProgress"));
        AssertFalse(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "LaunchButton").Entity));
        Emit(fixture.Intents, "ui.launch.restore");
        new MinecraftLaunchProgressPublisher(fixture.Store).RequestAcquisition("java-runtime-gamma", 17, Array.AsReadOnly<int>([17, 21]));
        scene = fixture.Shell.Render(new(850, 500));
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "DialogAlternate").Entity));
        scene = fixture.Shell.Render(new(850, 500));
        AssertTrue(HasKey(fixture.Shell, scene, "JavaChoice17"));
        AssertFalse(HasKey(fixture.Shell, scene, "JavaChoice8"));
        AssertEqual("选择 Java 版本", FindByKey(fixture.Shell, scene, "TitleSubpage").Text);
        scene = fixture.Shell.Render(new(850, 500));
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "JavaChoice17").Entity));
        AssertTrue(SpinWait.SpinUntil(() => recording.LastJavaMajor == 17, TimeSpan.FromSeconds(2)));
        fixture.Shell.Render(new(850, 500));
    }

    private static void VersionRowActionsKeepSelectionDistinct()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([Instance("chosen"), Instance("other")]), addProfile: true);
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        fixture.Shell.Renderer.ReducedMotion = true;
        var scene = fixture.Shell.Render(new(850, 500));
        fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "InstanceListButton").Entity);
        scene = fixture.Shell.Render(new(850, 500));
        var tick = FindByKey(fixture.Shell, scene, "LibraryRowCheck:version:chosen");
        var name = FindByKey(fixture.Shell, scene, "LibraryRowName:version:chosen");
        var icon = FindByKey(fixture.Shell, scene, "LibraryRowIcon:version:chosen");
        AssertTrue(tick.Rect.X + tick.Rect.Width <= icon.Rect.X);
        AssertEqual(icon.Rect.X, FindByKey(fixture.Shell, scene, "LibraryRowIcon:version:other").Rect.X);
        AssertEqual(name.Rect.X, FindByKey(fixture.Shell, scene, "LibraryRowName:version:other").Rect.X);
        AssertFalse(HasKey(fixture.Shell, scene, "LibraryRowCheck:version:other"));
        AssertFalse(HasKey(fixture.Shell, scene, "LibraryRowSelected:version:chosen"));
        foreach (string action in new[] { "Modify", "Settings", "Delete" })
            AssertEqual(32d, FindByKey(fixture.Shell, scene, "LibraryRow" + action + ":version:chosen").Rect.Height);
        var settings = FindByKey(fixture.Shell, scene, "LibraryRowSettings:version:chosen");
        AssertEqual(32d, settings.Rect.Width);
        var point = new XsrUiPoint(settings.Rect.X + 15, settings.Rect.Y + 15);
        fixture.Shell.Renderer.PointerMoved(point);
        scene = fixture.Shell.Render(new(850, 500));
        settings = FindByKey(fixture.Shell, scene, "LibraryRowSettings:version:chosen");
        AssertEqual(76d, settings.Rect.Width);
        point = new(settings.Rect.X + settings.Rect.Width / 2, settings.Rect.Y + 15);
        AssertTrue(fixture.Shell.Renderer.PointerPressed(point));
        AssertTrue(fixture.Shell.Renderer.PointerReleased(point));
        fixture.Controller.Versions.WaitUntilIdle().GetAwaiter().GetResult();
        scene = fixture.Shell.Render(new(850, 500));
        AssertEqual("版本设置", FindByKey(fixture.Shell, scene, "TitleSubpage").Text);
    }

    private static void ProcessControlsProjectServiceFacts()
    {
        var recording = new RecordingStartRoute();
        using var fixture = ComposeLaunchOverlayFixture(recording);
        fixture.Shell.Renderer.ReducedMotion = true;
        var id = Guid.NewGuid();
        var sessions = fixture.Store.Resolve(MinecraftProcessStateComposition.SessionsKey);
        var session = new MinecraftProcessSnapshot(id, "playable", 123, MinecraftProcessState.Running, null, DateTimeOffset.UtcNow, null);
        fixture.Store.PublishDelta(sessions, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(0, [session], []));
        var scene = fixture.Shell.Render(new(850, 500));
        var power = FindByKey(fixture.Shell, scene, "process-stop-" + id);
        AssertFalse(FindByKey(fixture.Shell, scene, "process-logs-" + id).IsClickable);
        AssertTrue(fixture.Shell.Renderer.Activate(power.Entity));
        scene = fixture.Shell.Render(new(850, 500));
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "DialogAccept").Entity));
        AssertTrue(SpinWait.SpinUntil(() => recording.LastCancelledSession == id, TimeSpan.FromSeconds(2)));
        fixture.Store.PublishDelta(sessions, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(1, [session with { State = MinecraftProcessState.Failed }], []));
        var failures = fixture.Store.Resolve(MinecraftProcessStateComposition.FailuresKey);
        fixture.Store.PublishDelta(failures, new XsrCollectionDelta<MinecraftProcessFailure, Guid>(0,
            [new(id, "playable", new MinecraftLaunchFaultReport { Code = MinecraftLaunchFaultCode.OutOfMemory, Message = "java heap space" })], []));
        scene = fixture.Shell.Render(new(850, 500));
        AssertFalse(HasKey(fixture.Shell, scene, "process-stop-" + id));
        scene = fixture.Shell.Render(new(850, 500));
        AssertTrue(FindByKey(fixture.Shell, scene, "DialogMessage").Text!.Contains("游戏内存不足", StringComparison.Ordinal));
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "DialogAccept").Entity));
        AssertFalse(HasKey(fixture.Shell, fixture.Shell.Render(new(850, 500)), "DialogCard"));
    }
    private static void BubblesShareVerticalDockAndReleaseHiddenSlots()
    {
        using var fixture = ComposeLaunchOverlayFixture(new RecordingStartRoute());
        using DesktopTaskBubblePresenter bubble = new(fixture.Shell, fixture.Store);
        var id = Guid.NewGuid();
        var sessions = fixture.Store.Resolve(MinecraftProcessStateComposition.SessionsKey);
        var session = new MinecraftProcessSnapshot(id, "playable", 123, MinecraftProcessState.Running, null, DateTimeOffset.UtcNow, null);
        fixture.Store.PublishDelta(sessions, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(0, [session], []));
        var scene = fixture.Shell.Render(new(850, 500));
        var power = FindByKey(fixture.Shell, scene, "process-stop-" + id);
        var logs = FindByKey(fixture.Shell, scene, "process-logs-" + id);
        AssertEqual(434d, power.Rect.Y);
        AssertEqual(power.Rect.X, logs.Rect.X);
        AssertEqual(power.Rect.Y - 58, logs.Rect.Y);
        AssertEqual(XsrUiOverlayMotionKind.Notification, power.OverlayMotion);
        AssertEqual(XsrUiOverlayMotionKind.Notification, logs.OverlayMotion);
        var style = fixture.Shell.Tree.GetComponent<XsrUiVisualStyle>(power.Entity)!;
        AssertEqual(XsrUiSurfaceKind.Solid, style.Surface);
        AssertEqual(DesktopUiPalette.CapsuleBackground, style.Background);
        var image = fixture.Shell.Tree.Children(power.Entity).Single();
        AssertEqual(DesktopUiPalette.CapsuleForeground, fixture.Shell.Tree.GetComponent<XsrUiVisualStyle>(image)!.Foreground);
        using var task = fixture.Foundation.Host.Tasks.Begin(new Nexa.Services.Tasks.TaskCenterStart("dock-test", "下载", ["文件"]));
        scene = fixture.Shell.Render(new(850, 500));
        var root = FindByKey(fixture.Shell, scene, "task-bubble");
        AssertEqual(root.Rect.X, FindByKey(fixture.Shell, scene, "process-stop-" + id).Rect.X);
        AssertEqual(root.Rect.Y - 58, FindByKey(fixture.Shell, scene, "process-stop-" + id).Rect.Y);
        bubble.SetPageVisible(true);
        scene = fixture.Shell.Render(new(850, 500));
        AssertTrue(FindByKey(fixture.Shell, scene, "task-bubble").IsOverlayClosing);
        AssertEqual(434d, FindByKey(fixture.Shell, scene, "process-stop-" + id).Rect.Y);
        fixture.Store.PublishDelta(sessions, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(1, [session with { State = MinecraftProcessState.Exited }], []));
        scene = fixture.Shell.Render(new(850, 500));
        AssertTrue(FindByKey(fixture.Shell, scene, "process-stop-" + id).IsOverlayClosing);
        AssertFalse(FindByKey(fixture.Shell, scene, "process-stop-" + id).IsClickable);
    }

}
