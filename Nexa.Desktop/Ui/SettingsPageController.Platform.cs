using Nexa.Services.Capabilities;
using Nexa.Services.Minecraft;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

internal static class SettingsPresentationState
{
    public static readonly XsrSemanticId WakeKey = XsrSemanticId.Parse("ui.settings.wake");
    public static void DeclareState(XsrStateStoreBuilder builder) => builder.Cell<long>(WakeKey, "Nexa.Desktop.Settings");
}

internal sealed partial class SettingsPageController
{
    // Preferred section order; snapshot groups this table omits append at the end instead
    // of piling at the top (the old IndexOf ordering sent every new group to position -1).
    private static readonly string[] PlatformGroups =
    [
        "系统", "运行环境", "处理器", "内存", "显卡", "存储", "文件系统", "显示", "电源", "散热",
        "形态", "输入", "Java", "加载器", "账户", "游戏文件", "游戏设置", "机器画像", "估算", "策略",
    ];
    private static readonly XsrSemanticId RefreshPlatform = XsrSemanticId.Parse("ui.settings.platform.refresh");
    private Task<XsrResult<MachineCapabilitySnapshot>>? _machineReading;
    private MachineCapabilityQuery? _machineReadingScope;
    private MachineCapabilityQuery _machineScope = new();
    private Task<XsrResult>? _machineRefreshing;
    private MachineCapabilitySnapshot? _machine;
    private string? _machineError;
    private long _platformWakeRevision;
    private void WakeOnPlatformCompletion(Task task)
    {
        _ = task.ContinueWith(_ =>
        {
            if (_disposed) return;
            try { _store.Publish(_store.Resolve(SettingsPresentationState.WakeKey), Interlocked.Increment(ref _platformWakeRevision)); }
            catch (ObjectDisposedException) { }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void RefreshPlatformCapabilities()
    {
        if (_machineRefreshing is not null || !_commands.TryResolve(MachineCapabilityStateContract.RefreshCommand, out var route)) return;
        _machineError = null;
        _machineRefreshing = _commands.Dispatch(route, new MachineCapabilityRefresh()).Completion;
        WakeOnPlatformCompletion(_machineRefreshing);
        if (_selected == "platform") BuildSections();
    }
    private void UpdatePlatformCapabilities()
    {
        MachineCapabilityQuery selectedScope = ResolveMachineScope();
        if (selectedScope != _machineScope)
        {
            _machineScope = selectedScope;
            _machine = null;
            _machineError = null;
        }
        if (_machineRefreshing is { IsCompleted: true } refreshed)
        {
            _machineRefreshing = null;
            if (!refreshed.IsCompletedSuccessfully || !refreshed.Result.IsSuccess) { _machineError = "检测未完成，请重试。"; if (_selected == "platform") BuildSections(); }
            else _machine = null;
        }
        if (_selected != "platform" || !_sections.IsAssigned) return;
        long revision = _store.Read<long>(_store.Resolve(MachineCapabilityStateContract.RevisionKey)).Value;
        if (_machineReading is null && (_machine is null || _machine.Revision != revision) && _machineError is null && _queries.TryResolve(MachineCapabilityStateContract.SnapshotQuery, out var route))
        {
            _machineReadingScope = _machineScope;
            _machineReading = _queries.QueryAsync<MachineCapabilityQuery, MachineCapabilitySnapshot>(route, _machineScope).AsTask();
            WakeOnPlatformCompletion(_machineReading);
        }
        if (_machineReading is not { IsCompleted: true } reading) return;
        _machineReading = null;
        if (_machineReadingScope != _machineScope)
        {
            _machineReadingScope = null;
            return;
        }
        _machineReadingScope = null;
        if (reading.IsCompletedSuccessfully && reading.Result.IsSuccess) _machine = reading.Result.Value;
        else _machineError = "无法读取平台功能，请重试。";
        BuildSections();
    }

    internal MachineCapabilityQuery CurrentMachineScope => ResolveMachineScope();

    private MachineCapabilityQuery ResolveMachineScope()
    {
        if (_store.ReadAppliedValue(_store.Resolve(MinecraftLibraryService.StateKey))
            is not MinecraftLibrarySnapshot library)
        {
            return new();
        }

        MinecraftInstanceDescriptor? instance = library.SelectedInstance;
        return instance is null
            ? new(MinecraftRootDirectory: library.RootDirectory)
            : new(instance.DirectoryPath, instance.Id, library.RootDirectory);
    }
    private void BuildPlatformCapabilities()
    {
        var toolbar = Stack(_sections, "PlatformToolbar", XsrUiOrientation.Horizontal, 12);
        var status = Text(toolbar, _machineError ?? (_machineRefreshing is not null || _machine is null ? "正在检测平台功能…" : "检测时间：" + _machine.Timestamp.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture)), 12, Muted, 32);
        _shell.Tree.GetComponent<XsrUiElement>(status)!.Weight = 1;
        var refresh = ActionButton(toolbar, "PlatformRefresh", "重新检测", RefreshPlatform, 84);
        _shell.Tree.GetComponent<XsrUiInput>(refresh)!.Enabled = _machineRefreshing is null;

        if (_machine is not null)
        {
            BuildPreflightCard();
            // Real facts only: definitions with no wired provider are registry placeholders
            // (they rendered as endless 尚未接入检测提供方 rows), and remediation-style
            // Action definitions are operations, not observations.
            var visible = _machine.Values
                .Where(value => value.Definition.Kind != CapabilityKind.Action && value.Definition.Kind != CapabilityKind.Policy)
                .Where(value => value.Availability != CapabilityAvailability.NotImplemented
                    || value.Reason != "尚未接入检测提供方")
                .GroupBy(value => value.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            var orderedGroups = visible.Select(value => value.Definition.Group).Distinct()
                .OrderBy(group => Array.IndexOf(PlatformGroups, group)).ThenBy(group => group, StringComparer.CurrentCulture)
                .ToList();
            foreach (var group in orderedGroups)
            {
                Text(_sections, group, 12, Muted, 24, 600);
                var card = Stack(_sections, "PlatformCard." + group, XsrUiOrientation.Vertical, 0);
                Style(card, White, Ink, 12);
                _shell.Tree.GetComponent<XsrUiElement>(card)!.Padding = new(16, 6, 16, 6);
                foreach (var value in visible.Where(value => value.Definition.Group == group))
                    BuildCapabilityRow(card, value);
            }
        }

        _shell.Tree.GetComponent<XsrUiScroll>(_sections)!.OffsetY = _scrollPositions.GetValueOrDefault("platform");
        _shell.Tree.MarkDirty(_sections, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    /// <summary>Status badge colors: gray pill background for every state.</summary>
    private static readonly XsrUiColor BadgeGray = new(245, 247, 250);

    private static (string Text, XsrUiColor Foreground) StatusText(ICapability value) => value.Availability switch
    {
        CapabilityAvailability.Available => (string.IsNullOrWhiteSpace(value.DisplayValue) ? "—" : LocalizeValue(value.DisplayValue), Ink),
        CapabilityAvailability.DependencyMissing => (ShortReason(value, "待前置能力"), Muted),
        CapabilityAvailability.PlatformUnsupported => (ShortReason(value, "平台不支持"), Muted),
        CapabilityAvailability.NotImplemented => (ShortReason(value, "尚未接入"), Muted),
        CapabilityAvailability.TemporarilyUnavailable => (ShortReason(value, "暂不可用"), Muted),
        CapabilityAvailability.Unknown => (ShortReason(value, "未知"), Muted),
        _ => ("—", Muted),
    };

    /// <summary>Enum-shaped values the services publish in English; the badge shows Chinese.</summary>
    private static string LocalizeValue(string value) => value switch
    {
        "true" => "是",
        "false" => "否",
        "Low" => "低",
        "Medium" => "中",
        "High" => "高",
        "NotStarted" => "未开始",
        "Pending" => "进行中",
        "Completed" => "已完成",
        "Failed" => "失败",
        "Verified" => "已验证",
        "Estimated" => "估算",
        "Inferred" => "推断",
        "Unknown" => "未知",
        "Desktop" => "台式机",
        "Laptop" => "笔记本电脑",
        "Handheld" => "掌机",
        "Win32" => "Windows",
        "X64" => "x64",
        "ARM64" => "ARM64",
        "Offline" => "离线",
        "Microsoft" => "微软账户",
        "LittleSkin" => "LittleSkin",
        "ThirdParty" => "第三方",
        "NCloud" => "NCloud",
        _ => value,
    };

    /// <summary>Reasons run long; the badge shows the leading clause, the row keeps the rest.</summary>
    private static string ShortReason(ICapability value, string fallback)
    {
        if (string.IsNullOrEmpty(value.Reason)) return fallback;
        int colon = value.Reason.IndexOf('：');
        return colon > 0 ? value.Reason[..colon] : value.Reason;
    }

    private void BuildCapabilityRow(XsrUiEntityId card, ICapability value)
    {
        var row = Stack(card, "PlatformCapability." + value.Id, XsrUiOrientation.Vertical, 1);
        _shell.Tree.GetComponent<XsrUiElement>(row)!.Padding = new(0, 7, 0, 7);
        var headline = Stack(row, "PlatformCapabilityHeadline", XsrUiOrientation.Horizontal, 12);
        var label = Text(headline, value.Definition.Label, 14, Ink, 28);
        _shell.Tree.GetComponent<XsrUiElement>(label)!.Weight = 1;

        // Badge: gray pill, right aligned, text centered — values and unavailability states
        // share one shape so the column reads as a single thing.
        (string badgeText, XsrUiColor badgeInk) = StatusText(value);
        var badge = Text(headline, badgeText, 12, badgeInk, 28);
        Style(badge, BadgeGray, badgeInk, 6, 12);
        var badgeElement = _shell.Tree.GetComponent<XsrUiElement>(badge)!;
        badgeElement.MinWidth = 96;
        badgeElement.MaxWidth = 330;
        badgeElement.HorizontalAlignment = XsrUiAlignment.End;
        _shell.Tree.GetComponent<XsrUiVisualStyle>(badge)!.TextAlignment = XsrUiTextAlignment.Center;

        // Users see the data source (or the reason), never capability ids or provider names.
        string evidence = value.Availability == CapabilityAvailability.Available
            ? value.Source
            : value.Reason;
        Text(row, evidence, 11, Muted, 22);
        BuildDetailRows(row, value);
    }

    /// <summary>Vertical detail list under a row for device/list-shaped facts (Registry §16:
    /// per-device availability; resource packs by name — never raw bracketed JSON).</summary>
    private void BuildDetailRows(XsrUiEntityId row, ICapability value)
    {
        if (value.Id is "input.gyroscope.available" or "input.haptics.available")
        {
            int controllers = ReadInt("input.controller.count");
            if (controllers <= 0)
            {
                Text(row, "未检测到已连接设备", 12, Muted, 22);
                return;
            }

            string channel = value.Id.StartsWith("input.gyroscope", StringComparison.Ordinal) ? "陀螺仪" : "振动反馈";
            for (int device = 1; device <= controllers && device <= 4; device++)
            {
                Text(row, $"手柄 {device} · {channel}：未接入（需逐设备通道）", 12, Muted, 22);
            }

            return;
        }

        if (value.Id is "minecraft.settings.resource_packs" && value.Availability == CapabilityAvailability.Available)
        {
            string display = value.DisplayValue;
            int split = display.IndexOf('：');
            if (split > 0)
            {
                foreach (string name in display[(split + 1)..].Split('、', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string item = name.EndsWith(" 等", StringComparison.Ordinal) ? name[..^2] : name;
                    Text(row, "· " + item, 12, Muted, 22);
                }
            }
        }
    }

    private int ReadInt(string id) =>
        (_machine?.Get<int>(id))?.Value is { } parsed ? parsed : 0;

    /// <summary>The remediation panel: live preflight over the same snapshot scope, one row
    /// per issue with its bound remediation action (Registry §50/§54 — the "修复" board).</summary>
    private void BuildPreflightCard()
    {
        if (_machine is null || !_queries.TryResolve(MachineCapabilityStateContract.PreflightQuery, out var route)) return;
        var report = _queries.QueryAsync<LaunchPreflightQuery, CapabilityPreflightReport>(
            route, new(_machineScope.InstanceDirectory, _machineScope.InstanceId, _machineScope.MinecraftRootDirectory)).AsTask().GetAwaiter().GetResult();
        if (report is not { IsSuccess: true } || report.Value.OverallSeverity == PreflightSeverity.None) return;

        Text(_sections, "诊断与修复", 12, Muted, 24, 600);
        var card = Stack(_sections, "PlatformPreflightCard", XsrUiOrientation.Vertical, 0);
        Style(card, White, Ink, 12);
        _shell.Tree.GetComponent<XsrUiElement>(card)!.Padding = new(16, 6, 16, 6);
        Text(card, "整体状态：" + SeverityLabel(report.Value.OverallSeverity), 13, Ink, 26, 600);
        foreach (var issue in report.Value.Issues)
        {
            var row = Stack(card, "PlatformIssue." + issue.Code, XsrUiOrientation.Horizontal, 12);
            _shell.Tree.GetComponent<XsrUiElement>(row)!.Padding = new(0, 6, 0, 6);
            var title = Text(row, IssueLabel(issue.Code) + "（" + SeverityLabel(issue.Severity) + "）", 13, Ink, 28);
            _shell.Tree.GetComponent<XsrUiElement>(title)!.Weight = 1;
            foreach (var remediationId in issue.Remediations.Take(1))
            {
                RemediationDefinition definition = RemediationCatalog.Get(remediationId);
                var button = ActionButton(row, "PlatformRemediation." + remediationId, definition.Label,
                    RemediationExecuted, 110);
                _shell.Tree.GetComponent<XsrUiSemantic>(button)!.Label = definition.Label + "（" + issue.Code + "）";
            }
        }
    }

    private static readonly XsrSemanticId RemediationExecuted = XsrSemanticId.Parse("ui.settings.platform.remediation");
    private static readonly XsrSemanticId RefreshPlatformField = RefreshPlatform;

    /// <summary>Remediation button activation → typed execute route; the result surfaces as
    /// an in-window notification (definitions requiring confirmation dispatch confirmed).</summary>
    private void OnPlatformRemediation(object? sender, DesktopUiIntentEventArgs args)
    {
        if (_disposed || !_commands.TryResolve(MachineCapabilityStateContract.RemediationCommand, out var route)) return;
        // The id rides the source entity name: "PlatformRemediation.<remediation-id>".
        string name = _shell.Tree.Name(args.Intent.Source);
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        string id = dot > 0 ? name[(dot + 1)..] : name;
        _ = _commands.Dispatch(route, new RemediationRequest(id, Confirmed: true)).Completion.ContinueWith(task =>
        {
            string message = task.IsCompletedSuccessfully && task.Result.IsSuccess
                ? "修复操作已执行。"
                : "修复操作未能执行：" + (task.IsCompletedSuccessfully ? task.Result.Error?.Message : "调度失败");
            _feedback?.Info(message);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private static string SeverityLabel(PreflightSeverity severity) => severity switch
    {
        PreflightSeverity.Information => "提示",
        PreflightSeverity.Warning => "警告",
        PreflightSeverity.Critical => "严重",
        PreflightSeverity.Blocked => "阻止启动",
        _ => "正常",
    };

    private static string IssueLabel(string code) => code switch
    {
        "JAVA_MISSING" => "缺少可用 Java",
        "JAVA_HARD_INCOMPATIBLE" => "Java 与当前实例硬性不兼容",
        "JAVA_NON_RECOMMENDED" => "Java 不是推荐版本",
        "JAVA_ARCH_INCOMPATIBLE" => "Java 架构不匹配",
        "JAVA_EMULATED" => "Java 运行在架构模拟下",
        "LOADER_MISSING" => "实例缺少模组加载器",
        "LOADER_INCOMPATIBLE" => "加载器与 Minecraft 不兼容",
        "MOD_REQUIRED_DEPENDENCY_MISSING" => "模组缺少必需依赖",
        "MOD_HARD_CONFLICT" => "模组存在硬性冲突",
        "MOD_LOADER_INCOMPATIBLE" => "模组与加载器不兼容",
        "MOD_MC_VERSION_INCOMPATIBLE" => "模组与 Minecraft 版本不兼容",
        "MOD_COMPAT_WARNING" => "模组兼容性警告",
        "MOD_COMPAT_CRITICAL" => "模组兼容性严重",
        "MEM_HEAP_LAUNCH_LOW" => "启动堆内存不足",
        "MEM_HEAP_RUNTIME_LOW" => "运行堆内存不足",
        "MEM_HEAP_ABOVE_PHYSICAL_AVAILABLE" => "堆内存超过可用物理内存",
        "MEM_HEAP_BELOW_HARD_MINIMUM" => "堆内存低于硬性下限",
        "MEM_PHYSICAL_LAUNCH_LOW" => "可用物理内存低于预计启动需求",
        "MEM_PHYSICAL_RUNTIME_LOW" => "可用物理内存偏低",
        "MEM_PHYSICAL_SEVERE" => "物理内存严重不足",
        "MEM_COMMIT_LAUNCH_LOW" => "提交预算低于预计启动需求",
        "MEM_COMMIT_RUNTIME_LOW" => "提交预算偏低",
        "MEM_COMMIT_GROWTH_REQUIRED" => "需要扩大提交预算",
        "MEM_COMMIT_NEAR_LIMIT" => "提交预算接近上限",
        "MEM_COMMIT_HARD_LIMIT" => "提交预算达到上限",
        "MEM_PAGEFILE_DISK_LOW" => "页面文件所在磁盘空间不足",
        "MEM_PAGEFILE_FIXED_MAX" => "页面文件已固定上限",
        "MEM_ESTIMATE_PENDING" => "资源估算尚未完成",
        "MEM_ESTIMATE_FAILED" => "资源估算失败",
        "MEM_ESTIMATE_LOW_CONFIDENCE" => "资源估算置信度低",
        "ESTIMATE_MODIFIED_CORE" => "核心文件被修改，估算置信度低",
        "ESTIMATE_SETTINGS_UNREADABLE" => "游戏设置不可读，估算置信度低",
        "ESTIMATE_UNKNOWN_MOD_PROFILE" => "存在未知模组画像",
        "GPU_LOW_PERFORMANCE_ADAPTER" => "当前使用低性能显卡",
        "GPU_VRAM_RUNTIME_LOW" => "显存运行余量不足",
        "GPU_VRAM_LAUNCH_CRITICAL" => "显存低于启动需求",
        "GPU_TEMPERATURE_HIGH" => "显卡温度偏高",
        "GPU_TEMPERATURE_NEAR_LIMIT" => "显卡温度接近上限",
        "CPU_TEMPERATURE_HIGH" => "处理器温度偏高",
        "CPU_TEMPERATURE_NEAR_LIMIT" => "处理器温度接近上限",
        "INPUT_TOUCH_SUPPORT_MISSING" => "当前以触屏为主但游戏缺少触屏支持",
        "INPUT_CONTROLLER_SUPPORT_MISSING" => "当前以手柄为主但游戏缺少手柄支持",
        "GAME_FILES_MISSING" => "游戏文件缺失",
        "GAME_FILES_CORRUPTED" => "游戏文件损坏",
        "GAME_REPAIR_FAILED" => "游戏文件修复失败",
        "GAME_METADATA_INVALID" => "版本元数据无效",
        "GAME_MAIN_CLASS_MISSING" => "主类缺失",
        "GAME_CLASSPATH_UNRESOLVED" => "类路径无法解析",
        "GAME_NATIVE_INCOMPATIBLE" => "本地库与平台不兼容",
        "STORAGE_SPACE_LOW" => "磁盘空间不足",
        "STORAGE_SPACE_CRITICAL" => "磁盘空间严重不足",
        "INSTANCE_PATH_UNAVAILABLE" => "实例路径不可用",
        "INSTANCE_PATH_NOT_WRITABLE" => "实例路径不可写",
        "ACCOUNT_REQUIRED" => "启动需要账户",
        "ACCOUNT_AUTH_FAILED" => "账户认证失败",
        "HOOK_REQUIRED_FAILED" => "必需的启动前钩子失败",
        "OS_UNSUPPORTED" => "操作系统不受支持",
        "UX_DATA_COLLECTION" => "诊断数据收集说明",
        _ => "检测到待处理事项",
    };
}
