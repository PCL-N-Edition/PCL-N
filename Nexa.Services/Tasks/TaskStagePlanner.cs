namespace Nexa.Services.Tasks;

/// <summary>
/// Derives the per-stage step list from progress reports. Progress must never move
/// backwards when a later phase reuses a generic stage name such as "下载文件", so the
/// active index is the maximum of the matched stage and any already-active stage.
/// Behavior parity with the legacy TaskManagerStagePlanner.
/// </summary>
public static class TaskStagePlanner
{
    public static TaskCenterStep[] Create(IReadOnlyList<string> names) =>
        names
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Select(static name => new TaskCenterStep(name.Trim(), "等待中", 0d, TaskCenterEntryState.Waiting))
            .ToArray();

    public static TaskCenterStep[] Advance(IReadOnlyList<TaskCenterStep> plan, string stage, string detail, double progress)
    {
        if (plan.Count == 0)
        {
            return Create([stage]);
        }

        int current = ResolveStageIndex(plan, stage);
        int alreadyActive = -1;
        for (int i = 0; i < plan.Count; i++)
        {
            if (plan[i].State is TaskCenterEntryState.Running or TaskCenterEntryState.Finished)
            {
                alreadyActive = i;
            }
        }

        current = Math.Clamp(Math.Max(current, alreadyActive), 0, plan.Count - 1);
        bool completesCurrent = progress >= 0.999999d
            || stage.Contains("完成", StringComparison.OrdinalIgnoreCase)
            || stage.Contains("就绪", StringComparison.OrdinalIgnoreCase);

        TaskCenterStep[] result = new TaskCenterStep[plan.Count];
        for (int i = 0; i < plan.Count; i++)
        {
            if (i < current)
            {
                result[i] = plan[i] with { Detail = "已完成", Progress = 1d, State = TaskCenterEntryState.Finished };
            }
            else if (i == current)
            {
                result[i] = plan[i] with
                {
                    Detail = string.IsNullOrWhiteSpace(detail) ? stage : detail,
                    Progress = completesCurrent ? 1d : Math.Clamp(progress, 0d, 1d),
                    State = completesCurrent ? TaskCenterEntryState.Finished : TaskCenterEntryState.Running,
                };
            }
            else
            {
                result[i] = plan[i] with { Detail = "等待中", Progress = 0d, State = TaskCenterEntryState.Waiting };
            }
        }

        return result;
    }

    private static int ResolveStageIndex(IReadOnlyList<TaskCenterStep> plan, string stage)
    {
        string normalized = Normalize(stage);
        int bestMatch = -1;
        int bestMatchLength = -1;
        for (int i = 0; i < plan.Count; i++)
        {
            string candidate = Normalize(plan[i].Name);
            if (candidate.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || normalized.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                if (candidate.Length > bestMatchLength)
                {
                    bestMatch = i;
                    bestMatchLength = candidate.Length;
                }
            }
        }

        if (bestMatch >= 0)
        {
            return bestMatch;
        }

        string[] semanticTargets = normalized switch
        {
            _ when normalized.Contains("加载器", StringComparison.OrdinalIgnoreCase) => ["加载器"],
            _ when normalized.Contains("附加组件", StringComparison.OrdinalIgnoreCase) => ["附加组件"],
            _ when normalized.Contains("版本描述", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("版本信息", StringComparison.OrdinalIgnoreCase) => ["版本信息", "版本描述"],
            _ when normalized.Contains("客户端", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("资源", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("运行库", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("文件", StringComparison.OrdinalIgnoreCase)
                => ["游戏文件", "缺失文件", "下载内容", "下载资源"],
            _ when normalized.Contains("下载", StringComparison.OrdinalIgnoreCase)
                && !normalized.Contains("下载地址", StringComparison.OrdinalIgnoreCase)
                => ["下载资源", "下载内容", "下载游戏文件", "下载模组"],
            _ => [],
        };
        foreach (string target in semanticTargets)
        {
            for (int i = 0; i < plan.Count; i++)
            {
                if (Normalize(plan[i].Name).Contains(target, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        ReadOnlySpan<string> keywords =
        [
            "准备", "元数据", "版本信息", "版本描述", "前置", "下载", "游戏文件",
            "加载器", "附加组件", "校验", "验签", "重组", "解压", "安装", "应用", "完成", "就绪",
        ];
        foreach (string keyword in keywords)
        {
            if (!normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            for (int i = 0; i < plan.Count; i++)
            {
                if (Normalize(plan[i].Name).Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
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
