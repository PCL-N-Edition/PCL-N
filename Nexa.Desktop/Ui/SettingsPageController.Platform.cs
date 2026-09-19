using Nexa.Services.Capabilities;
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
    private static readonly string[] PlatformGroups = ["系统", "处理器", "内存", "运行环境", "待接入能力"];
    private static readonly XsrSemanticId RefreshPlatform = XsrSemanticId.Parse("ui.settings.platform.refresh");
    private Task<XsrResult<MachineCapabilitySnapshot>>? _machineReading;
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
            _machineReading = _queries.QueryAsync<MachineCapabilityQuery, MachineCapabilitySnapshot>(route, new()).AsTask();
            WakeOnPlatformCompletion(_machineReading);
        }
        if (_machineReading is not { IsCompleted: true } reading) return;
        _machineReading = null;
        if (reading.IsCompletedSuccessfully && reading.Result.IsSuccess) _machine = reading.Result.Value;
        else _machineError = "无法读取平台功能，请重试。";
        BuildSections();
    }
    private void BuildPlatformCapabilities()
    {
        var toolbar = Stack(_sections, "PlatformToolbar", XsrUiOrientation.Horizontal, 12);
        var status = Text(toolbar, _machineError ?? (_machineRefreshing is not null || _machine is null ? "正在检测平台功能…" : "检测时间：" + _machine.Timestamp.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture)), 12, Muted, 32);
        _shell.Tree.GetComponent<XsrUiElement>(status)!.Weight = 1;
        var refresh = ActionButton(toolbar, "PlatformRefresh", "重新检测", RefreshPlatform, 84);
        _shell.Tree.GetComponent<XsrUiInput>(refresh)!.Enabled = _machineRefreshing is null;
        if (_machine is not null)
            foreach (var group in _machine.Values.GroupBy(value => value.Definition.Group).OrderBy(group => Array.IndexOf(PlatformGroups, group.Key)))
            {
                Text(_sections, group.Key, 12, Muted, 24, 600);
                var card = Stack(_sections, "PlatformCard." + group.Key, XsrUiOrientation.Vertical, 0);
                Style(card, White, Ink, 12);
                _shell.Tree.GetComponent<XsrUiElement>(card)!.Padding = new(14, 6, 14, 6);
                foreach (var value in group)
                {
                    var row = Stack(card, "PlatformCapability." + value.Id, XsrUiOrientation.Vertical, 1);
                    _shell.Tree.GetComponent<XsrUiElement>(row)!.Padding = new(0, 7, 0, 7);
                    var headline = Stack(row, "PlatformCapabilityHeadline", XsrUiOrientation.Horizontal, 12);
                    _shell.Tree.GetComponent<XsrUiElement>(headline)!.Padding = new(0, 0, 12, 0);
                    var label = Text(headline, value.Definition.Label, 14, Ink, 28);
                    _shell.Tree.GetComponent<XsrUiElement>(label)!.Weight = 1;
                    Text(headline, value.Availability == CapabilityAvailability.Available ? value.DisplayValue : value.Reason, 12, Muted, 28);
                    string evidence = value.Availability == CapabilityAvailability.Available ? value.Source : value.Id;
                    Text(row, evidence + " · " + value.Definition.Provider, 11, Muted, 22);
                }
            }
        _shell.Tree.GetComponent<XsrUiScroll>(_sections)!.OffsetY = _scrollPositions.GetValueOrDefault("platform");
        _shell.Tree.MarkDirty(_sections, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }
}
