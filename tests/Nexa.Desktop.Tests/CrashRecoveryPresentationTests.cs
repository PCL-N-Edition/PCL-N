using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft.Crash;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.Process;
using Nexa.UI.Next;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void CrashChangesPagesRemainBoundedAndLateUpdatesCannotReopenDialog()
    {
        using var fixture = ComposeLaunchOverlayFixture(new RecordingStartRoute());
        fixture.Shell.Renderer.ReducedMotion = true;
        var feedback = fixture.Feedback;
        AssertTrue(feedback.TryShowMessageDialog("crash", "游戏异常退出", "原因", "知道了", out var id));
        var changes = Enumerable.Range(0, 25).Select(i => new InstanceRecoveryChange(
            InstanceRecoveryChangeKind.Modified, "模组", $"mods/{i:D2}.jar")).ToArray();
        var report = new InstanceRecoveryReport("instance", Guid.NewGuid(), DateTimeOffset.UtcNow, changes, "digest");
        new CrashChangesPresentation(feedback, id, "原始错误", report).Show();
        var first = feedback.Snapshot().Dialog!;
        AssertTrue(first.Message.Contains("25 项（1/3）"));
        AssertTrue(first.Message.Contains("mods/11.jar"));
        AssertFalse(first.Message.Contains("mods/12.jar"));
        var scene = fixture.Shell.Render(new(850, 500));
        var viewport = FindByKey(fixture.Shell, scene, "DialogMessageViewport");
        var scroll = fixture.Shell.Tree.GetComponent<XsrUiScroll>(viewport.Entity)!;
        scroll.OffsetY = 100;
        AssertTrue(feedback.InvokeDialogAlternate(id));
        scene = fixture.Shell.Render(new(850, 500));
        AssertEqual(0d, scroll.OffsetY);
        var accept = FindByKey(fixture.Shell, scene, "DialogAccept");
        AssertTrue(accept.Rect.Y + accept.Rect.Height <= 500);
        AssertTrue(FindByKey(fixture.Shell, scene, "DialogAlternate").IsClickable);
        AssertTrue(feedback.Snapshot().Dialog!.Message.Contains("mods/23.jar"));
        AssertTrue(feedback.InvokeDialogAlternate(id));
        AssertTrue(feedback.Snapshot().Dialog!.Message.Contains("mods/24.jar"));
        AssertTrue(feedback.InvokeDialogAlternate(id));
        AssertEqual(first.Message, feedback.Snapshot().Dialog!.Message);
        AssertTrue(feedback.ResolveDialog(id, true));
        AssertTrue(feedback.TryShowMessageDialog("crash", "新错误", "新内容", "知道了", out var replacement));
        first.Alternate!();
        AssertFalse(feedback.TryUpdateMessageDialog(id, "过期结果"));
        AssertEqual(replacement, feedback.Snapshot().Dialog!.Id);
        AssertEqual("新内容", feedback.Snapshot().Dialog!.Message);
        feedback.ResolveDialog(replacement, true);
        first.Alternate!();
        AssertTrue(feedback.Snapshot().Dialog is null);
    }

    private static void CrashChangesQueryUsesFailedProcessInstance()
    {
        using var fixture = ComposeLaunchOverlayFixture(new RecordingStartRoute());
        fixture.Shell.Renderer.ReducedMotion = true;
        var failureId = Guid.NewGuid();
        var state = fixture.Store.Resolve(MinecraftProcessStateComposition.FailuresKey);
        // No baseline at this independent root. The currently selected instance is irrelevant.
        string instance = Path.Combine(fixture.TemporaryDirectory, "other-root", "versions", "crashed");
        fixture.Store.PublishDelta(state, new XsrCollectionDelta<MinecraftProcessFailure, Guid>(0,
            [new(failureId, "crashed", new MinecraftLaunchFaultReport { Message = "original crash" })
                { InstanceDirectory = instance }], []));
        fixture.Shell.Render(new(850, 500));
        AssertTrue(SpinWait.SpinUntil(() => fixture.Feedback.Snapshot().Dialog?.Message
            .Contains("尚无成功运行的快照", StringComparison.Ordinal) == true, TimeSpan.FromSeconds(5)));
        var dialog = fixture.Feedback.Snapshot().Dialog!;
        AssertTrue(dialog.Message.Contains("original crash"));
        AssertEqual("crashed · 游戏异常退出", dialog.Title);
        AssertFalse(Directory.Exists(instance));
        fixture.Feedback.ResolveDialog(dialog.Id, true);
        AssertFalse(HasKey(fixture.Shell, fixture.Shell.Render(new(850, 500)), "DialogCard"));
    }
}
