using Nexa.Services.Minecraft.Management;
using Nexa.UI.Next;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private int _recoverySnapshotPage, _recoveryChangesPage;

    private void BuildRecoveryStorage(InstanceManagementSnapshot snapshot)
    {
        if (_catalog?.Entries.FirstOrDefault(item => item.Id == "instance.recovery.keep-history") is { } setting)
            BuildRow(_sections, setting);
        Text(_sections, "默认只保留最新成功快照。关闭历史保留后，下次成功采集时清理旧快照。", 12, Muted, 28);
        if (snapshot.RecoveryStorage is not { } storage)
        {
            Text(_sections, "正在统计存储与读取快照…", 13, Muted, 28);
            return;
        }
        if (storage.Error is { } error) Text(_sections, error, 13, Muted, 28);
        ManagementFact("版本文件", RecoverySize(storage.VersionBytes) + (storage.Complete ? "" : "（已统计）"));
        ManagementFact("快照存储", RecoverySize(storage.SnapshotBytes) + (storage.Complete ? "" : "（已统计）"));
        Text(_sections, "按文件大小统计；版本文件不含快照及版本目录外的共享文件。快照含压缩对象、清单和暂存。", 12, Muted, 42);
        Text(_sections, "成功快照", 18, Ink, 30, 600);
        const int pageSize = 8;
        int pages = Math.Max(1, (storage.Snapshots.Count + pageSize - 1) / pageSize);
        _recoverySnapshotPage = Math.Clamp(_recoverySnapshotPage, 0, pages - 1);
        if (storage.Snapshots.Count == 0) Text(_sections, storage.Complete ? "尚无成功运行的快照。" : "暂时无法列出快照。", 13, Muted, 28);
        foreach (var item in storage.Snapshots.Skip(_recoverySnapshotPage * pageSize).Take(pageSize))
        {
            string label = item.CapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
            if (item.Revision == snapshot.RecoveryComparison?.BaselineRevision) label += " · 最新";
            var row = Stack(_sections, "RecoverySnapshot", XsrUiOrientation.Vertical, 2);
            Text(row, label, 14, Ink, 26);
            Text(row, $"{item.Files} 个文件 · 原始内容 {RecoverySize(item.ContentBytes)}", 12, Muted, 22);
        }
        if (pages > 1)
            ManagementButton(_sections, $"快照 {_recoverySnapshotPage + 1}/{pages} · 下一页", () =>
            { _recoverySnapshotPage = (_recoverySnapshotPage + 1) % pages; BuildSections(true); UpdateEditors(); }, 180);
        Text(_sections, "与最新快照比较", 18, Ink, 30, 600);
        if (snapshot.RecoveryComparison is not { } comparison)
            Text(_sections, "正在比较更改…", 13, Muted, 28);
        else if (comparison.UnavailableReason is { } unavailable)
            Text(_sections, unavailable, 13, Muted, 28);
        else
        {
            int changePages = Math.Max(1, (comparison.Changes.Count + pageSize - 1) / pageSize);
            _recoveryChangesPage = Math.Clamp(_recoveryChangesPage, 0, changePages - 1);
            Text(_sections, comparison.Changes.Count == 0 ? "恢复范围内没有更改。" : $"{comparison.Changes.Count} 项更改", 13, Muted, 26);
            if (comparison.Changes.Count > 0)
                ManagementButton(_sections, "回滚全部更改", () => RestoreChanges(comparison, comparison.Changes), 140);
            foreach (var item in comparison.Changes.Skip(_recoveryChangesPage * pageSize).Take(pageSize))
            {
                string kind = item.Kind switch { InstanceRecoveryChangeKind.Added => "新增", InstanceRecoveryChangeKind.Removed => "删除", _ => "修改" };
                var row = Stack(_sections, "RecoveryChange", XsrUiOrientation.Vertical, 2);
                Text(row, kind + " · " + item.Category, 12, Muted, 22);
                var path = Text(row, new string(item.Path.Select(c => char.IsControl(c) ? ' ' : c).ToArray()), 14, Ink, 42);
                _shell.Tree.GetComponent<XsrUiVisualStyle>(path)!.WrapText = true;
                ManagementButton(row, "回滚此项", () => RestoreChanges(comparison, [item]), 100);
            }
            if (changePages > 1)
                ManagementButton(_sections, $"更改 {_recoveryChangesPage + 1}/{changePages} · 下一页", () =>
                { _recoveryChangesPage = (_recoveryChangesPage + 1) % changePages; BuildSections(true); UpdateEditors(); }, 180);
        }
    }

    private void RestoreChanges(InstanceRecoveryReport report, IReadOnlyList<InstanceRecoveryChange> changes)
    {
        if (_managementWrite is not null || report.BaselineRevision is not { } revision
            || _instance != report.InstanceDirectory || !_commands.TryResolve(InstanceRecoveryContract.Restore, out var route)) return;
        _feedback.ShowDialog("recovery.confirm", "回滚所选更改", $"将恢复 {changes.Count} 项更改。存档、截图和日志不受影响。", "回滚", "取消", accepted =>
        {
            if (!accepted || _managementWrite is not null || _instance != report.InstanceDirectory) return;
            _managementWriteInstance = report.InstanceDirectory;
            _managementWrite = _commands.Dispatch(route, new InstanceRecoveryRestoreCommand(report.InstanceDirectory, revision,
                report.Fingerprint, changes.ToArray())).Completion;
            WakeOnPlatformCompletion(_managementWrite);
        });
    }

    private static string RecoverySize(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.##} GiB"
        : bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.##} MiB" : $"{bytes / 1024d:0.##} KiB";
}
