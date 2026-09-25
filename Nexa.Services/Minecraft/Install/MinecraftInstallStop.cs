using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Install;

public sealed record MinecraftInstallStopCommand(bool Pause);

public sealed partial class MinecraftInstallService
{
    private readonly object _executionGate = new();
    private readonly HashSet<InstallExecution> _executions = [];
    private readonly HashSet<string> _recoveryRoots = new(MinecraftLibraryService.PathComparer);
    private readonly SemaphoreSlim _recoveryDiscoveryGate = new(1, 1);
    private volatile bool _stopping;
    private bool _stopInProgress;
    private volatile bool _pauseForExit;

    private sealed class InstallExecution : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop = new();
        private Task? _cancellation;
        private bool _finished;
        public CancellationToken Token => _stop.Token;
        public Task RequestStopAsync()
        {
            lock (_gate) return _finished ? Task.CompletedTask : _cancellation ??= _stop.CancelAsync();
        }
        public void Dispose()
        {
            Task cancellation;
            lock (_gate) { _finished = true; cancellation = _cancellation ?? Task.CompletedTask; }
            if (cancellation.IsCompleted) _stop.Dispose();
            else _ = cancellation.ContinueWith(_ => _stop.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public required bool CanPause { get; init; }
        public string? Stage { get; set; }
        public bool Completed { get; set; }
    }

    private InstallExecution RegisterExecution(MinecraftInstallCommand command)
    {
        lock (_executionGate)
        {
            if (_stopping) throw new OperationCanceledException("正在停止安装任务。");
            _recoveryRoots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(command.RootDirectory)));
            var execution = new InstallExecution
            {
                CanPause = command.NewInstanceName is null || command.NewInstanceName == command.InstanceName
            };
            _executions.Add(execution);
            return execution;
        }
    }

    private void FinishExecution(InstallExecution execution)
    {
        lock (_executionGate) _executions.Remove(execution);
        execution.Dispose();
        execution.Done.TrySetResult();
    }

    private static void ReadyExecution(InstallExecution? execution, string stage)
    {
        if (execution is null) return;
        execution.Stage = stage;
        execution.Ready.TrySetResult();
    }

    public async Task<XsrResult> StopAsync(MinecraftInstallStopCommand command, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        InstallExecution[] executions;
        string[] roots;
        lock (_executionGate)
        {
            if (_stopInProgress || _stopping && command.Pause)
                return XsrResult.Failure(MinecraftErrors.InvalidRequest("安装任务正在停止或等待撤回，请稍后重试取消／回滚。"));
            executions = _executions.ToArray();
            roots = _recoveryRoots.ToArray();
            if (command.Pause && executions.Any(item => !item.CanPause))
                return XsrResult.Failure(MinecraftErrors.InvalidRequest("当前改名任务暂不支持暂停，请等待完成或取消安装。"));
            _stopping = true;
            _stopInProgress = true;
            _pauseForExit = command.Pause;
        }
        try
        {
            // Stop each ready worker independently: queued work may be waiting on a running worker's root lease.
            await Task.WhenAll(executions.Select(async item =>
            {
                if (command.Pause) await Task.WhenAny(item.Ready.Task, item.Done.Task).ConfigureAwait(false);
                await item.RequestStopAsync().ConfigureAwait(false);
                await item.Done.Task.ConfigureAwait(false);
            })).ConfigureAwait(false);
            // A discovery batch can also be executing an already-requested rollback outside the worker registry.
            await _recoveryDiscoveryGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!command.Pause) await CancelPendingInstallationsAsync(roots).ConfigureAwait(false);
            }
            finally { _recoveryDiscoveryGate.Release(); }
            return XsrResult.Success();
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            return XsrResult.Failure(MinecraftErrors.InvalidRequest("安装任务未能安全停止：" + error.Message));
        }
        finally { lock (_executionGate) _stopInProgress = false; }
        // Admission remains closed after a stop request, including failure: no concurrent restart over retained work.
    }
}
