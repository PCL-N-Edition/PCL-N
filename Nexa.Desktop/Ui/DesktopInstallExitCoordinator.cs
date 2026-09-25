using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Tasks;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

internal sealed class DesktopInstallExitCoordinator(
    XsrStateStore store, XsrCommandRouter commands, DesktopFeedbackService feedback, Action close)
{
    private int _busy;
    private volatile bool _approved;
    private volatile bool _stopFailed;

    public bool CanClose()
    {
        if (_approved) return true;
        if (Volatile.Read(ref _busy) != 0) return false;
        var active = store.ReadCollection<TaskCenterEntry>(store.Resolve(TaskCenterStateContract.EntriesKey)).Items
            .Where(item => !item.IsTerminal && (item.TaskId.StartsWith("install:", StringComparison.Ordinal)
                || item.TaskId.StartsWith("install-recovery:", StringComparison.Ordinal))).ToArray();
        if (active.Length == 0 && !_stopFailed) return true;
        if (Interlocked.Exchange(ref _busy, 1) != 0) return false;
        if (active.Length == 0 && _stopFailed)
        {
            feedback.ShowDialog("install.exit.retry", "安装撤回尚未完成", "恢复记录已保留。处理任务提示中的问题后，可重试取消或回滚。",
                "重试撤回并退出", "留在启动器", accepted =>
                {
                    if (accepted) _ = StopAsync(false); else Interlocked.Exchange(ref _busy, 0);
                });
            return false;
        }
        bool modifications = active.Any(item => item.Title.StartsWith("修改", StringComparison.Ordinal)
            || item.Title.StartsWith("继续修改", StringComparison.Ordinal)
            || item.TaskId.StartsWith("install-recovery:queue:", StringComparison.Ordinal));
        Guid dialogId = Guid.Empty;
        dialogId = feedback.ShowDialog("install.exit", "安装尚未完成", "暂停后，下次启动会继续。取消安装或回滚修改会撤回当前运行及排队恢复的任务。",
            "暂停并退出", "留在启动器", accepted =>
            {
                if (accepted) _ = StopAsync(true); else Interlocked.Exchange(ref _busy, 0);
            }, modifications ? "取消安装／回滚并退出" : "取消安装并退出", () => { if (feedback.DismissDialog(dialogId)) _ = StopAsync(false); });
        return false;
    }

    private async Task StopAsync(bool pause)
    {
        try
        {
            if (!commands.TryResolve(MinecraftInstallRoutes.Stop, out var route)) throw new InvalidOperationException("安装停止路由未注册。");
            var result = await Task.Run(async () => await commands.Dispatch(route, new MinecraftInstallStopCommand(pause)).Completion.ConfigureAwait(false)).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                _stopFailed = true;
                feedback.Error(result.Error?.Code == MinecraftErrors.InvalidRequestCode
                    ? result.Error.Message : "未能安全停止安装，请查看任务状态后重试。");
                return;
            }
            _approved = true;
            close();
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { _stopFailed = true; feedback.Error("未能安全停止安装：" + error.Message); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }
}
