// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;

namespace PCL.Desktop.Features.Tasks.Views;

public enum TaskManagerTaskState
{
    Waiting,
    Running,
    Finished,
    Failed,
    Canceled
}

public sealed record TaskManagerEntrySnapshot(
    string TaskId,
    string Title,
    string Stage,
    string Detail,
    double Progress,
    int CompletedFiles,
    int TotalFiles,
    long SpeedBytesPerSecond,
    TaskManagerTaskState State,
    string? ErrorMessage = null,
    int ActiveThreads = 0,
    int ThreadLimit = 1,
    IReadOnlyList<TaskManagerSubTaskSnapshot>? Steps = null,
    /// <summary>
    /// When false, the task card hides the top-right close/cancel control
    /// (e.g. launcher self-update cannot be aborted mid-download).
    /// </summary>
    bool CanCancel = true);

public sealed record TaskManagerSubTaskSnapshot(
    string Name,
    string Detail,
    double Progress,
    TaskManagerTaskState State);

public sealed record TaskManagerSummary(
    double Progress,
    long SpeedBytesPerSecond,
    int RemainingFiles,
    int ActiveThreads,
    int ThreadLimit);

public sealed class TaskManagerTaskEventArgs(string taskId) : EventArgs
{
    public string TaskId { get; } = taskId;
}

internal static class TaskManagerFormatting
{
    public static string Percent(double value, bool twoDecimals = false)
    {
        value = Math.Clamp(value, 0d, 1d);
        if (value >= 0.999999d)
            return "100%";

        return value.ToString(twoDecimals ? "P2" : "P0", CultureInfo.CurrentCulture);
    }

    public static string Speed(long bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
            return "0 B/s";

        double value = bytesPerSecond;
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        int unit = 0;
        while (value >= 1024d && unit < units.Length - 1)
        {
            value /= 1024d;
            unit++;
        }

        return value.ToString(unit == 0 ? "F0" : "F1", CultureInfo.CurrentCulture) + " " + units[unit];
    }
}

internal static class TaskManagerStagePlanner
{
    public static TaskManagerSubTaskSnapshot[] Create(params string[] names) =>
        names
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Select(static name => new TaskManagerSubTaskSnapshot(
                name.Trim(),
                "等待中",
                0d,
                TaskManagerTaskState.Waiting))
            .ToArray();

    public static TaskManagerSubTaskSnapshot[] Advance(
        IReadOnlyList<TaskManagerSubTaskSnapshot> plan,
        string stage,
        string detail,
        double progress)
    {
        if (plan.Count == 0)
            return Create(stage);

        int current = ResolveStageIndex(plan, stage);
        int alreadyActive = -1;
        for (int i = 0; i < plan.Count; i++)
        {
            if (plan[i].State is TaskManagerTaskState.Running or TaskManagerTaskState.Finished)
                alreadyActive = i;
        }

        // Progress must never visually move backwards when a later phase reports a
        // generic stage name such as "下载文件" again.
        current = Math.Max(current, alreadyActive);
        current = Math.Clamp(current, 0, plan.Count - 1);
        bool completesCurrent = progress >= 0.999999d ||
                                stage.Contains("完成", StringComparison.OrdinalIgnoreCase) ||
                                stage.Contains("就绪", StringComparison.OrdinalIgnoreCase);

        TaskManagerSubTaskSnapshot[] result = new TaskManagerSubTaskSnapshot[plan.Count];
        for (int i = 0; i < plan.Count; i++)
        {
            if (i < current)
            {
                result[i] = plan[i] with
                {
                    Detail = "已完成",
                    Progress = 1d,
                    State = TaskManagerTaskState.Finished
                };
            }
            else if (i == current)
            {
                result[i] = plan[i] with
                {
                    Detail = string.IsNullOrWhiteSpace(detail) ? stage : detail,
                    Progress = completesCurrent ? 1d : Math.Clamp(progress, 0d, 1d),
                    State = completesCurrent
                        ? TaskManagerTaskState.Finished
                        : TaskManagerTaskState.Running
                };
            }
            else
            {
                result[i] = plan[i] with
                {
                    Detail = "等待中",
                    Progress = 0d,
                    State = TaskManagerTaskState.Waiting
                };
            }
        }

        return result;
    }

    private static int ResolveStageIndex(IReadOnlyList<TaskManagerSubTaskSnapshot> plan, string stage)
    {
        string normalized = Normalize(stage);
        int bestMatch = -1;
        int bestMatchLength = -1;
        for (int i = 0; i < plan.Count; i++)
        {
            string candidate = Normalize(plan[i].Name);
            if (candidate.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                if (candidate.Length > bestMatchLength)
                {
                    bestMatch = i;
                    bestMatchLength = candidate.Length;
                }
            }
        }

        if (bestMatch >= 0)
            return bestMatch;

        string[] semanticTargets = normalized switch
        {
            _ when normalized.Contains("加载器", StringComparison.OrdinalIgnoreCase) => ["加载器"],
            _ when normalized.Contains("附加组件", StringComparison.OrdinalIgnoreCase) => ["附加组件"],
            _ when normalized.Contains("版本描述", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("版本信息", StringComparison.OrdinalIgnoreCase) => ["版本信息", "版本描述"],
            _ when normalized.Contains("客户端", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("资源", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("运行库", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("文件", StringComparison.OrdinalIgnoreCase) =>
                ["游戏文件", "缺失文件", "下载内容", "下载资源"],
            _ when normalized.Contains("下载", StringComparison.OrdinalIgnoreCase) &&
                   !normalized.Contains("下载地址", StringComparison.OrdinalIgnoreCase) =>
                ["下载资源", "下载内容", "下载游戏文件", "下载模组"],
            _ => []
        };
        foreach (string target in semanticTargets)
        {
            for (int i = 0; i < plan.Count; i++)
            {
                if (Normalize(plan[i].Name).Contains(target, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }

        ReadOnlySpan<string> keywords =
        [
            "准备", "元数据", "版本信息", "版本描述", "前置", "下载", "游戏文件",
            "加载器", "附加组件", "校验", "验签", "重组", "解压", "安装", "应用", "完成", "就绪"
        ];
        foreach (string keyword in keywords)
        {
            if (!normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                continue;
            for (int i = 0; i < plan.Count; i++)
            {
                if (Normalize(plan[i].Name).Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }

        return 0;
    }

    private static string Normalize(string value) =>
        (value ?? string.Empty)
            .Replace("正在", string.Empty, StringComparison.Ordinal)
            .Replace("…", string.Empty, StringComparison.Ordinal)
            .Trim();
}
