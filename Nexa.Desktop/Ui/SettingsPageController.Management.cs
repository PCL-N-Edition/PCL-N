using Nexa.Services.Minecraft.Management;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private static readonly XsrSemanticId ManagementAction = XsrSemanticId.Parse("ui.settings.management.action");
    private InstanceManagementSnapshot? _management;
    private Task<XsrResult<InstanceManagementSnapshot>>? _managementRead;
    private CancellationTokenSource? _managementStop;
    private bool _managementLoaded;
    private string? _managementError;
    private readonly Dictionary<XsrUiEntityId, Action> _managementActions = [];
    private readonly List<XsrUiEntityId> _contentActions = [];
    private Task<XsrResult>? _managementWrite;
    private string? _managementWriteInstance;
    private XsrUiEntityId _contentList;
    private InstanceContentSnapshot? _contentSnapshot;
    private int _contentWindowStart = -1, _contentWindowCount;
    internal Action<string>? OpenManagementDirectory { get; set; }
    private IReadOnlyList<SettingsCatalogPage> ManagementPages => _management is { } snapshot
        ? snapshot.Pages.Select(page => new SettingsCatalogPage(page.Id, page.Label)).ToArray()
        : [new("overview", "总览"), new("game", "游戏设置"), new("java", "Java")];

    private void CancelManagementRead()
    {
        _managementStop?.Cancel(); _managementStop?.Dispose(); _managementStop = null;
        _managementRead = null; _managementLoaded = false;
    }

    private void ResetManagement()
    {
        if (_instanceDirectory is null) return;
        CancelManagementRead(); _management = null; _managementError = null;
        _selected = "overview"; _scrollPositions.Clear();
        if (_catalog is not null) RebuildManagementNavigation();
    }

    private void UpdateManagement()
    {
        if (_instanceDirectory is null || _instance is null) return;
        if (_managementWrite is { IsCompleted: true } writing)
        {
            _managementWrite = null;
            if (_managementWriteInstance == _instance)
            {
                if (!writing.IsCompletedSuccessfully || !writing.Result.IsSuccess)
                    _feedback.Error(writing.IsCompletedSuccessfully ? writing.Result.Error?.Message ?? "模组状态未保存。" : "模组状态未保存。");
                CancelManagementRead();
            }
        }
        if (!_managementLoaded && _managementRead is null && _queries.TryResolve(InstanceManagementContract.Query, out var route))
        {
            _managementStop = new();
            _managementRead = _queries.QueryAsync<InstanceManagementQuery, InstanceManagementSnapshot>(route,
                new(_instance), cancellationToken: _managementStop.Token).AsTask();
            WakeOnPlatformCompletion(_managementRead);
        }
        if (_managementRead is not { IsCompleted: true } reading) return;
        _managementRead = null; _managementLoaded = true;
        _managementStop?.Dispose(); _managementStop = null;
        if (reading.IsCompletedSuccessfully && reading.Result.IsSuccess
            && reading.Result.Value!.InstanceDirectory == _instance)
        {
            _management = reading.Result.Value; _managementError = null;
            if (!ManagementPages.Any(page => page.Id == _selected)) _selected = "overview";
            RebuildManagementNavigation();
        }
        else _managementError = "无法读取版本内容，请检查目录后重试。";
        BuildSections(navigating: _selected is not ("game" or "java")); UpdateEditors();
    }

    private void RebuildManagementNavigation()
    {
        foreach (var page in _pages.Keys.Where(id => !Pages.Any(item => item.Id == id)).ToArray())
        { _shell.Tree.Destroy(_pages[page]); _pages.Remove(page); }
        foreach (var entity in _navigation.Keys) _shell.Tree.Destroy(entity);
        _navigation.Clear();
        _shell.Tree.SetComponent(_pager, new XsrUiPager(XsrUiOrientation.Horizontal));
        BuildNavigation();
        _shell.Renderer.RebasePagerPage(_pager, Pages.ToList().FindIndex(page => page.Id == _selected));
    }

    private void ManagementButton(XsrUiEntityId parent, string label, Action action, double width = 84)
    {
        var button = ActionButton(parent, "Management." + label, label, ManagementAction, width);
        _managementActions[button] = action;
    }

    private void OpenContentDirectory(string directory)
    {
        try { OpenManagementDirectory?.Invoke(directory); }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { _feedback.Error("无法打开目录，请检查目录是否存在。"); }
    }

    private void ToggleMod(InstanceContentEntry entry)
    {
        if (_managementWrite is not null || _managementRead is not null || !_managementLoaded || _management is null || entry.Enabled is not { } enabled || entry.Size is not { } size
            || !_commands.TryResolve(InstanceManagementContract.SetModEnabled, out var route)) return;
        _managementWriteInstance = _management.InstanceDirectory;
        _managementWrite = _commands.Dispatch(route, new InstanceModEnabledCommand(_managementWriteInstance, entry.Name, !enabled, size, entry.ModifiedUtcTicks)).Completion;
        WakeOnPlatformCompletion(_managementWrite);
    }

    private void BuildManagementSection()
    {
        var toolbar = Stack(_sections, "ManagementToolbar", XsrUiOrientation.Horizontal, 10);
        var title = Text(toolbar, Pages.First(page => page.Id == _selected).Label, 20, Ink, 34, 600);
        _shell.Tree.GetComponent<XsrUiElement>(title)!.Weight = 1;
        ManagementButton(toolbar, "刷新", () => { CancelManagementRead(); _managementError = null; }, 60);
        if (_management is not { } snapshot)
        {
            Text(_sections, _managementError ?? "正在读取版本内容…", 13, Muted, 28);
            return;
        }
        if (_managementError is not null) Text(_sections, _managementError, 13, Muted, 28);
        var page = snapshot.Pages.First(item => item.Id == _selected);
        if (page.Directory is { } directory)
        {
            if (OpenManagementDirectory is not null) ManagementButton(toolbar, "打开文件夹", () => OpenContentDirectory(directory), 100);
            Text(_sections, directory, 12, Muted, 24);
            _contentSnapshot = snapshot.Contents.FirstOrDefault(item => item.PageId == _selected);
            if (_contentSnapshot?.Error is { } error) Text(_sections, error, 13, Muted, 28);
            else if (_contentSnapshot is not { Entries.Count: > 0 }) Text(_sections, "此目录中还没有内容。", 13, Muted, 28);
            else
            {
                Text(_sections, _contentSnapshot.Complete ? $"{_contentSnapshot.Entries.Count} 项" : $"显示前 {_contentSnapshot.Entries.Count} 项，请在文件夹中查看其余内容。", 12, Muted, 22);
                _contentList = Stack(_sections, "ManagementContentList", XsrUiOrientation.Vertical, 0);
                UpdateContentWindow();
            }
        }
        else if (_selected == "overview")
        {
            ManagementFact("版本", System.IO.Path.GetFileName(snapshot.InstanceDirectory));
            ManagementFact("Minecraft", snapshot.GameVersion);
            ManagementFact("游戏目录", snapshot.GameDirectory);
            if (snapshot.Description.Length > 0) ManagementFact("描述", snapshot.Description);
            if (OpenManagementDirectory is not null) ManagementButton(_sections, "打开版本文件夹", () => OpenContentDirectory(snapshot.InstanceDirectory), 128);
        }
        else if (_selected == "components")
        {
            ManagementFact("Minecraft", snapshot.GameVersion);
            foreach (var component in snapshot.Components) ManagementFact(component.Loader.ToString(), component.Version);
            if (snapshot.Components.Count == 0) Text(_sections, "此版本未安装加载器。", 13, Muted, 28);
            ManagementButton(_sections, "修改组件", () => _intents.Emit(XsrSemanticId.Parse("ui.launch.modify"), _sections, XsrCorrelationId.Create()), 100);
        }
        else if (_selected == "modpack")
        {
            ManagementFact("整合包版本", string.IsNullOrEmpty(snapshot.ModpackVersion) ? "未记录" : snapshot.ModpackVersion);
            Text(_sections, "整合包导出功能尚未迁移。", 13, Muted, 28);
        }
        else if (_selected == "servers")
            Text(_sections, "服务器列表管理尚未迁移。", 13, Muted, 28);
        _shell.Tree.GetComponent<XsrUiScroll>(_sections)!.OffsetY = _scrollPositions.GetValueOrDefault(_selected);
        _shell.Tree.MarkDirty(_sections, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    private void ManagementFact(string label, string value)
    {
        var row = Stack(_sections, "ManagementFact", XsrUiOrientation.Horizontal, 20);
        var caption = Text(row, label, 13, Muted, 32);
        _shell.Tree.GetComponent<XsrUiElement>(caption)!.Width = 120;
        var content = Text(row, value, 14, Ink, 32);
        _shell.Tree.GetComponent<XsrUiElement>(content)!.Weight = 1;
    }

    private void UpdateContentWindow()
    {
        if (!_contentList.IsAssigned || !_shell.Tree.IsAlive(_contentList) || _contentSnapshot is not { } snapshot) return;
        const double rowHeight = 44;
        int start = Math.Clamp((int)((_shell.Tree.GetComponent<XsrUiScroll>(_sections)!.OffsetY - 140) / rowHeight) - 4, 0, Math.Max(0, snapshot.Entries.Count - 1));
        int count = Math.Min(snapshot.Entries.Count - start, (int)Math.Ceiling(_shell.Renderer.Viewport.Height / rowHeight) + 10);
        if (_contentWindowStart == start && _contentWindowCount == count) return;
        _contentWindowStart = start; _contentWindowCount = count;
        foreach (var action in _contentActions) _managementActions.Remove(action);
        _contentActions.Clear();
        foreach (var child in _shell.Tree.Children(_contentList).ToArray()) _shell.Tree.Destroy(child);
        Element(_contentList, "ManagementContentBefore", XsrUiSemanticRole.None, null, height: start * rowHeight);
        foreach (var item in snapshot.Entries.Skip(start).Take(count))
        {
            var row = Stack(_contentList, "ManagementContentRow", XsrUiOrientation.Horizontal, 16);
            _shell.Tree.GetComponent<XsrUiElement>(row)!.Height = rowHeight;
            var name = Text(row, item.Name, 14, Ink, 30);
            _shell.Tree.GetComponent<XsrUiElement>(name)!.Weight = 1;
            var size = Text(row, item.IsDirectory ? "文件夹" : item.Size is { } bytes ? $"{bytes / 1024d:N1} KB" : "", 12, Muted, 30);
            _shell.Tree.GetComponent<XsrUiElement>(size)!.Width = 100;
            _shell.Tree.GetComponent<XsrUiVisualStyle>(size)!.TextAlignment = XsrUiTextAlignment.End;
            if (item.Enabled is { } enabled)
            {
                var button = ActionButton(row, "ManagementModToggle." + item.Name, enabled ? "停用" : "启用", ManagementAction, 64);
                _managementActions[button] = () => ToggleMod(item);
                _contentActions.Add(button);
            }
        }
        Element(_contentList, "ManagementContentAfter", XsrUiSemanticRole.None, null, height: (snapshot.Entries.Count - start - count) * rowHeight);
        _shell.Tree.MarkDirty(_contentList, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }
}
