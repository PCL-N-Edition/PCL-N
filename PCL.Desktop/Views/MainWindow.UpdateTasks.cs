// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Threading;
using PCL.Application.Updates;
using PCL.Desktop.Diagnostics;
using PCL.Desktop.Features.Tasks.Views;
using PCL.Desktop.Hosting;
using PCL.Desktop.Localization;

namespace PCL.Desktop.Views;

public partial class MainWindow
{
    /// <summary>Stable task-manager id for launcher self-update transfers.</summary>
    private const string LauncherUpdateTaskId = "update:launcher";

    private bool _launcherUpdateTaskVisible;
    private string _launcherUpdateTaskTitle = "启动器更新";

    private void AttachLauncherUpdateTaskBridge()
    {
        LauncherUpdateCoordinator coordinator = LauncherUpdateCoordinator.Current;
        coordinator.ProgressChanged += OnLauncherUpdateProgressChanged;
        coordinator.UpdateOperationActiveChanged += OnLauncherUpdateOperationActiveChanged;
        coordinator.PreparedUpdateChanged += OnLauncherUpdatePreparedChanged;
    }

    private void DetachLauncherUpdateTaskBridge()
    {
        LauncherUpdateCoordinator coordinator = LauncherUpdateCoordinator.Current;
        coordinator.ProgressChanged -= OnLauncherUpdateProgressChanged;
        coordinator.UpdateOperationActiveChanged -= OnLauncherUpdateOperationActiveChanged;
        coordinator.PreparedUpdateChanged -= OnLauncherUpdatePreparedChanged;
    }

    private void OnLauncherUpdateOperationActiveChanged(bool active)
    {
        void Apply()
        {
            if (active)
            {
                BeginLauncherUpdateTask();
                return;
            }

            // Flow ended (success, skip, or prompt dismissed without download).
            if (!_launcherUpdateTaskVisible)
                return;

            if (_taskSessionStore.TryGet(LauncherUpdateTaskId, out TaskManagerEntrySnapshot snapshot) &&
                snapshot.State is TaskManagerTaskState.Finished or TaskManagerTaskState.Failed or TaskManagerTaskState.Canceled)
            {
                _launcherUpdateTaskVisible = false;
                return;
            }

            if (LauncherUpdateCoordinator.Current.PreparedUpdate is not null)
            {
                FinishLauncherUpdateTask(
                    AvaloniaLocalizationManager.GetText(
                        "Setup.Update.Task.Ready",
                        "更新已就绪"));
            }
            else
            {
                // Prompt dismissed / skipped before prepare — drop the row quietly.
                _taskSessionStore.Remove(LauncherUpdateTaskId);
                _taskUiCoalescer.FlushNow();
                RefreshTaskManagerButton();
                _launcherUpdateTaskVisible = false;
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    private void OnLauncherUpdateProgressChanged(object? sender, LauncherUpdateProgress progress)
    {
        void Apply()
        {
            if (!_launcherUpdateTaskVisible &&
                !LauncherUpdateCoordinator.Current.IsUpdateOperationActive)
            {
                return;
            }

            if (!_launcherUpdateTaskVisible)
                BeginLauncherUpdateTask();

            string stage = FormatLauncherUpdateStage(progress.Stage);
            string detail = string.IsNullOrWhiteSpace(progress.Message)
                ? stage
                : progress.Message.Trim();

            if (progress.Stage is LauncherUpdateStage.Ready)
            {
                UpsertLauncherUpdateTask(
                    stage,
                    detail,
                    progress: 1d,
                    TaskManagerTaskState.Finished);
                _taskUiCoalescer.FlushNow();
                NotifyTaskManagerButton(ribble: true);
                _ = RemoveTaskAfterDelayAsync(LauncherUpdateTaskId, TimeSpan.FromMilliseconds(1200));
                _launcherUpdateTaskVisible = false;
                return;
            }

            UpsertLauncherUpdateTask(
                stage,
                detail,
                Math.Clamp(progress.Progress, 0d, 1d),
                TaskManagerTaskState.Running);
            _taskUiCoalescer.Request();
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    private void OnLauncherUpdatePreparedChanged(PreparedLauncherUpdate? prepared)
    {
        if (prepared is null)
            return;

        void Apply()
        {
            string version = prepared.Package.TargetVersion;
            if (!string.IsNullOrWhiteSpace(version))
            {
                _launcherUpdateTaskTitle =
                    AvaloniaLocalizationManager.GetText("Setup.Update.Task.Title", "启动器更新") +
                    " " + version;
            }

            if (!_launcherUpdateTaskVisible)
                return;

            UpsertLauncherUpdateTask(
                AvaloniaLocalizationManager.GetText("Setup.Update.Task.Ready", "更新已就绪"),
                AvaloniaLocalizationManager.GetText(
                    "Setup.Update.Task.ReadyDetail",
                    "下载完成并通过校验"),
                progress: 1d,
                TaskManagerTaskState.Finished);
            _taskUiCoalescer.FlushNow();
            NotifyTaskManagerButton(ribble: true);
            _ = RemoveTaskAfterDelayAsync(LauncherUpdateTaskId, TimeSpan.FromMilliseconds(1200));
            _launcherUpdateTaskVisible = false;
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    private void BeginLauncherUpdateTask()
    {
        string? version = LauncherUpdateCoordinator.Current.ActiveUpdateVersion;
        _launcherUpdateTaskTitle = string.IsNullOrWhiteSpace(version)
            ? AvaloniaLocalizationManager.GetText("Setup.Update.Task.Title", "启动器更新")
            : AvaloniaLocalizationManager.GetText("Setup.Update.Task.Title", "启动器更新") + " " + version;

        _launcherUpdateTaskVisible = true;
        DesktopFileLog.Info(
            "Task",
            $"任务开始/进入阶段；Id={LauncherUpdateTaskId}；Title={_launcherUpdateTaskTitle}；Stage=准备更新。");
        UpsertLauncherUpdateTask(
            AvaloniaLocalizationManager.GetText("Setup.Update.Task.Preparing", "准备更新…"),
            detail: string.Empty,
            progress: 0d,
            TaskManagerTaskState.Waiting);
        _taskUiCoalescer.FlushNow();
        NotifyTaskManagerButton(ribble: true);
    }

    private void FinishLauncherUpdateTask(string stage)
    {
        UpsertLauncherUpdateTask(
            stage,
            detail: AvaloniaLocalizationManager.GetText("Common.Task.Completed", "任务已完成"),
            progress: 1d,
            TaskManagerTaskState.Finished);
        _taskUiCoalescer.FlushNow();
        NotifyTaskManagerButton(ribble: true);
        _ = RemoveTaskAfterDelayAsync(LauncherUpdateTaskId, TimeSpan.FromMilliseconds(900));
        _launcherUpdateTaskVisible = false;
    }

    private void UpsertLauncherUpdateTask(
        string stage,
        string detail,
        double progress,
        TaskManagerTaskState state)
    {
        // CanCancel: false — launcher self-update cannot be aborted mid-download; hide the X.
        _taskSessionStore.Upsert(LauncherUpdateTaskId, new TaskManagerEntrySnapshot(
            LauncherUpdateTaskId,
            _launcherUpdateTaskTitle,
            stage,
            detail,
            progress,
            CompletedFiles: 0,
            TotalFiles: 0,
            SpeedBytesPerSecond: 0,
            state,
            CanCancel: false));
    }

    private static string FormatLauncherUpdateStage(LauncherUpdateStage stage) =>
        stage switch
        {
            LauncherUpdateStage.IndexingLocalBlocks => AvaloniaLocalizationManager.GetText(
                "Setup.Update.Task.Stage.Indexing", "正在索引本地分块…"),
            LauncherUpdateStage.DownloadingBlocks => AvaloniaLocalizationManager.GetText(
                "Setup.Update.Task.Stage.DownloadingBlocks", "正在下载更新分块…"),
            LauncherUpdateStage.RebuildingFromBlocks => AvaloniaLocalizationManager.GetText(
                "Setup.Update.Task.Stage.Rebuilding", "正在重组更新包…"),
            LauncherUpdateStage.DownloadingPatch => AvaloniaLocalizationManager.GetText(
                "Setup.Update.Task.Stage.DownloadingPatch", "正在下载补丁…"),
            LauncherUpdateStage.ApplyingPatch => AvaloniaLocalizationManager.GetText(
                "Setup.Update.Task.Stage.ApplyingPatch", "正在应用补丁…"),
            LauncherUpdateStage.FallingBack => AvaloniaLocalizationManager.GetText(
                "Setup.Update.Task.Stage.FallingBack", "补丁不可用，改用完整包…"),
            LauncherUpdateStage.DownloadingFullPackage => AvaloniaLocalizationManager.GetText(
                "Setup.Update.Task.Stage.DownloadingFull", "正在下载完整更新包…"),
            LauncherUpdateStage.Extracting => AvaloniaLocalizationManager.GetText(
                "Setup.Update.Task.Stage.Extracting", "正在解压…"),
            LauncherUpdateStage.Verifying => AvaloniaLocalizationManager.GetText(
                "Setup.Update.Task.Stage.Verifying", "正在校验…"),
            LauncherUpdateStage.VerifyingSignature => AvaloniaLocalizationManager.GetText(
                "Setup.Update.Task.Stage.VerifyingSignature", "正在验证签名…"),
            LauncherUpdateStage.Ready => AvaloniaLocalizationManager.GetText(
                "Setup.Update.Task.Ready", "更新已就绪"),
            _ => AvaloniaLocalizationManager.GetText("Setup.Update.Task.Preparing", "准备更新…")
        };
}
