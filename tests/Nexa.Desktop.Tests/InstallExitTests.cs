using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Tasks;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void InstallExitFailureAllowsRetryAfterWorkerBecomesTerminal()
    {
        XsrStateStoreBuilder states = new(); TaskCenterStateContract.DeclareState(states);
        var store = states.Build(); var tasks = new TaskCenterService(store);
        using var task = tasks.Begin(new("install-recovery:queue:test", "继续安装与修改", ["下载"]));
        using var feedback = new DesktopFeedbackService();
        using var closed = new ManualResetEventSlim();
        int attempts = 0;
        bool? retryPause = null;
        XsrCommandRouterBuilder builder = new();
        builder.Register<MinecraftInstallStopCommand>(MinecraftInstallRoutes.Stop, (command, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                task.Fail("回滚冲突");
                return ValueTask.FromResult(XsrResult.Failure(XsrRuntimeErrors.HandlerFaulted()));
            }
            retryPause = command.Pause;
            return ValueTask.FromResult(XsrResult.Success());
        });
        var controller = new DesktopInstallExitCoordinator(store, builder.Build(new NoopDispatchObserver()), feedback, closed.Set);
        AssertFalse(controller.CanClose());
        feedback.InvokeDialogAlternate(feedback.Snapshot().Dialog!.Id);
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            controller.CanClose();
            return feedback.Snapshot().Dialog?.Key == "install.exit.retry";
        }, TimeSpan.FromSeconds(5)));
        AssertFalse(closed.IsSet);
        feedback.ResolveDialog(feedback.Snapshot().Dialog!.Id, true);
        AssertTrue(closed.Wait(TimeSpan.FromSeconds(5)));
        AssertEqual(false, retryPause);
        AssertEqual(2, attempts);
    }

    private static void InstallExitWaitsForStopAndPreservesStayChoice()
    {
        foreach (bool requestPause in new[] { true, false })
        {
            XsrStateStoreBuilder states = new(); TaskCenterStateContract.DeclareState(states);
            var store = states.Build(); var tasks = new TaskCenterService(store);
            using var task = tasks.Begin(new("install:exit", "修改版本 test", ["下载"]));
            using var feedback = new DesktopFeedbackService();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var closed = new ManualResetEventSlim();
            bool? pause = null;
            XsrCommandRouterBuilder builder = new();
            builder.Register<MinecraftInstallStopCommand>(MinecraftInstallRoutes.Stop, async (command, token) =>
            {
                pause = command.Pause; entered.Set();
                await Task.Run(() => release.Wait(token), token);
                return XsrResult.Success();
            });
            var controller = new DesktopInstallExitCoordinator(store, builder.Build(new NoopDispatchObserver()), feedback, closed.Set);
            AssertFalse(controller.CanClose());
            var first = feedback.Snapshot().Dialog!;
            AssertTrue(first.AlternateLabel!.Contains("回滚", StringComparison.Ordinal));
            feedback.ResolveDialog(first.Id, false);
            AssertFalse(entered.IsSet);
            AssertFalse(controller.CanClose());
            if (requestPause) feedback.ResolveDialog(feedback.Snapshot().Dialog!.Id, true);
            else feedback.InvokeDialogAlternate(feedback.Snapshot().Dialog!.Id);
            try
            {
                AssertTrue(entered.Wait(TimeSpan.FromSeconds(5)));
                AssertFalse(controller.CanClose()); AssertFalse(closed.IsSet);
                AssertEqual(requestPause, pause);
            }
            finally { release.Set(); }
            AssertTrue(closed.Wait(TimeSpan.FromSeconds(5)));
            AssertTrue(controller.CanClose());
        }
    }
}
