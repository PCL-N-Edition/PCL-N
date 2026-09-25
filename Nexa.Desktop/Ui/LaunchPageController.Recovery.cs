using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.Process;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Ui;

internal sealed partial class LaunchPageController
{
    private readonly XsrQueryRouter? _recoveryQueries;

    private bool TryShowCrashDialog(MinecraftProcessFailure failure)
    {
        string reason = CrashSummary(failure.Report) + "\n\n" + failure.Report.Message;
        if (!_feedback.TryShowMessageDialog("minecraft.crash." + failure.SessionId,
            failure.InstanceId + " · 游戏异常退出", reason + "\n\n正在比较上次成功运行后的更改…", "知道了", out var id))
            return false;
        _ = LoadCrashChangesAsync(failure, id, reason);
        return true;
    }

    private async Task LoadCrashChangesAsync(MinecraftProcessFailure failure, Guid dialogId, string reason)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(failure.InstanceDirectory) || _recoveryQueries is null
                || !_recoveryQueries.TryResolve(InstanceRecoveryContract.Query, out var route))
            {
                _feedback.TryUpdateMessageDialog(dialogId, reason + "\n\n无法确定本次运行的实例，未比较更改。");
                return;
            }
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            void CancelIfClosed(object? sender, EventArgs args)
            {
                if (_feedback.Snapshot().Dialog?.Id == dialogId) return;
                try { stop.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            _feedback.Changed += CancelIfClosed;
            Nexa.Xsr.XsrResult<InstanceRecoveryReport> result;
            try
            {
                CancelIfClosed(null, EventArgs.Empty);
                // Dispatch away from render even if a query handler has synchronous preparation.
                result = await Task.Run(async () => await _recoveryQueries
                    .QueryAsync<InstanceRecoveryQuery, InstanceRecoveryReport>(route, new(failure.InstanceDirectory),
                        cancellationToken: stop.Token).ConfigureAwait(false)).ConfigureAwait(false);
            }
            finally { _feedback.Changed -= CancelIfClosed; }
            if (_disposed) return;
            if (!result.IsSuccess)
            {
                _feedback.TryUpdateMessageDialog(dialogId, reason + "\n\n更改比较未完成。" + result.Error?.Message);
                return;
            }
            new CrashChangesPresentation(_feedback, dialogId, reason, result.Value!).Show();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            _feedback.TryUpdateMessageDialog(dialogId, reason + "\n\n暂时无法读取更改清单。");
        }
    }
}

// Bounded pages keep wrapping/layout work independent of the size of a modpack.
// Every change remains reachable, without creating thousands of UI entities.
internal sealed class CrashChangesPresentation(DesktopFeedbackService feedback, Guid dialogId,
    string reason, InstanceRecoveryReport report)
{
    internal const int PageSize = 12;
    private int _page;

    public void Show()
    {
        if (report.UnavailableReason is { } unavailable)
        {
            feedback.TryUpdateMessageDialog(dialogId, reason + "\n\n" + unavailable);
            return;
        }
        int count = report.Changes.Count, pages = Math.Max(1, (count + PageSize - 1) / PageSize);
        string heading = count == 0 ? "与上次成功运行相比，恢复范围内没有更改。"
            : $"上次成功运行后的更改 · {count} 项（{_page + 1}/{pages}）";
        var rows = report.Changes.Skip(_page * PageSize).Take(PageSize).Select(change =>
            $"{Kind(change.Kind)} · {change.Category}\n{DisplayPath(change.Path)}");
        string body = reason + "\n\n" + heading + "\n" + string.Join("\n\n", rows);
        feedback.TryUpdateMessageDialog(dialogId, body, pages > 1 ? (_page + 1 == pages ? "回到首批" : "下一批更改") : null,
            pages > 1 ? () => { _page = (_page + 1) % pages; Show(); }
        : null);
    }

    private static string Kind(InstanceRecoveryChangeKind kind) => kind switch
    {
        InstanceRecoveryChangeKind.Added => "新增",
        InstanceRecoveryChangeKind.Removed => "删除",
        _ => "修改"
    };

    // Filenames are data: embedded control characters must not impersonate another row.
    private static string DisplayPath(string path) => new(path.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
