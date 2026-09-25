using System.Reflection;
using Nexa.Pxml;
using Nexa.Services.Minecraft;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

internal interface IVersionDirectoryEffects
{
    Task<string?> PickDirectoryAsync();
    void ArmDirectoryDoubleClick(Action openDirectory) { }
    void CancelDirectoryDoubleClick() { }
    Task OpenDirectoryAsync(string directory) => Task.CompletedTask;
}
internal sealed class NativeVersionDirectoryEffects(AvaloniaUiPlatformActions actions) : IVersionDirectoryEffects
{
    public Task<string?> PickDirectoryAsync() => actions.PickDirectoryAsync();
    public void ArmDirectoryDoubleClick(Action openDirectory) => actions.ArmPostNavigationDoubleClick(openDirectory);
    public void CancelDirectoryDoubleClick() => actions.CancelPostNavigationDoubleClick();
    public Task OpenDirectoryAsync(string directory) { actions.OpenDirectory(directory); return Task.CompletedTask; }
}

internal static class VersionSelectionState
{
    public static XsrSemanticId Key(string name) => XsrSemanticId.Parse("versions." + name);
    public static void DeclareState(XsrStateStoreBuilder builder)
    {
        foreach (string name in new[] { "directory.name", "directory.path", "list.count", "list.status" })
            builder.Cell<string>(Key(name), "Nexa.Desktop.VersionSelection");
        foreach (string name in new[] { "list.visible", "directory.named", "rename.visible", "add.visible", "list.empty" })
            builder.Cell<bool>(Key(name), "Nexa.Desktop.VersionSelection");
    }
}

/// <summary>PXML projection and intent adapter. Directory and version truth lives in Services.</summary>
internal sealed class VersionSelectionController : IDisposable
{
    private static readonly XsrUiColor Ink = new(38, 49, 65);
    private static readonly XsrUiColor Secondary = new(94, 110, 130);
    private static readonly XsrUiColor Blue = new(11, 91, 203);
    private static readonly XsrUiColor Surface = new(255, 255, 255, 245);
    private static readonly XsrUiColor Tint = new(231, 240, 255);
    private readonly XsrUiShell _shell;
    private readonly DesktopUiIntentSink _intents;
    private readonly XsrCommandRouter _commands;
    private readonly XsrStateStore _store;
    private readonly DesktopFeedbackService _feedback;
    private readonly IVersionDirectoryEffects? _effects;
    private readonly Dictionary<string, XsrUiEntityId> _entities = [];
    private readonly Dictionary<XsrUiEntityId, (string Root, string Id)> _versions = [];
    private readonly Dictionary<XsrUiEntityId, string> _directories = [];
    private readonly Dictionary<XsrUiEntityId, string> _forget = [];
    private readonly Dictionary<XsrUiEntityId, string> _rename = [];
    private readonly PxmlHostIr _row = Load("VersionSelectionRow.pxml");
    private readonly PxmlHostIr _directoryRow = Load("VersionDirectoryRow.pxml");
    private readonly XsrUiEntityId _dropdown;
    private readonly CancellationTokenSource _lifetime = new();
    private long _revision = -1;
    private string _filter = "", _root = "";
    private bool _chooseDirectories, _disposed, _wasVisible, _picking, _manualAdd, _keyboardDropdown;
    private string? _editingRoot;
    private int _pendingCloseChooser;
    private long _pendingEpoch;
    private long _viewEpoch;
    private string? _pendingVersionPage;
    private readonly Dictionary<XsrUiEntityId, (string Root, string Id)> _actions = [];
    private Task _pending = Task.CompletedTask;

    public VersionSelectionController(XsrUiShell shell, DesktopUiIntentSink intents, XsrCommandRouter commands,
        XsrStateStore store, DesktopFeedbackService feedback, IVersionDirectoryEffects? effects = null)
    {
        _shell = shell; _intents = intents; _commands = commands; _store = store; _feedback = feedback; _effects = effects;
        XsrUiEntityId parent = shell.Tree.Create("version-selection-loader");
        Page = PxmlUiLoader.Load(Load("VersionSelectionPage.pxml"), shell.Tree, store, parent);
        shell.Tree.Detach(Page); shell.Tree.Destroy(parent);
        shell.Tree.Walk(Page, entity => { _entities[shell.Tree.Name(entity)] = entity; Style(entity); return true; });
        _dropdown = PxmlUiLoader.Load(Load("VersionDirectoryDropdown.pxml"), shell.Tree, store, Page);
        shell.Tree.Walk(_dropdown, entity => { _entities[shell.Tree.Name(entity)] = entity; Style(entity); return true; });
        shell.Tree.Detach(_dropdown);
        shell.Tree.SetComponent(_entities["LibraryDropdownCard"], new XsrUiAnchoredOverlay(_entities["LibraryChooseDirectory"]));
        shell.Tree.SetComponent(_dropdown, new XsrUiOverlayLayer(isModal: true));
        shell.Tree.SetComponent(_dropdown, new XsrUiDismissBinding(XsrSemanticId.Parse("ui.versions.dismiss")));
        shell.Tree.GetComponent<XsrUiInput>(_entities["LibraryDropdownDismiss"])!.Focusable = false;
        shell.Tree.GetComponent<XsrUiScroll>(_entities["LibraryVersionRows"])!.ShowsVerticalIndicator = true;
        shell.Tree.GetComponent<XsrUiScroll>(_entities["LibraryDirectoryRows"])!.ShowsVerticalIndicator = true;
        shell.Tree.SetComponent(_entities["LibraryVersionRows"], new XsrUiStableContent());
        Publish("list.visible", true);
        shell.Renderer.FramePreparing += OnFrame;
        intents.IntentEmitted += OnIntent;
    }

    public XsrUiEntityId Page { get; }
    public Task WaitUntilIdle() => _pending;
    private MinecraftLibrarySnapshot Snapshot => (MinecraftLibrarySnapshot)_store.ReadAppliedValue(_store.Resolve(MinecraftLibraryService.StateKey))!;

    private void OnIntent(object? sender, DesktopUiIntentEventArgs e)
    {
        if (_disposed || _shell.Stage.Navigation.Current != Page) return;
        string command = e.Intent.Command.Value;

        if (command is "ui.versions.modify" or "ui.versions.settings" or "ui.versions.delete"
            && _actions.TryGetValue(e.Intent.Source, out var target))
        {
            if (command == "ui.versions.delete")
                _feedback.ShowDialog("version.delete." + target.Id, "删除 " + target.Id + "？",
                    "版本将移入游戏目录的 .recycle 文件夹，可手动恢复。共享资源不会移除。", "删除", "取消",
                    confirmed => { if (confirmed) _pending = Dispatch(MinecraftLibraryRoutes.Delete, new MinecraftLibraryDeleteCommand(target.Root, target.Id)); });
            else
            {
                _pendingVersionPage = command == "ui.versions.modify" ? "ui.launch.modify" : "ui.launch.settings";
                _pending = Dispatch(MinecraftLibraryRoutes.Select, new MinecraftLibrarySelectCommand(target.Root, target.Id), returnHome: true);
            }
            return;
        }
        if (command == "ui.page.back" || command.StartsWith("ui.navigation.", StringComparison.Ordinal))
        { Interlocked.Increment(ref _viewEpoch); CloseDropdown(false); return; }
        if (command == "ui.versions.directories")
        {
            if (_chooseDirectories) CloseDropdown();
            else OpenDropdown(IsKeyboard(e.Intent.Source));
        }
        else if (command == "ui.versions.dismiss") CloseDropdown();
        else if (command == "ui.versions.browse" && !_picking) _pending = BrowseAsync();
        else if (command == "ui.versions.add-path")
        {
            string path = _shell.Tree.GetComponent<XsrUiTextInput>(_entities["LibraryDirectoryInput"])!.ReadDraft().Trim();
            _pending = Dispatch(MinecraftLibraryRoutes.Directory, new MinecraftLibraryDirectoryCommand(path, true), closeChooser: true);
        }
        else if (command == "ui.versions.refresh") _pending = Dispatch(MinecraftLibraryRoutes.Refresh, new MinecraftLibraryRefreshCommand());
        else if (command == "ui.versions.select" && _versions.TryGetValue(e.Intent.Source, out var version))
        {
            _pendingVersionPage = null;
            _effects?.CancelDirectoryDoubleClick();

            string? directory = Snapshot.Instances.FirstOrDefault(item => item.Id == version.Id)?.DirectoryPath;
            if (!IsKeyboard(e.Intent.Source) && directory is not null && _effects is not null)
                _effects.ArmDirectoryDoubleClick(() =>
                {
                    if (!_disposed) _pending = OpenVersionDirectoryAsync(directory);
                });
            _pending = Dispatch(MinecraftLibraryRoutes.Select,
                new MinecraftLibrarySelectCommand(version.Root, version.Id), returnHome: true);
        }
        else if (command == "ui.versions.directory" && _directories.TryGetValue(e.Intent.Source, out string? root))
            _pending = Dispatch(MinecraftLibraryRoutes.Directory, new MinecraftLibraryDirectoryCommand(root), closeChooser: true);
        else if (command == "ui.versions.forget" && _forget.TryGetValue(e.Intent.Source, out string? forgotten))
            _pending = Dispatch(MinecraftLibraryRoutes.Forget, new MinecraftLibraryForgetCommand(forgotten));
        else if (command == "ui.versions.rename" && _rename.TryGetValue(e.Intent.Source, out string? edited))
        {
            MinecraftLibraryDirectory directory = Snapshot.Directories.First(item => item.Path == edited);
            if (directory.IsOfficial) return;
            _editingRoot = edited;
            Publish("rename.visible", true);
            _shell.Renderer.SetTextInputValue(_entities["LibraryNameInput"], directory.Name);
            _shell.Renderer.Focus(_entities["LibraryNameInput"], IsKeyboard(e.Intent.Source));
        }
        else if (command == "ui.versions.rename-save" && _editingRoot is { } renamed)
            _pending = Dispatch(MinecraftLibraryRoutes.Rename, new MinecraftLibraryRenameCommand(renamed,
                _shell.Tree.GetComponent<XsrUiTextInput>(_entities["LibraryNameInput"])!.ReadDraft()), closeChooser: true);
        else if (command == "ui.versions.rename-cancel")
        { _editingRoot = null; Publish("rename.visible", false); FocusDirectory(); }
    }

    private async Task OpenVersionDirectoryAsync(string directory)
    {
        try { await _effects!.OpenDirectoryAsync(directory).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { if (!_disposed) _feedback.Error("无法打开版本目录：" + error.Message); }
    }

    private async Task BrowseAsync()
    {
        if (_effects is null) { OpenDropdown(false, manualAdd: true); return; }
        _picking = true;
        long epoch = _viewEpoch;
        try
        {
            string? path = await _effects.PickDirectoryAsync().ConfigureAwait(false);
            if (!_disposed && epoch == Interlocked.Read(ref _viewEpoch) && path is not null)
                await Dispatch(MinecraftLibraryRoutes.Directory, new MinecraftLibraryDirectoryCommand(path, true), closeChooser: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        { if (!_disposed) _feedback.Error("无法打开目录选择器，请使用完整路径添加目录。"); }
        finally { _picking = false; }
    }

    private async Task Dispatch<T>(XsrSemanticId route, T command, bool closeChooser = false, bool returnHome = false) where T : notnull
    {
        long epoch = Interlocked.Read(ref _viewEpoch);
        if (!_commands.TryResolve(route, out XsrCommandId id)) { _feedback.Error("版本目录服务尚未就绪。"); return; }
        XsrResult result = await _commands.Dispatch(id, command, cancellationToken: _lifetime.Token).Completion.ConfigureAwait(false);
        if (_disposed) return;
        if (!result.IsSuccess)
        {
            if (route == MinecraftLibraryRoutes.Select) _effects?.CancelDirectoryDoubleClick();
            _pendingVersionPage = null;
            if (result.Error?.Code != XsrRuntimeErrors.Cancelled().Code)
                _feedback.Error(route == MinecraftLibraryRoutes.Delete ? result.Error?.Message ?? "无法删除此版本。"
                    : result.Error?.Code == MinecraftErrors.InvalidRequestCode
                    ? "无法读取此游戏目录，请检查完整路径和访问权限。" : "未能保存版本选择，请检查设置目录是否可写。");
        }
        if (result.IsSuccess && (closeChooser || returnHome) && epoch == Interlocked.Read(ref _viewEpoch))
        {
            Interlocked.Exchange(ref _pendingEpoch, epoch);
            Interlocked.Exchange(ref _pendingCloseChooser, returnHome ? 2 : 1);
            // Wake the frame even when discovery completed before the close flag was queued.
            _store.Publish(_store.Resolve(VersionSelectionState.Key("list.visible")), true);
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        bool visible = _shell.Stage.Navigation.Current == Page;
        if (_wasVisible && !visible) { Interlocked.Increment(ref _viewEpoch); }
        _wasVisible = visible;
        if (!visible) { CloseDropdown(false); Interlocked.Exchange(ref _pendingCloseChooser, 0); return; }
        int pending = Interlocked.Exchange(ref _pendingCloseChooser, 0);
        if (pending != 0 && Interlocked.Read(ref _pendingEpoch) == Interlocked.Read(ref _viewEpoch))
        {
            CloseDropdown();
            if (pending == 2)
            {
                _intents.Emit(XsrSemanticId.Parse("ui.page.back"), Page, XsrCorrelationId.Create());
                if (_pendingVersionPage is { } destination)
                { _pendingVersionPage = null; _intents.Emit(XsrSemanticId.Parse(destination), Page, XsrCorrelationId.Create()); }
                return;
            }
        }
        MinecraftLibrarySnapshot snapshot = Snapshot;
        if (_chooseDirectories)
        {
            // Rows are 48 px + 4 spacing each, plus the card's 8+8 padding: the card must be
            // tall enough that the list does NOT overflow — an under-sized card makes
            // CanScrollVertically true and paints a full-height track for a two-row list.
            double height = Math.Min(
                snapshot.Directories.Count * 48 + (4 * Math.Max(0, snapshot.Directories.Count - 1)) + 8
                + (_editingRoot is null ? 0 : 44) + (_manualAdd ? 44 : 0),
                Math.Max(96, _shell.Renderer.Viewport.Height - 160));
            XsrUiElement card = _shell.Tree.GetComponent<XsrUiElement>(_entities["LibraryDropdownCard"])!;
            if (card.Height != height) { card.Height = height; _shell.Tree.MarkDirty(_dropdown, XsrUiDirtyKinds.Layout); }
        }
        if (_root != snapshot.RootDirectory)
        {
            _root = snapshot.RootDirectory;
            _shell.Renderer.SetTextInputValue(_entities["LibrarySearch"], "");
            _shell.Tree.GetComponent<XsrUiScroll>(_entities["LibraryVersionRows"])!.OffsetY = 0;
        }
        string filter = _shell.Tree.GetComponent<XsrUiTextInput>(_entities["LibrarySearch"])!.ReadDraft().Trim();
        if (_revision == snapshot.Revision && filter == _filter) return;
        _revision = snapshot.Revision; _filter = filter;
        MinecraftLibraryDirectory current = snapshot.Directories.First(item => MinecraftLibraryService.PathComparer.Equals(item.Path, snapshot.RootDirectory));
        Publish("directory.name", current.DisplayName); Publish("directory.path", current.Path); Publish("directory.named", current.HasName);
        IReadOnlyList<MinecraftInstanceDescriptor> shown = [.. snapshot.Instances.Where(instance =>
            instance.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) || instance.VersionId.Contains(filter, StringComparison.OrdinalIgnoreCase))];
        Publish("list.count", snapshot.IsLoading ? "正在刷新…" : $"{shown.Count} 个版本");
        Publish("list.empty", shown.Count == 0);
        Publish("list.status", snapshot.IsLoading ? "正在读取目录中的版本…" : snapshot.Error is not null
            ? "无法读取此目录" : snapshot.Instances.Count == 0
            ? "暂无已安装版本" : "无匹配版本");
        ReconcileRows(shown, snapshot);
    }

    private void OpenDropdown(bool keyboard, bool manualAdd = false)
    {
        Interlocked.Increment(ref _viewEpoch);
        _keyboardDropdown = keyboard;
        _manualAdd = manualAdd;
        _editingRoot = null;
        Publish("add.visible", manualAdd); Publish("rename.visible", false);
        if (!_chooseDirectories) _shell.Tree.Attach(_dropdown, _entities["LibraryContent"]);
        _chooseDirectories = true; _revision = -1;
        if (manualAdd) _shell.Renderer.Focus(_entities["LibraryDirectoryInput"], keyboard);
        else FocusDirectory();
    }

    private void CloseDropdown(bool restoreFocus = true)
    {
        if (!_chooseDirectories) return;
        _shell.Tree.Detach(_dropdown);
        _chooseDirectories = false; _editingRoot = null; _manualAdd = false;
        Publish("rename.visible", false); Publish("add.visible", false);
        if (restoreFocus) _shell.Renderer.Focus(_entities["LibraryChooseDirectory"], _keyboardDropdown);
    }

    private void FocusDirectory()
    {
        XsrUiEntityId row = _directories.FirstOrDefault(item => item.Value == Snapshot.RootDirectory).Key;
        if (row.IsAssigned) _shell.Renderer.Focus(row, _keyboardDropdown);
    }

    private void ReconcileRows(IReadOnlyList<MinecraftInstanceDescriptor> shown, MinecraftLibrarySnapshot snapshot)
    {
        XsrUiEntityId focused = _shell.Renderer.Focused;
        string? selectedFocus = _versions.TryGetValue(focused, out var focusedVersion) ? focusedVersion.Id : null;
        bool keyboard = IsKeyboard(focused);
        // Reuse stable rows for selection-only revisions; only discovery/filter changes rebuild.
        string[] existing = [.. _versions.Values.Select(item => item.Id)];
        if (!existing.SequenceEqual(shown.Select(item => item.Id)) || _versions.Values.Any(item => item.Root != snapshot.RootDirectory))
        {
            foreach (XsrUiEntityId entity in _versions.Keys) _shell.Tree.Destroy(entity);
            _versions.Clear();
            _actions.Clear();
            foreach (MinecraftInstanceDescriptor instance in shown)
            {
                XsrUiEntityId row = CreateRow(_entities["LibraryVersionRows"], "version:" + instance.Id, instance.Id,
                    VersionKindLabel(instance.Version.Kind) + (instance.Version.InheritsFrom is { Length: > 0 } parent ? " · " + parent : " · " + instance.VersionId),
                    false, VersionIcon(instance.Version.Kind));
                _versions.Add(row, (snapshot.RootDirectory, instance.Id));
                _shell.Tree.Walk(row, entity =>
                {
                    string key = _shell.Tree.Name(entity).Split(':')[0];
                    if (key is "LibraryRowModify" or "LibraryRowSettings" or "LibraryRowDelete") _actions[entity] = (snapshot.RootDirectory, instance.Id);
                    return true;
                });
                if (instance.Id == selectedFocus) _shell.Renderer.Focus(row, keyboard);
            }
        }
        foreach (var (row, value) in _versions)
        {
            MinecraftInstanceDescriptor instance = shown.First(item => item.Id == value.Id);
            _shell.Tree.Walk(row, entity =>
            {
                string key = _shell.Tree.Name(entity);
                if (key.StartsWith("LibraryRowIcon:", StringComparison.Ordinal))
                    _shell.Tree.GetComponent<XsrUiImage>(entity)!.Source = VersionIcon(instance.Version.Kind);
                if (key.StartsWith("LibraryRowDetail:", StringComparison.Ordinal))
                    _shell.Tree.GetComponent<XsrUiText>(entity)!.Content = VersionKindLabel(instance.Version.Kind) + " · " +
                        (instance.Version.InheritsFrom is { Length: > 0 } parent ? parent : instance.VersionId);
                return true;
            });
            MarkSelected(row, value.Id == snapshot.SelectedInstanceId, "当前版本", value.Id);
        }
        // Directory rows stay attached while their selected marker changes.
        if (!_directories.Values.SequenceEqual(snapshot.Directories.Select(item => item.Path)))
        {
            foreach (XsrUiEntityId entity in _directories.Keys) _shell.Tree.Destroy(entity);
            _directories.Clear(); _forget.Clear(); _rename.Clear();
            foreach (MinecraftLibraryDirectory directory in snapshot.Directories)
            {
                PxmlIrNode Project(PxmlIrNode node) => node with
                { Key = node.Key + ":directory:" + directory.Path, Children = [.. node.Children.Select(Project)] };
                XsrUiEntityId row = PxmlUiLoader.Load(new(Project(_directoryRow.Root)), _shell.Tree, _store, _entities["LibraryDirectoryRows"]);
                _shell.Tree.SetComponent(row, new XsrUiSelection());
                _directories[row] = directory.Path;
                _shell.Tree.Walk(row, entity =>
                {
                    Style(entity);
                    string key = _shell.Tree.Name(entity);
                    if (key.StartsWith("LibraryDirectoryForget:", StringComparison.Ordinal)) _forget[entity] = directory.Path;
                    if (key.StartsWith("LibraryDirectoryRename:", StringComparison.Ordinal)) _rename[entity] = directory.Path;
                    return true;
                });
            }
        }
        foreach (var (row, root) in _directories)
        {
            MinecraftLibraryDirectory directory = snapshot.Directories.First(item => item.Path == root);
            MarkSelected(row, MinecraftLibraryService.PathComparer.Equals(root, snapshot.RootDirectory), "当前目录", directory.DisplayName);
            _shell.Tree.Walk(row, entity =>
            {
                string key = _shell.Tree.Name(entity).Split(':')[0];
                if (key == "LibraryDirectoryRowName") _shell.Tree.GetComponent<XsrUiText>(entity)!.Content = directory.DisplayName;
                if (key == "LibraryDirectoryRowPath")
                { _shell.Tree.GetComponent<XsrUiText>(entity)!.Content = root; _shell.Tree.GetComponent<XsrUiElement>(entity)!.IsVisible = directory.HasName; }
                if (key == "LibraryDirectoryRename") _shell.Tree.GetComponent<XsrUiElement>(entity)!.IsVisible = !directory.IsOfficial;
                if (key == "LibraryDirectoryForget") _shell.Tree.GetComponent<XsrUiElement>(entity)!.IsVisible = snapshot.Directories.Count > 1;
                return true;
            });
        }
        if (_chooseDirectories && !_shell.Tree.IsAlive(focused)) FocusDirectory();
    }

    private XsrUiEntityId CreateRow(XsrUiEntityId parent, string key, string name, string detail, bool directory, string? icon = null)
    {
        PxmlIrNode Project(PxmlIrNode node) => node with
        {
            Key = node.Key + ":" + key,
            Content = node.Key switch { "LibraryRowName" => name, "LibraryRowDetail" => detail, _ => node.Content },
            ImageSource = node.Key == "LibraryRowIcon" ? directory ? "pcl/folder" : icon : node.ImageSource,
            Command = node.Key == "LibraryRow" && directory ? XsrSemanticId.Parse("ui.versions.directory") : node.Command,
            Children = [.. node.Children.Select(Project)],
        };
        XsrUiEntityId row = PxmlUiLoader.Load(new(Project(_row.Root)), _shell.Tree, _store, parent);
        _shell.Tree.SetComponent(row, new XsrUiSelection());
        _shell.Tree.Walk(row, entity => { Style(entity); return true; });
        return row;
    }

    private void MarkSelected(XsrUiEntityId row, bool selected, string marker, string name)
    {
        _shell.Tree.GetComponent<XsrUiSelection>(row)!.IsSelected = selected;
        XsrUiVisualStyle style = _shell.Tree.GetComponent<XsrUiVisualStyle>(row)!;
        style.Background = selected ? Tint : Surface; style.Border = selected ? new(149, 186, 239) : new(227, 233, 242); style.BorderWidth = 1;
        _shell.Tree.GetComponent<XsrUiSemantic>(row)!.Label = selected ? $"{name}，{marker}" : $"选择 {name}";
        _shell.Tree.Walk(row, entity =>
        {
            string key = _shell.Tree.Name(entity);
            if (key.StartsWith("LibraryRowCheck:", StringComparison.Ordinal) || key.StartsWith("LibraryRowSelected:", StringComparison.Ordinal)
                || key.StartsWith("LibraryDirectoryRowCheck:", StringComparison.Ordinal))
            {
                _shell.Tree.GetComponent<XsrUiElement>(entity)!.IsVisible = selected;
                if (_shell.Tree.GetComponent<XsrUiText>(entity) is { } text) text.Content = marker;
            }
            return true;
        });
        _shell.Tree.MarkDirty(row, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    private void Style(XsrUiEntityId entity)
    {
        string key = _shell.Tree.Name(entity).Split(':')[0];
        XsrUiVisualStyle style = new() { Foreground = Ink, FontSize = 14, FontWeight = 400, CornerRadius = 12 };
        if (key is "LibraryChooseDirectory" or "LibraryRow" or "LibraryDropdownCard") { style.Background = new(255, 255, 255); style.Border = new(227, 233, 242); style.BorderWidth = 1; }
        if (key is "LibraryListTitle" or "LibraryDirectoriesTitle" or "LibraryRowName") { style.FontSize = 16; style.FontWeight = 600; }
        if (key is "LibraryRowDetail" or "LibraryCount" or "LibraryEmpty" or "LibraryDirectoryPath" or "LibraryDirectoryRowPath") { style.Foreground = Secondary; style.FontSize = 12; }
        if (key is "LibraryDirectoryPath" or "LibraryDirectoryRowPath") style.TextAlignment = XsrUiTextAlignment.End;
        if (key == "LibraryEmpty") style.WrapText = true;
        if (key is "LibraryRowSelected" or "LibraryRowCheck" or "LibraryFolderIcon" or "LibraryRowIcon") { style.Foreground = Blue; style.FontSize = 12; style.FontWeight = 600; }
        if (_shell.Tree.GetComponent<XsrUiInput>(entity) is not null)
        { style.Hover = new(237, 243, 253); if (key is not "LibraryRow" and not "LibraryChooseDirectory" and not "LibraryDirectoryRow") { style.Background = new(240, 244, 250); style.FontSize = 13; } }
        if (key is "LibraryRowModify" or "LibraryRowSettings" or "LibraryRowDelete")
        { style.HoverExpand = true; style.CornerRadius = 16; style.Background = DesktopUiPalette.CapsuleBackground; style.Foreground = DesktopUiPalette.CapsuleForeground; style.TextAlignment = XsrUiTextAlignment.Center; }
        if (key == "LibraryAddPath") { style.Background = Blue; style.Foreground = new(255, 255, 255); style.Hover = new(23, 110, 225); }
        if (key == "LibraryDropdownDismiss") { style.Background = XsrUiColor.Transparent; style.Hover = XsrUiColor.Transparent; }
        if (key is "LibraryAddDirectory" or "LibraryRefresh") { style.HoverExpand = true; style.CornerRadius = key == "LibraryAddDirectory" ? 20 : 18; style.TextAlignment = XsrUiTextAlignment.Center; }
        if (key == "LibraryAddDirectory") { style.Background = DesktopUiPalette.CapsuleBackground; style.Foreground = DesktopUiPalette.CapsuleForeground; style.Hover = DesktopUiPalette.CapsuleHover; style.FontWeight = 600; }
        _shell.Tree.SetComponent(entity, style);
        if (_shell.Tree.GetComponent<XsrUiText>(entity) is { } text && key != "LibraryEmpty") { text.MaxLines = 1; text.TrimOverflow = true; }
    }

    internal static string VersionIcon(MinecraftVersionKind kind) => "nexa/version/" + (kind switch
    {
        MinecraftVersionKind.Snapshot => "CommandBlock",
        MinecraftVersionKind.Old => "CobbleStone",
        MinecraftVersionKind.AprilFools => "GoldBlock",
        MinecraftVersionKind.OptiFine => "GrassPath",
        MinecraftVersionKind.LiteLoader => "Egg",
        MinecraftVersionKind.Forge => "Anvil",
        MinecraftVersionKind.NeoForge => "NeoForge",
        MinecraftVersionKind.Cleanroom => "Cleanroom",
        MinecraftVersionKind.Quilt => "Quilt",
        MinecraftVersionKind.Fabric => "Fabric",
        MinecraftVersionKind.LabyMod => "LabyMod",
        _ => "Grass",
    });
    private static string VersionKindLabel(MinecraftVersionKind kind) => kind switch
    {
        MinecraftVersionKind.Release => "正式版",
        MinecraftVersionKind.Snapshot => "快照版",
        MinecraftVersionKind.Old => "远古版",
        MinecraftVersionKind.AprilFools => "愚人节版",
        _ => kind.ToString(),
    };

    private bool IsKeyboard(XsrUiEntityId entity) => _shell.Tree.IsAlive(entity) && _shell.Tree.GetComponent<XsrUiInput>(entity)?.IsFocusVisible == true;
    private void Publish<T>(string name, T value)
    { XsrStateId id = _store.Resolve(VersionSelectionState.Key(name)); if (!Equals(_store.ReadAppliedValue(id), value)) _store.Publish(id, value); }
    private static PxmlHostIr Load(string name)
    {
        Assembly assembly = typeof(VersionSelectionController).Assembly;
        using Stream stream = assembly.GetManifestResourceStream("Nexa.Desktop.Ui." + name)!;
        using StreamReader reader = new(stream);
        return PxmlCompiler.Compile(PxmlParser.Parse(reader.ReadToEnd()));
    }
    public void Dispose()
    {

        CloseDropdown(false);
        _shell.Tree.Destroy(_dropdown);
        _disposed = true; Interlocked.Increment(ref _viewEpoch); _lifetime.Cancel();
        _intents.IntentEmitted -= OnIntent; _shell.Renderer.FramePreparing -= OnFrame; _lifetime.Dispose();
    }
}
