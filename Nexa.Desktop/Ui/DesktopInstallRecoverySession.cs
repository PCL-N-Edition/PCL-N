using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Minecraft.Management;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Ui;

/// <summary>One startup dispatch with an owned lifetime; it never runs disk work on the render thread.</summary>
internal sealed class DesktopInstallRecoverySession : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _work;
    private int _disposed;

    internal DesktopInstallRecoverySession(XsrCommandRouter commands, IReadOnlyList<string> roots, Action<string> report, XsrCommandRouter? javaCommands = null, XsrCommandRouter? recoveryCommands = null)
    {
        if (!commands.TryResolve(MinecraftInstallRoutes.Recover, out var route)) throw new InvalidOperationException("安装恢复路由未注册。");
        var request = new MinecraftInstallRecoveryCommand(Array.AsReadOnly(roots.ToArray()));
        _work = Task.Run(async () =>
        {
            try
            {
                if (recoveryCommands is not null && recoveryCommands.TryResolve(InstanceRecoveryContract.Recover, out var recoveryRoute))
                {
                    var recovered = await recoveryCommands.Dispatch(recoveryRoute, new InstanceRecoveryResumeCommand(roots), cancellationToken: _lifetime.Token).Completion.ConfigureAwait(false);
                    if (!recovered.IsSuccess) { report("快照恢复事务尚未处理完毕，已暂停自动安装恢复。" + recovered.Error?.Message); return; }
                }
                var result = await commands.Dispatch(route, request, cancellationToken: _lifetime.Token).Completion.ConfigureAwait(false);
                if (!result.IsSuccess && result.Error?.Code != XsrRuntimeErrors.Cancelled().Code && !_lifetime.IsCancellationRequested) report("部分安装任务未能恢复，记录已保留；请查看任务中心。");
                if (javaCommands is not null && javaCommands.TryResolve(JavaInstallRoutes.Recover, out var javaRoute))
                {
                    var javaResult = await javaCommands.Dispatch(javaRoute, new JavaInstallRecoverCommand(), cancellationToken: _lifetime.Token).Completion.ConfigureAwait(false);
                    if (!javaResult.IsSuccess && javaResult.Error?.Code != XsrRuntimeErrors.Cancelled().Code && !_lifetime.IsCancellationRequested)
                        report("Java 安装未能恢复，记录已保留；请查看任务中心。");
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            { report("安装恢复未完成：" + error.Message); }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        try { _work.GetAwaiter().GetResult(); }
        finally { _lifetime.Dispose(); }
    }
}
