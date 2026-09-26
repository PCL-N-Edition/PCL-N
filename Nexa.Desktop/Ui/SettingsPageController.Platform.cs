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
        "形态", "输入", "Java", "加载器", "账户", "游戏文件", "游戏设置", "机器画像", "资源估算", "启动预检", "策略",
    ];
    private static readonly XsrSemanticId RefreshPlatform = XsrSemanticId.Parse("ui.settings.platform.refresh");
    private Task<XsrResult<MachineCapabilitySnapshot>>? _machineReading;
    private MachineCapabilityQuery? _machineReadingScope;
    private MachineCapabilityQuery _machineScope = new();
    private Task<XsrResult>? _machineRefreshing;
    private MachineCapabilitySnapshot? _machine;
    private long _machineSourceRevision = -1;
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
            _machineSourceRevision = -1;
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
        if (_machineReading is null && (_machine is null || _machineSourceRevision != revision) && _machineError is null && _queries.TryResolve(MachineCapabilityStateContract.SnapshotQuery, out var route))
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
        if (reading.IsCompletedSuccessfully && reading.Result.IsSuccess)
        {
            _machine = reading.Result.Value;
            _machineSourceRevision = _store.Read<long>(
                _store.Resolve(MachineCapabilityStateContract.RevisionKey)).Value;
        }
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
                .Where(value => value.Id is not "input.gyroscope.available" and not "input.haptics.available"
                    and not "mod.metadata.fingerprint" and not "minecraft.settings.fingerprint")
                .Where(value => value.Availability != CapabilityAvailability.NotImplemented
                    || value.Reason != "尚未接入检测提供方")
                .GroupBy(value => value.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            var orderedGroups = visible.Select(value => value.Definition.Group).Distinct()
                .OrderBy(group =>
                {
                    int index = Array.IndexOf(PlatformGroups, group);
                    return index < 0 ? int.MaxValue : index;
                }).ThenBy(group => group, StringComparer.CurrentCulture)
                .ToList();
            foreach (var group in orderedGroups)
            {
                Text(_sections, group, 12, Muted, 24, 600);
                var card = SettingsCard("PlatformCard." + group, new(16, 6, 16, 6));
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
        if (value is Capability<IReadOnlyList<InputDeviceFeature>> devices)
        {
            BuildRightListRow(card, value, devices.Value?.Select(device =>
                device.DeviceName + " · " + (device.Available ? "可用" : "不可用")) ?? []);
            return;
        }

        if (value is Capability<IReadOnlyList<string>> items
            && value.Id == "minecraft.settings.resource_packs")
        {
            BuildRightListRow(card, value, items.Value is { Count: > 0 } ? items.Value : ["默认资源"]);
            return;
        }

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
    }

    private void BuildRightListRow(XsrUiEntityId card, ICapability value, IEnumerable<string> values)
    {
        var row = Stack(card, "PlatformCapability." + value.Id, XsrUiOrientation.Horizontal, 12);
        _shell.Tree.GetComponent<XsrUiElement>(row)!.Padding = new(0, 7, 0, 7);
        var label = Text(row, value.Definition.Label, 14, Ink, 28);
        _shell.Tree.GetComponent<XsrUiElement>(label)!.Weight = 1;
        var list = Stack(row, "PlatformCapabilityList." + value.Id, XsrUiOrientation.Vertical, 2);
        _shell.Tree.GetComponent<XsrUiElement>(list)!.HorizontalAlignment = XsrUiAlignment.End;
        string[] entries = values.ToArray();
        if (entries.Length == 0) entries = ["未检测到已连接设备"];
        foreach (string entry in entries)
        {
            var text = Text(list, entry, 12, Muted, 22);
            _shell.Tree.GetComponent<XsrUiVisualStyle>(text)!.TextAlignment = XsrUiTextAlignment.End;
        }
    }

    /// <summary>The remediation panel: preflight is a PURE function over the snapshot this
    /// page already holds — evaluating inline never blocks the build path, never issues a
    /// query, and never re-collects machine state (the old route re-collected per render
    /// and fed the revision loop).</summary>
    private void BuildPreflightCard()
    {
        if (_machine is null) return;
        CapabilityPreflightReport report = CapabilityPreflightEngine.Evaluate(_machine);
        if (report.OverallSeverity == PreflightSeverity.None) return;

        Text(_sections, "诊断与修复", 12, Muted, 24, 600);
        var card = SettingsCard("PlatformPreflightCard", new(16, 6, 16, 6));
        Text(card, "整体状态：" + SeverityLabel(report.OverallSeverity), 13, Ink, 26, 600);
        foreach (var issue in report.CollapsedIssues)
        {
            var row = Stack(card, "PlatformIssue." + issue.Code, XsrUiOrientation.Horizontal, 12);
            _shell.Tree.GetComponent<XsrUiElement>(row)!.Padding = new(0, 6, 0, 6);
            var title = Text(row, issue.Title + "（" + SeverityLabel(issue.Severity) + "）", 13, Ink, 28);
            _shell.Tree.GetComponent<XsrUiElement>(title)!.Weight = 1;
            foreach (var remediationId in issue.Remediations.Take(1))
            {
                RemediationDefinition definition = RemediationCatalog.Get(remediationId);
                var button = ActionButton(row, "PlatformRemediation." + remediationId, definition.Label,
                    RemediationExecuted, 110);
                _shell.Tree.GetComponent<XsrUiInput>(button)!.Enabled =
                    _machine.Get<bool>(remediationId) is { Availability: CapabilityAvailability.Available, Value: true };
                _shell.Tree.GetComponent<XsrUiSemantic>(button)!.Label = definition.Label + "：" + issue.Title;
            }
        }
    }

    private static readonly XsrSemanticId RemediationExecuted = XsrSemanticId.Parse("ui.settings.platform.remediation");
    private static readonly XsrSemanticId RefreshPlatformField = RefreshPlatform;
    internal Func<Task<string?>>? PickRemediationJava { get; set; }

    /// <summary>Remediation button activation → typed execute route; the result surfaces as
    /// an in-window notification (definitions requiring confirmation dispatch confirmed).</summary>
    private void OnPlatformRemediation(object? sender, DesktopUiIntentEventArgs args)
    {
        if (_disposed || !_commands.TryResolve(MachineCapabilityStateContract.RemediationCommand, out var route)) return;
        // The id rides the source entity name: "PlatformRemediation.<remediation-id>".
        string name = _shell.Tree.Name(args.Intent.Source);
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        string id = dot > 0 ? name[(dot + 1)..] : name;
        Dictionary<string, string> arguments = new(StringComparer.Ordinal);
        if (_machine?.Get<long>("estimate.heap.recommended") is { Availability: CapabilityAvailability.Available } memory)
            arguments["memoryMiB"] = memory.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(_machineScope.InstanceDirectory))
            arguments["instanceDirectory"] = _machineScope.InstanceDirectory;
        void Execute()
        {
            _ = ExecuteRemediationAsync(id, arguments, route);
        }
        if (RemediationCatalog.Get(id).RequiresConfirmation)
            _feedback.ShowDialog("preflight.confirm." + id, RemediationCatalog.Get(id).Label, "将对当前选中的实例执行此操作。", "继续", "取消", accepted => { if (accepted) Execute(); });
        else Execute();
    }

    private async Task ExecuteRemediationAsync(string id, Dictionary<string, string> arguments, XsrCommandId route)
    {
        try
        {
            if (id == "remediation.java.select")
            {
                if (PickRemediationJava is null) return;
                string? executable = await PickRemediationJava().ConfigureAwait(false);
                if (executable is null || _disposed) return;
                arguments["executable"] = executable;
            }
            var result = await _commands.Dispatch(route, new RemediationRequest(id, arguments, Confirmed: true)).Completion.ConfigureAwait(false);
            if (_disposed) return;
            if (result.IsSuccess) _feedback.Info("修复操作已完成。");
            else _feedback.Error(result.Error?.Message ?? "修复操作未完成。");
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { if (!_disposed) _feedback.Error("修复操作未完成，请查看任务详情。"); }
    }

    private static string SeverityLabel(PreflightSeverity severity) => severity switch
    {
        PreflightSeverity.Information => "提示",
        PreflightSeverity.Warning => "警告",
        PreflightSeverity.Critical => "严重",
        PreflightSeverity.Blocked => "阻止启动",
        _ => "正常",
    };

}
