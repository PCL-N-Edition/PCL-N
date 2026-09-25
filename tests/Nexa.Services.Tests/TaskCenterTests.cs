using Nexa.Services.Tasks;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

// XSR-723: task center capability contract — registration, progress aggregation, stage
// advance monotonicity, cancellation, dismissal, and the published summary. Everything runs
// on one built store so the collection/summary cells behave exactly as the renderer sees them.
internal static partial class Program
{
    private static ValueTask TaskCenterPausePreservesCheckpointWithoutCompletingSteps()
    {
        TaskCenterService service = NewTaskCenter(out XsrStateStore store);
        using ITaskCenterTask task = service.Begin(new("pause:1", "安装", ["下载", "发布"]));
        task.Report("下载", "下载中", 0.4, 2, 5, 100);
        task.Paused();
        task.Report("发布", "迟到的进度", 1, 5, 5, 100);
        task.Complete();
        var entry = store.ReadCollection<TaskCenterEntry>(store.Resolve(TaskCenterStateContract.EntriesKey)).Items.Single();
        AssertEqual(TaskCenterEntryState.Paused, entry.State);
        AssertEqual(0.4, entry.Progress);
        AssertEqual(0L, entry.SpeedBytesPerSecond);
        AssertEqual(TaskCenterEntryState.Waiting, entry.Steps![1].State);
        AssertEqual(0, store.Read<TaskCenterSummary>(store.Resolve(TaskCenterStateContract.SummaryKey)).Value!.ActiveCount);
        AssertEqual(TaskCenterCancelResult.NotFound, service.RequestCancel("pause:1"));
        AssertTrue(service.Dismiss("pause:1"));
        return ValueTask.CompletedTask;
    }

    private sealed class TaskPublicationObserver : IXsrStateObserver
    {
        internal Action? Callback;
        public void OnChanged(XsrStateChange change) => Interlocked.Exchange(ref Callback, null)?.Invoke();
    }

    private static async ValueTask TaskCenterSerializesRaces()
    {
        foreach (string scenario in new[] { "complete", "reuse", "dismiss" })
        {
            var observer = new TaskPublicationObserver();
            var builder = new XsrStateStoreBuilder();
            TaskCenterStateContract.DeclareState(builder);
            var store = builder.Build(observer);
            var service = new TaskCenterService(store);
            using var original = service.Begin(new("same", "Original", ["Work"]));
            if (scenario == "dismiss") original.Complete();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            observer.Callback = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(15)); };
            var publisher = Task.Run(() =>
            {
                if (scenario == "dismiss") service.Dismiss("same");
                else original.Report("Work", "Old report", .5, 1, 2, 0);
            });
            ITaskCenterTask? replacement = null;
            try
            {
                AssertTrue(entered.Wait(TimeSpan.FromSeconds(10)), "Publication reached barrier");
                await Task.Run(() =>
                {
                    if (scenario != "dismiss") original.Complete();
                    if (scenario != "complete") replacement = service.Begin(new("same", "Replacement", ["Work"]));
                }).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { release.Set(); }
            await publisher.WaitAsync(TimeSpan.FromSeconds(10));
            var entry = store.ReadCollection<TaskCenterEntry>(store.Resolve(TaskCenterStateContract.EntriesKey)).Items.Single();
            var summary = (TaskCenterSummary)store.ReadAppliedValue(store.Resolve(TaskCenterStateContract.SummaryKey))!;
            AssertEqual(scenario == "complete" ? TaskCenterEntryState.Finished : TaskCenterEntryState.Running, entry.State);
            AssertEqual(scenario == "complete" ? 0 : 1, summary.ActiveCount);
            if (replacement is not null)
            {
                original.Report("Work", "Late old report", .9, 2, 2, 0);
                AssertEqual("Replacement", entry.Title);
                replacement.Dispose();
            }
        }
        // An observer may call back into the service synchronously without recursive publication.
        var reentrant = new TaskPublicationObserver();
        var declaration = new XsrStateStoreBuilder();
        TaskCenterStateContract.DeclareState(declaration);
        var reentrantStore = declaration.Build(reentrant);
        var center = new TaskCenterService(reentrantStore);
        using var task = center.Begin(new("reentry", "Reentry", ["Work"]));
        reentrant.Callback = () => task.Complete();
        task.Report("Work", "Progress", .5, 1, 2, 0);
        AssertEqual(0, ((TaskCenterSummary)reentrantStore.ReadAppliedValue(reentrantStore.Resolve(TaskCenterStateContract.SummaryKey))!).ActiveCount);
    }

    private static TaskCenterService NewTaskCenter(out XsrStateStore store)
    {
        XsrStateStoreBuilder builder = new();
        TaskCenterStateContract.DeclareState(builder);
        store = builder.Build();
        return new TaskCenterService(store);
    }

    private static ValueTask TaskCenterTracksLifecycleAndSummary()
    {
        TaskCenterService service = NewTaskCenter(out XsrStateStore store);
        XsrStateId entries = store.Resolve(TaskCenterStateContract.EntriesKey);
        XsrStateId summary = store.Resolve(TaskCenterStateContract.SummaryKey);

        using ITaskCenterTask install = service.Begin(new TaskCenterStart(
            "install:1", "安装 Minecraft 1.20.1", ["版本信息", "游戏文件", "完成"]));
        install.Report("游戏文件", "下载中", 0.5, 5, 10, 1024);
        using ITaskCenterTask skin = service.Begin(new TaskCenterStart(
            "download:1", "下载皮肤", ["下载文件"], CanCancel: false));

        TaskCenterSummary live = (TaskCenterSummary)store.ReadAppliedValue(summary)!;
        AssertEqual(2, live.ActiveCount);
        AssertEqual(2, live.VisibleCount);
        AssertTrue(Math.Abs(live.Progress - 0.25) < 0.0001, "summary averages active progress");
        AssertEqual(1024L, live.SpeedBytesPerSecond);
        AssertEqual(5, live.RemainingFiles);

        XsrCollectionSnapshot<TaskCenterEntry> snapshot = store.ReadCollection<TaskCenterEntry>(entries);
        AssertEqual(2, snapshot.Items.Count);
        TaskCenterEntry installEntry = snapshot.Items.Single(entry => entry.TaskId == "install:1");
        AssertEqual(TaskCenterEntryState.Running, installEntry.State);
        AssertEqual(3, installEntry.Steps!.Count);
        AssertEqual(TaskCenterEntryState.Finished, installEntry.Steps[0].State);
        AssertEqual(TaskCenterEntryState.Running, installEntry.Steps[1].State);

        install.Complete("安装完成");
        live = (TaskCenterSummary)store.ReadAppliedValue(summary)!;
        AssertEqual(1, live.ActiveCount);
        TaskCenterEntry finished = store.ReadCollection<TaskCenterEntry>(entries)
            .Items.Single(entry => entry.TaskId == "install:1");
        AssertTrue(TaskCenterEntryState.Finished == finished.State && 1d == finished.Progress,
            "finished entry is complete");
        AssertEqual(TaskCenterEntryState.Finished, finished.Steps![2].State);

        // Terminal entries stay visible until dismissed; dismissal removes them.
        AssertTrue(service.Dismiss("install:1"), "terminal entry dismisses");
        AssertFalse(service.Dismiss("download:1"));
        AssertEqual(1, store.ReadCollection<TaskCenterEntry>(entries).Items.Count);
        return ValueTask.CompletedTask;
    }

    private static ValueTask TaskCenterCancelRoutesToOwnerToken()
    {
        TaskCenterService service = NewTaskCenter(out XsrStateStore store);
        using ITaskCenterTask task = service.Begin(new TaskCenterStart(
            "install:2", "安装 Minecraft 1.20.1", ["游戏文件"]));
        AssertFalse(task.CancellationToken.IsCancellationRequested);

        AssertEqual(TaskCenterCancelResult.Canceled, service.RequestCancel("install:2"));
        AssertTrue(task.CancellationToken.IsCancellationRequested, "owner token fired");

        // Terminal and unknown ids read as not-found at the boundary.
        task.Canceled();
        AssertEqual(TaskCenterCancelResult.NotFound, service.RequestCancel("install:2"));
        AssertEqual(TaskCenterCancelResult.NotFound, service.RequestCancel("missing"));
        TaskCenterEntry entry = store.ReadCollection<TaskCenterEntry>(
            store.Resolve(TaskCenterStateContract.EntriesKey)).Items.Single();
        AssertEqual(TaskCenterEntryState.Canceled, entry.State);
        return ValueTask.CompletedTask;
    }

    private static ValueTask TaskCenterStagesNeverMoveBackwards()
    {
        TaskCenterService service = NewTaskCenter(out XsrStateStore store);
        using ITaskCenterTask task = service.Begin(new TaskCenterStart(
            "install:3", "安装", ["版本信息", "游戏文件", "附加组件"]));
        task.Report("游戏文件", "half", 0.5, 1, 2, 0);
        // A later phase reusing the generic "下载文件" name must not rewind the step list.
        task.Report("下载文件", "addon", 0.1, 0, 9, 0);
        TaskCenterEntry entry = store.ReadCollection<TaskCenterEntry>(
            store.Resolve(TaskCenterStateContract.EntriesKey)).Items.Single();
        AssertEqual(TaskCenterEntryState.Finished, entry.Steps![0].State);
        AssertEqual(TaskCenterEntryState.Running, entry.Steps[1].State);
        AssertEqual(TaskCenterEntryState.Waiting, entry.Steps[2].State);
        return ValueTask.CompletedTask;
    }

    private static ValueTask TaskCenterAbandonedHandlesSurfaceAsFailed()
    {
        TaskCenterService service = NewTaskCenter(out XsrStateStore store);
        XsrStateId entries = store.Resolve(TaskCenterStateContract.EntriesKey);
        ITaskCenterTask task = service.Begin(new TaskCenterStart("install:4", "安装", []));
        task.Dispose();
        TaskCenterEntry entry = store.ReadCollection<TaskCenterEntry>(entries).Items.Single();
        AssertEqual(TaskCenterEntryState.Failed, entry.State);
        AssertEqual("任务意外结束。", entry.ErrorMessage);

        // The abandoned registration released its id: reuse replaces the failed card with a
        // fresh running one under the same id (one card, not two).
        using ITaskCenterTask reused = service.Begin(new TaskCenterStart("install:4", "安装", []));
        TaskCenterEntry reentered = store.ReadCollection<TaskCenterEntry>(entries).Items.Single();
        AssertEqual(TaskCenterEntryState.Running, reentered.State);
        AssertEqual(null, reentered.ErrorMessage);
        return ValueTask.CompletedTask;
    }

    private static ValueTask TaskCenterRejectsCancelOfProtectedTasks()
    {
        TaskCenterService service = NewTaskCenter(out XsrStateStore store);
        using ITaskCenterTask protectedTask = service.Begin(new TaskCenterStart(
            "update:1", "启动器自更新", [], CanCancel: false));

        AssertEqual(TaskCenterCancelResult.NotCancelable, service.RequestCancel("update:1"));
        AssertFalse(protectedTask.CancellationToken.IsCancellationRequested);
        return ValueTask.CompletedTask;
    }

    private static ValueTask TaskCenterBoundsTerminalHistoryForEveryTerminalKind()
    {
        TaskCenterService service = NewTaskCenter(out XsrStateStore store);
        XsrStateId entries = store.Resolve(TaskCenterStateContract.EntriesKey);
        int TerminalCount() => store.ReadCollection<TaskCenterEntry>(entries)
            .Items.Count(static entry => entry.IsTerminal);

        for (int index = 0; index < 31; index++)
        {
            service.Begin(new TaskCenterStart($"done:{index}", $"完成 {index}", [])).Complete();
        }

        AssertTrue(TerminalCount() <= 30, $"finished bounded: {TerminalCount()}");

        for (int index = 0; index < 31; index++)
        {
            service.Begin(new TaskCenterStart($"fail:{index}", $"失败 {index}", [])).Fail("boom");
        }

        AssertTrue(TerminalCount() <= 30, $"failed bounded: {TerminalCount()}");

        for (int index = 0; index < 31; index++)
        {
            ITaskCenterTask canceled = service.Begin(new TaskCenterStart($"cancel:{index}", $"取消 {index}", []));
            _ = service.RequestCancel($"cancel:{index}");
            canceled.Canceled();
        }

        AssertTrue(TerminalCount() <= 30, $"canceled bounded: {TerminalCount()}");

        for (int index = 0; index < 31; index++)
        {
            service.Begin(new TaskCenterStart($"abandon:{index}", $"丢弃 {index}", [])).Dispose();
        }

        AssertTrue(TerminalCount() <= 30, $"abandoned bounded: {TerminalCount()}");
        return ValueTask.CompletedTask;
    }

    private static ValueTask TaskCenterClearFinishedLeavesActive()
    {
        TaskCenterService service = NewTaskCenter(out XsrStateStore store);
        ITaskCenterTask first = service.Begin(new TaskCenterStart("a", "a", []));
        using ITaskCenterTask second = service.Begin(new TaskCenterStart("b", "b", []));
        first.Complete();
        AssertEqual(1, service.ClearFinished());
        AssertEqual(1, store.ReadCollection<TaskCenterEntry>(
            store.Resolve(TaskCenterStateContract.EntriesKey)).Items.Count);
        second.Fail("boom");
        AssertEqual(TaskCenterEntryState.Failed, store.ReadCollection<TaskCenterEntry>(
            store.Resolve(TaskCenterStateContract.EntriesKey)).Items.Single().State);
        return ValueTask.CompletedTask;
    }
}
