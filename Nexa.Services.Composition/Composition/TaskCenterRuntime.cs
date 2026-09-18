using Nexa.Services.Foundation;
using Nexa.Services.Tasks;
using Nexa.Xsr.Runtime;

namespace Nexa.Services.Composition;

public sealed class TaskCenterRuntime(XsrCommandRouter commands) : IDisposable
{
    public XsrCommandRouter Commands { get; } = commands;
    public void Dispose() { }
}

public static class TaskCenterRuntimeComposer
{
    public static TaskCenterRuntime Compose(FoundationHost host, IXsrDispatchObserver? observer = null)
    {
        XsrCommandRouterBuilder commands = new();
        commands.Register<TaskCenterCancelCommand>(TaskCenterRoutes.Cancel,
            (command, _) => ValueTask.FromResult(host.Tasks.RequestCancel(command.TaskId) switch
            {
                TaskCenterCancelResult.Canceled => Nexa.Xsr.XsrResult.Success(),
                // Not-cancelable is not missing: the route says which, so callers never
                // disguise an enforced boundary as an unknown id.
                TaskCenterCancelResult.NotCancelable => Nexa.Xsr.XsrResult.Failure(new Nexa.Xsr.XsrError(
                    Nexa.Xsr.XsrErrorKind.Rejected,
                    Nexa.Xsr.XsrSemanticId.Parse("tasks.task_not_cancelable"),
                    "该任务不支持取消。")),
                _ => Nexa.Xsr.XsrResult.Failure(Nexa.Xsr.XsrRuntimeErrors.TargetNotFound("任务不存在或已结束。")),
            }));
        commands.Register<TaskCenterDismissCommand>(TaskCenterRoutes.Dismiss,
            (command, _) => ValueTask.FromResult(host.Tasks.Dismiss(command.TaskId)
                ? Nexa.Xsr.XsrResult.Success()
                : Nexa.Xsr.XsrResult.Failure(Nexa.Xsr.XsrRuntimeErrors.TargetNotFound("任务不存在或已结束。"))));
        commands.Register<TaskCenterClearCommand>(TaskCenterRoutes.ClearFinished,
            (_, _) =>
            {
                host.Tasks.ClearFinished();
                return ValueTask.FromResult(Nexa.Xsr.XsrResult.Success());
            });
        var dispatchObserver = observer ?? new Observer();
        return new TaskCenterRuntime(commands.Build(dispatchObserver));
    }

    private sealed class Observer : IXsrDispatchObserver
    {
        public void OnCompleted(XsrDispatchObservation observation) { }
    }
}

public sealed record TaskCenterCancelCommand(string TaskId);
public sealed record TaskCenterDismissCommand(string TaskId);
public sealed record TaskCenterClearCommand;
