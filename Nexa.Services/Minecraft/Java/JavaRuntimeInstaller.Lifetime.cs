using Nexa.Services.Tasks;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Java;

public sealed record JavaInstallStopCommand(bool Pause);
public sealed record JavaInstallRecoverCommand;
public static class JavaInstallRoutes
{
    public static readonly XsrSemanticId Stop = XsrSemanticId.Parse("minecraft.java.install.stop");
    public static readonly XsrSemanticId Recover = XsrSemanticId.Parse("minecraft.java.install.recover");
}

public sealed partial class JavaRuntimeInstaller
{
    private readonly TaskCenterService? _tasks;
    private readonly object _gate = new();
    private readonly HashSet<JavaExecution> _executions = [];
    private readonly HashSet<string> _roots = new(MinecraftLibraryService.PathComparer);
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private bool _stopping;
    private bool _stopBusy;
    private sealed class JavaExecution : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop = new();
        private Task? _cancellation;
        private bool _finished;
        internal CancellationToken Token => _stop.Token;
        internal Task CancelAsync()
        { lock (_gate) return _finished ? Task.CompletedTask : _cancellation ??= _stop.CancelAsync(); }
        public void Dispose()
        {
            Task cancellation;
            lock (_gate) { _finished = true; cancellation = _cancellation ?? Task.CompletedTask; }
            if (cancellation.IsCompleted) _stop.Dispose();
            else _ = cancellation.ContinueWith(_ => _stop.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class JavaProgress(Action<JavaRuntimeInstallProgress> report) : IProgress<JavaRuntimeInstallProgress>
    { public void Report(JavaRuntimeInstallProgress value) => report(value); }

    public async Task<string> InstallAsync(string requestedComponent, string runtimeRootDirectory,
        IProgress<JavaRuntimeInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedComponent);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeRootDirectory));
        var execution = new JavaExecution();
        lock (_gate)
        {
            if (_stopping) { execution.Dispose(); throw new OperationCanceledException("Java 安装正在停止。"); }
            _roots.Add(root); _executions.Add(execution);
        }
        try
        {
            using var task = _tasks?.Begin(new("java-install:" + Guid.NewGuid().ToString("N"), "安装 Java", ["下载 Java"], CanCancel: false));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, execution.Token);
            try
            {
                string result = await InstallCoreAsync(requestedComponent, root, new JavaProgress(value =>
                {
                    task?.Report("下载 Java", value.Detail ?? "正在准备", value.Progress, value.CompletedFiles, value.TotalFiles, 0);
                    progress?.Report(value);
                }), execution, linked.Token).ConfigureAwait(false);
                task?.Complete(); return result;
            }
            catch (OperationCanceledException) { task?.Paused(); throw; }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            { task?.Fail("Java 安装未完成，已保留恢复记录。"); throw; }
        }
        finally
        {
            // Stop holds its own snapshot; dispose only after any cancellation callbacks have finished.
            lock (_gate) _executions.Remove(execution);
            execution.Dispose();
            execution.Done.TrySetResult();
        }
    }

    public async Task<XsrResult> StopAsync(JavaInstallStopCommand command, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        JavaExecution[] active; string[] roots;
        lock (_gate)
        {
            if (_stopBusy) return XsrResult.Failure(MinecraftErrors.InvalidRequest("Java 安装正在停止。"));
            _stopping = true; _stopBusy = true; active = _executions.ToArray(); roots = _roots.ToArray();
        }
        try
        {
            await Task.WhenAll(active.Select(async item =>
            {
                if (command.Pause) await Task.WhenAny(item.Ready.Task, item.Done.Task).ConfigureAwait(false);
                await item.CancelAsync().ConfigureAwait(false);
                await item.Done.Task.ConfigureAwait(false);
            })).ConfigureAwait(false);
            await _recoveryGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!command.Pause)
                    foreach (string root in roots) await CancelPendingAsync(root).ConfigureAwait(false);
            }
            finally { _recoveryGate.Release(); }
            return XsrResult.Success();
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { return XsrResult.Failure(MinecraftErrors.InvalidRequest("Java 安装未能安全停止：" + error.Message)); }
        finally { lock (_gate) _stopBusy = false; }
    }
}
