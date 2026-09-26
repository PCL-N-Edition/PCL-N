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
    private int _trashPage;
    private XsrUiEntityId _contentSearch;
    private string _contentFilter = "";
    private InstanceContentEntry? _contentDetail;
    private double _contentReturnOffset;
    private int _contentColumns;
    internal Action<string>? OpenManagementDirectory { get; set; }
    internal Action? ManagementChanged { get; set; }
    private IReadOnlyList<SettingsCatalogPage> ManagementPages => _management is { } snapshot
        ? snapshot.Pages.Select(page => new SettingsCatalogPage(page.Id, page.Label)).ToArray()
        : [new("overview", "总览"), new("game", "游戏设置"), new("recovery", "快照与存储")];

    private void CancelManagementRead()
    {
        _managementStop?.Cancel(); _managementStop?.Dispose(); _managementStop = null;
        _managementRead = null; _managementLoaded = false;
    }

    private void ResetManagement()
    {
        if (_instanceDirectory is null) return;
        _contentDetail = null; _contentFilter = "";
        CancelManagementRead(); _management = null; _managementError = null;
        _selected = "overview"; _scrollPositions.Clear(); _recoverySnapshotPage = 0; _recoveryChangesPage = 0;
        if (_catalog is not null) RebuildManagementNavigation();
    }

    private void UpdateManagement()
    {
        if (_instanceDirectory is null || _instance is null) return;
        if (_contentSearch.IsAssigned && _shell.Tree.IsAlive(_contentSearch)
            && _shell.Tree.GetComponent<XsrUiTextInput>(_contentSearch) is { } search
            && search.ReadDraft() != _contentFilter)
        {
            _contentFilter = search.ReadDraft();
            ApplyContentFilter();
            _shell.Tree.GetComponent<XsrUiScroll>(_sections)!.OffsetY = 0;
            UpdateContentWindow();
        }
        if (_managementWrite is { IsCompleted: true } writing)
        {
            _managementWrite = null;
            if (_managementWriteInstance == _instance)
            {
                if (!writing.IsCompletedSuccessfully || !writing.Result.IsSuccess)
                    _feedback.Error(writing.IsCompletedSuccessfully ? writing.Result.Error?.Message ?? "版本更改未完成。" : "版本更改未完成。");
                else { _contentDetail = null; ManagementChanged?.Invoke(); }
                CancelManagementRead();
            }
        }
        if (!_managementLoaded && _managementRead is null && _queries.TryResolve(InstanceManagementContract.Query, out var route))
        {
            _managementStop = new();
            _managementRead = _queries.QueryAsync<InstanceManagementQuery, InstanceManagementSnapshot>(route,
                new(_instance) { IncludeRecoveryStorage = _selected == "recovery", IncludeTrash = _selected == "trash" }, cancellationToken: _managementStop.Token).AsTask();
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
        BuildSections(); UpdateEditors();
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
        if (_managementWrite is not null || _management is null || entry.Enabled is not { } enabled || entry.Size is not { } size
            || !_commands.TryResolve(InstanceManagementContract.SetModEnabled, out var route)) return;
        _managementWriteInstance = _management.InstanceDirectory;
        _managementWrite = _commands.Dispatch(route, new InstanceModEnabledCommand(_managementWriteInstance, entry.Name, !enabled, size, entry.ModifiedUtcTicks)).Completion;
        WakeOnPlatformCompletion(_managementWrite);
    }

    private void BuildManagementSection()
    {
        _shell.Tree.GetComponent<XsrUiStackPanel>(_sections)!.Spacing = 12;
        if (_contentDetail is { } detail) { BuildContentDetail(detail); return; }
        _shell.Tree.GetComponent<XsrUiScroll>(_sections)!.OffsetY = _scrollPositions.GetValueOrDefault(_selected);
        var toolbar = Stack(_sections, "ManagementToolbar", XsrUiOrientation.Horizontal, 10);
        if (_management is not { } snapshot)
        {
            Text(_sections, _managementError ?? "正在读取版本内容…", 13, Muted, 28);
            ManagementButton(toolbar, "刷新", () => { CancelManagementRead(); _managementError = null; }, 60);
            return;
        }
        var page = snapshot.Pages.First(item => item.Id == _selected);
        if (page.Directory is null) ManagementButton(toolbar, "刷新", () => { CancelManagementRead(); _managementError = null; }, 60);
        if (page.Directory is { } directory)
        {
            _contentSearch = Element(toolbar, "ManagementContentSearch", XsrUiSemanticRole.TextInput, "搜索内容", height: 36);
            _shell.Tree.GetComponent<XsrUiElement>(_contentSearch)!.Weight = 1;
            _shell.Tree.GetComponent<XsrUiElement>(_contentSearch)!.Padding = new(12, 0, 12, 0);
            _shell.Tree.SetComponent(_contentSearch, new XsrUiTextInput { Placeholder = "搜索名称、文件名或版本" });
            _shell.Tree.SetComponent(_contentSearch, new XsrUiInput { Focusable = true, Clickable = true });
            Style(_contentSearch, new(241, 244, 248), Ink, 10, 13);
            _shell.Renderer.SetTextInputValue(_contentSearch, _contentFilter);
            ManagementButton(toolbar, "刷新", () => { CancelManagementRead(); _managementError = null; }, 60);
            ManagementButton(toolbar, "打开文件夹", () => OpenContentDirectory(directory), 100);
            _contentSnapshot = snapshot.Contents.FirstOrDefault(item => item.PageId == _selected);
            var location = Stack(_sections, "ManagementLocation", XsrUiOrientation.Horizontal, 12);
            var path = Text(location, directory, 12, Muted, 24);
            _shell.Tree.GetComponent<XsrUiElement>(path)!.Weight = 1;
            Text(location, $"{_contentSnapshot?.Entries.Count ?? 0} 项" + (_contentSnapshot?.Complete == false ? " · 未全部列出" : ""), 12, Muted, 24);
            if (_managementError is not null) Text(_sections, _managementError, 13, Muted, 28);
            if (_contentSnapshot?.Error is { } error) Text(_sections, error, 13, Muted, 28);
            _contentList = Stack(_sections, "ManagementContentList", XsrUiOrientation.Vertical, 0);
            ApplyContentFilter();
            UpdateContentWindow();
        }
        else if (_selected == "overview")
        {
            ManagementFact("版本", System.IO.Path.GetFileName(snapshot.InstanceDirectory));
            ManagementFact("Minecraft", snapshot.GameVersion);
            ManagementFact("游戏目录", snapshot.GameDirectory);
            if (snapshot.Description.Length > 0) ManagementFact("描述", snapshot.Description);
            if (OpenManagementDirectory is not null) ManagementButton(_sections, "打开版本文件夹", () => OpenContentDirectory(snapshot.InstanceDirectory), 128);
        }
        else if (_selected == "recovery") BuildRecoveryStorage(snapshot);
        else if (_selected == "trash") BuildContentTrash(snapshot);
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
        bool gallery = _selected == "screenshots";
        int columns = gallery ? Math.Max(1, (int)((_shell.Renderer.Viewport.Width - 130) / 250)) : 1;
        double rowHeight = gallery ? 210 : _selected == "resourcepacks" ? 104 : 84;
        int rows = (snapshot.Entries.Count + columns - 1) / columns;
        int start = Math.Clamp((int)((_shell.Tree.GetComponent<XsrUiScroll>(_sections)!.OffsetY - 92) / rowHeight) - 3, 0, Math.Max(0, rows - 1));
        int count = Math.Min(rows - start, (int)Math.Ceiling(_shell.Renderer.Viewport.Height / rowHeight) + 8);
        if (_contentWindowStart == start && _contentWindowCount == count && _contentColumns == columns) return;
        _contentWindowStart = start; _contentWindowCount = count; _contentColumns = columns;
        foreach (var action in _contentActions) _managementActions.Remove(action);
        _contentActions.Clear();
        foreach (var child in _shell.Tree.Children(_contentList).ToArray()) _shell.Tree.Destroy(child);
        if (rows == 0) Text(_contentList, _contentFilter.Length == 0 ? "此目录中还没有内容。" : "没有匹配的内容。", 13, Muted, 48);
        Element(_contentList, "ManagementContentBefore", XsrUiSemanticRole.None, null, height: start * rowHeight);
        for (int rowIndex = start; rowIndex < start + count; rowIndex++)
        {
            var row = Stack(_contentList, "ManagementContentRow", XsrUiOrientation.Horizontal, gallery ? 12 : 0);
            var layout = _shell.Tree.GetComponent<XsrUiElement>(row)!;
            layout.Height = rowHeight - (gallery ? 12 : 8);
            layout.Margin = new(0, 0, 0, gallery ? 12 : 8);
            if (!gallery) Style(row, White, Ink, 12);
            foreach (var item in snapshot.Entries.Skip(rowIndex * columns).Take(columns))
            {
                if (gallery) BuildScreenshotCard(row, item);
                else
                {
                    var body = Stack(row, "ManagementContentBody", XsrUiOrientation.Horizontal, 14);
                    var bodyLayout = _shell.Tree.GetComponent<XsrUiElement>(body)!;
                    bodyLayout.Weight = 1;
                    bodyLayout.VerticalAlignment = XsrUiAlignment.Stretch;
                    bodyLayout.Padding = new(16, 10, 16, 10);
                    ContentImage(body, item, 48, 48);
                    var text = Stack(body, "ManagementContentIdentity", XsrUiOrientation.Vertical, 2);
                    _shell.Tree.GetComponent<XsrUiElement>(text)!.Weight = 1;
                    if (_selected == "resourcepacks")
                    {
                        Text(text, ResourcePackTitle(item), 15, Ink, 26, 550);
                        ContentName(text, item.Description, 12, 40, maxLines: 2, foreground: Muted);
                    }
                    else
                    {
                        ContentName(text, item.DisplayName.Length == 0 ? item.Name : item.DisplayName, 15, 26);
                        Text(text, item.Name + (item.Enabled == false ? " · 已停用" : ""), 12, Muted, 22);
                    }
                    if (_selected == "mods")
                    {
                        var version = Text(body, item.Version.Length > 0 ? item.Version : "版本未标注", 12, Muted, 24);
                        _shell.Tree.GetComponent<XsrUiElement>(version)!.Width = 116;
                        _shell.Tree.GetComponent<XsrUiVisualStyle>(version)!.TextAlignment = XsrUiTextAlignment.End;
                    }
                    var details = ActionButton(body, "ManagementContentDetails." + item.Name, "详情", ManagementAction, 64);
                    RegisterContentAction(details, () => OpenContentDetail(item));
                }
            }
            if (gallery)
                for (int missing = columns - Math.Min(columns, snapshot.Entries.Count - rowIndex * columns); missing > 0; missing--)
                    _shell.Tree.GetComponent<XsrUiElement>(Element(row, "GallerySpace", XsrUiSemanticRole.None, null))!.Weight = 1;
        }
        Element(_contentList, "ManagementContentAfter", XsrUiSemanticRole.None, null, height: (rows - start - count) * rowHeight);
        _shell.Tree.MarkDirty(_contentList, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    private void ApplyContentFilter()
    {
        if (_management?.Contents.FirstOrDefault(item => item.PageId == _selected) is not { } source) return;
        _contentSnapshot = source with { Entries = source.Entries.Where(item => (item.Name.Contains(_contentFilter, StringComparison.OrdinalIgnoreCase) || (StripContentFormatting(item.DisplayName).Contains(_contentFilter, StringComparison.OrdinalIgnoreCase) || StripContentFormatting(item.Description).Contains(_contentFilter, StringComparison.OrdinalIgnoreCase)) || item.Version.Contains(_contentFilter, StringComparison.OrdinalIgnoreCase))).ToArray() };
        _contentWindowStart = -1;
    }

    private void RemoveContent(InstanceContentEntry item)
    {
        if (_managementWrite is not null || _management is not { } snapshot
            || !_commands.TryResolve(InstanceManagementContract.RemoveContent, out var route)) return;
        string page = _selected;
        _feedback.ShowDialog("content.remove", "移除内容", $"将“{item.Name}”移至已移除内容，可随时还原。", "移除", "取消", accepted =>
        {
            if (!accepted || _managementWrite is not null || _instance != snapshot.InstanceDirectory) return;
            _managementWriteInstance = snapshot.InstanceDirectory;
            _managementWrite = _commands.Dispatch(route, new InstanceContentRemoveCommand(snapshot.InstanceDirectory, page, item.Name, item.IsDirectory, item.Size, item.ModifiedUtcTicks)).Completion;
            WakeOnPlatformCompletion(_managementWrite);
        });
    }

    private void BuildContentTrash(InstanceManagementSnapshot snapshot)
    {
        Text(_sections, "移除的内容仍保存在游戏目录中。还原不会覆盖同名文件。", 13, Muted, 30);
        int pages = Math.Max(1, (snapshot.Trash.Count + 9) / 10);
        _trashPage = Math.Clamp(_trashPage, 0, pages - 1);
        if (snapshot.Trash.Count == 0) Text(_sections, "没有已移除的内容。", 13, Muted, 28);
        foreach (var item in snapshot.Trash.Skip(_trashPage * 10).Take(10))
        {
            var row = Stack(_sections, "ContentTrashRow", XsrUiOrientation.Horizontal, 12);
            var name = Text(row, item.Name, 14, Ink, 38);
            _shell.Tree.GetComponent<XsrUiElement>(name)!.Weight = 1;
            ManagementButton(row, "还原", () =>
            {
                if (_managementWrite is not null || _instance != snapshot.InstanceDirectory || !_commands.TryResolve(InstanceManagementContract.RestoreContent, out var route)) return;
                _managementWriteInstance = snapshot.InstanceDirectory;
                _managementWrite = _commands.Dispatch(route, new InstanceContentRestoreCommand(snapshot.InstanceDirectory, item.Id)).Completion;
                WakeOnPlatformCompletion(_managementWrite);
            }, 64);
        }
        if (pages > 1) ManagementButton(_sections, $"{_trashPage + 1}/{pages} · 下一页", () =>
        { _trashPage = (_trashPage + 1) % pages; BuildSections(true); }, 140);
    }
}
