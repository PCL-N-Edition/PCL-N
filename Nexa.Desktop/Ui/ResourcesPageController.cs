using System.Collections.Concurrent;
using Nexa.Pxml;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Resources;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

internal static class ResourcesPresentationState
{
    internal static readonly XsrSemanticId Wake = XsrSemanticId.Parse("ui.resources.wake");
    internal static void DeclareState(XsrStateStoreBuilder builder) => builder.Cell<long>(Wake, "Nexa.Desktop.Resources");
}

/// <summary>Presentation-only catalog. Request replacement cancels and forgets the old query.</summary>
internal sealed class ResourcesPageController : IDisposable
{
    private static readonly XsrSemanticId Action = XsrSemanticId.Parse("ui.resources.action");
    private static readonly XsrUiColor Ink = new(43, 51, 64), Muted = new(113, 124, 140), Blue = new(11, 91, 203), Tint = new(241, 245, 250), White = new(255, 255, 255);
    private readonly XsrUiShell _shell;
    private readonly DesktopUiIntentSink _intents;
    private readonly XsrQueryRouter _queries;
    private readonly XsrStateStore _store;
    private readonly Action<Uri> _open;
    private readonly Dictionary<XsrUiEntityId, Action> _actions = [];
    private readonly ConcurrentQueue<XsrUiEntityId> _pending = new();
    private readonly Dictionary<string, XsrUiEntityId> _entities = [];
    private readonly List<XsrUiEntityId> _listActions = [];
    private readonly List<XsrUiEntityId> _detailActions = [];
    private readonly XsrUiEntityId _search, _game, _loader, _status, _previous, _next, _detailBody;
    private CancellationTokenSource _stop = new();
    private CancellationTokenSource _iconStop = new();
    private Task<XsrResult<ResourceSearchResult>>? _searching;
    private Task<XsrResult<ResourceDetail>>? _reading;
    private readonly List<(XsrUiEntityId Entity, Task<XsrResult<ResourceIconResult>> Read)> _icons = [];
    private Task<XsrResult<MinecraftInstallEditSnapshot>>? _instanceReading;
    private MinecraftInstallEditQuery? _instanceRequest;
    private XsrQueryRouter? _instanceQueries;
    private Func<MinecraftInstallEditQuery?>? _selectedInstance;
    private ResourceSearchQuery _filter = new();
    private ResourceSearchResult? _result;
    private ResourceDetail? _detail;
    private string? _detailId;
    private int _detailPage;
    private bool _started, _disposed, _visible;
    private long _wake;
    private XsrUiThickness _previousPadding;
    internal XsrUiEntityId Page { get; }
    internal XsrUiEntityId DetailPage { get; }

    internal ResourcesPageController(XsrUiShell shell, DesktopUiIntentSink intents, XsrQueryRouter queries,
        XsrStateStore store, Action<Uri> open)
    {
        _shell = shell; _intents = intents; _queries = queries; _store = store; _open = open;
        using var stream = typeof(ResourcesPageController).Assembly.GetManifestResourceStream("Nexa.Desktop.Ui.ResourcesPage.pxml")!;
        using var reader = new StreamReader(stream);
        var host = shell.Tree.Create("resources-loader");
        Page = PxmlUiLoader.Load(PxmlCompiler.Compile(PxmlParser.Parse(reader.ReadToEnd())), shell.Tree, store, host);
        shell.Tree.Detach(Page); shell.Tree.Destroy(host);
        shell.Tree.Walk(Page, entity => { _entities[shell.Tree.Name(entity)] = entity; return true; });
        shell.Tree.SetComponent(_entities["ResourceList"], new XsrUiScrollGesture());
        Segment(_entities["ResourceCategories"], "ResourceCategory", ["模组", "整合包", "资源包", "光影", "数据包"], index =>
        { _filter = _filter with { Kind = (ResourceKind)index }; Search(0); }, 70);
        var header = _entities["ResourceCategories"];
        E(Element(header, "ResourceHeaderSpace")).Weight = 1;
        Segment(header, "ResourceOrder", ["相关", "热门", "更新"], index =>
        { _filter = _filter with { Order = (ResourceOrder)index }; Search(0); }, 54);
        var toolbar = _entities["ResourceToolbar"];
        _search = Input(toolbar, "ResourceSearch", "搜索资源", 0); E(_search).Weight = 1;
        _game = Input(toolbar, "ResourceGame", "游戏版本", 104);
        _loader = Input(toolbar, "ResourceLoader", "加载器", 100);
        Button(toolbar, "ResourceSearchButton", "搜索", 56, () => Search(0));
        Button(toolbar, "ResourceCurrentInstance", "当前版本", 76, UseCurrentInstance);
        Button(toolbar, "ResourceReset", "重置", 48, () =>
        {
            foreach (var input in new[] { _search, _game, _loader }) _shell.Renderer.SetTextInputValue(input, "");
            Search(0);
        });
        var pagination = _entities["ResourcePagination"];
        _status = Text(pagination, "Modrinth", 12, Muted, 28); E(_status).Weight = 1;
        _previous = Button(pagination, "ResourcePrevious", "上一页", 68, () => Search(Math.Max(0, _filter.Page - 1)));
        _next = Button(pagination, "ResourceNext", "下一页", 68, () => Search(_filter.Page + 1));
        E(_previous).Height = 28; E(_next).Height = 28;
        DetailPage = Element(default, "ResourceDetailPage", XsrUiSemanticRole.Page, "资源详情");
        _detailBody = Stack(DetailPage, "ResourceDetailBody"); E(_detailBody).Weight = 1; E(_detailBody).Padding = new(24, 20, 24, 24);
        shell.Tree.SetComponent(_detailBody, new XsrUiScroll { ShowsVerticalIndicator = true });
        shell.Tree.SetComponent(_detailBody, new XsrUiScrollGesture());
        _intents.IntentEmitted += OnIntent; shell.Renderer.FramePreparing += OnFrame;
    }

    private void OnIntent(object? sender, DesktopUiIntentEventArgs args)
    {
        if (args.Intent.Command == Action && (_shell.Stage.Navigation.Current == Page || _shell.Stage.Navigation.Current == DetailPage)) _pending.Enqueue(args.Intent.Source);
    }

    private void OnFrame(object? sender, EventArgs args)
    {
        if (_disposed) return;
        bool visible = _shell.Stage.Navigation.Current == Page || _shell.Stage.Navigation.Current == DetailPage;
        if (visible != _visible)
        {
            var content = E(_shell.Content);
            if (visible) { _previousPadding = content.Padding; content.Padding = default; }
            else
            {
                content.Padding = _previousPadding;
                if (_searching is not null || _icons.Count > 0) _started = false;
                Cancel(); CancelIcons();
            }
            _visible = visible; _shell.Tree.MarkDirty(_shell.Content, XsrUiDirtyKinds.Layout);
        }
        if (!visible) return;
        for (int i = _icons.Count - 1; i >= 0; i--)
        {
            var (entity, read) = _icons[i];
            if (!read.IsCompleted) continue;
            _icons.RemoveAt(i);
            if (read.IsCompletedSuccessfully && read.Result.IsSuccess && read.Result.Value?.Image is { } image)
            {
                _shell.Tree.GetComponent<XsrUiImage>(entity)!.Raster = new(image,
                    [new(new(0, 0, image.Width, image.Height), new(0, 0, 1, 1))])
                { FitToBounds = true };
                _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
            }
        }
        _shell.Tree.GetComponent<XsrUiInput>(_entities["ResourceCurrentInstance"])!.Enabled = _instanceReading is null && _selectedInstance?.Invoke() is not null;
        if (!_started) { _started = true; Search(_filter.Page); }
        while (_pending.TryDequeue(out var source)) if (_actions.TryGetValue(source, out var action)) action();
        if (_instanceReading is { IsCompleted: true } instanceReading)
        {
            _instanceReading = null;
            if (_instanceRequest != _selectedInstance?.Invoke())
                _shell.Tree.SetComponent(_status, new XsrUiText("当前选择已改变，请重新选择筛选。"));
            else if (instanceReading.IsCompletedSuccessfully && instanceReading.Result.IsSuccess)
            {
                var instance = instanceReading.Result.Value!;
                _shell.Renderer.SetTextInputValue(_game, instance.GameVersion);
                string loader = instance.Selection.Where(item => item.Loader is InstallLoader.Fabric or InstallLoader.Quilt or InstallLoader.Forge or InstallLoader.NeoForge).Select(item => item.Loader.ToString().ToLowerInvariant()).FirstOrDefault() ?? "";
                _shell.Renderer.SetTextInputValue(_loader, loader is "fabric" or "quilt" or "forge" or "neoforge" ? loader : "");
                Search(0);
            }
            else _shell.Tree.SetComponent(_status, new XsrUiText("无法读取当前版本，请手动填写筛选条件。"));
        }
        if (_shell.Stage.Navigation.Current == Page && _reading is not null) { Cancel(); _detailId = null; }
        if (_searching is { IsCompleted: true } searching)
        {
            _searching = null;
            if (searching.IsCompletedSuccessfully && searching.Result.IsSuccess)
            { _result = searching.Result.Value!; ShowResults(); }
            else ShowFailure(_entities["ResourceList"], "暂时无法加载资源。请检查网络后重试。", () => Search(_filter.Page), _listActions);
            UpdatePagination();
        }
        if (_reading is { IsCompleted: true } reading)
        {
            _reading = null;
            if (reading.IsCompletedSuccessfully && reading.Result.IsSuccess) { _detail = reading.Result.Value!; ShowDetail(); }
            else ShowFailure(_detailBody, "暂时无法加载详情。请重试。", () => ReadDetail(_detailId!), _detailActions);
        }
    }

    private string Draft(XsrUiEntityId entity) => _shell.Tree.GetComponent<XsrUiTextInput>(entity)!.ReadDraft().Trim();
    private void Search(int page)
    {
        Cancel(); CancelIcons(); _result = null;
        _filter = _filter with { Text = Draft(_search), GameVersion = Draft(_game), Loader = Draft(_loader), Page = page };
        Clear(_entities["ResourceList"], _listActions);
        Text(_entities["ResourceList"], "正在加载资源…", 14, Muted, 54);
        _shell.Tree.GetComponent<XsrUiScroll>(_entities["ResourceList"])!.OffsetY = 0;
        if (_queries.TryResolve(ResourceCatalogContract.Search, out var route))
        {
            _searching = _queries.QueryAsync<ResourceSearchQuery, ResourceSearchResult>(route, _filter, cancellationToken: _stop.Token).AsTask();
            Wake(_searching);
        }
        UpdatePagination();
    }

    private void ShowResults()
    {
        var list = _entities["ResourceList"]; Clear(list, _listActions);
        if (_result!.Projects.Count == 0) { Text(list, "没有找到匹配的资源。试试其他关键词或筛选条件。", 14, Muted, 64); return; }
        foreach (var project in _result.Projects)
        {
            var row = Card(list, "ResourceProject." + project.Id);
            var icon = Element(row, "ResourceProjectIcon"); E(icon).Width = 48; E(icon).Height = 48; E(icon).VerticalAlignment = XsrUiAlignment.Center;
            Style(icon, Tint, Muted, 12);
            _shell.Tree.SetComponent(icon, new XsrUiImage(_filter.Kind switch
            {
                ResourceKind.Mod => "lucide/blocks",
                ResourceKind.Shader => "nexa/content-shader",
                _ => "nexa/content-package"
            }));
            if (project.IconUrl is { } url && _queries.TryResolve(ResourceCatalogContract.Icon, out var iconRoute))
            {
                var read = _queries.QueryAsync<ResourceIconQuery, ResourceIconResult>(iconRoute, new(url), cancellationToken: _iconStop.Token).AsTask();
                _icons.Add((icon, read)); Wake(read);
            }
            var copy = Stack(row, "ResourceProjectCopy"); E(copy).Weight = 1;
            Text(copy, project.Title, 15, Ink, 22, 600);
            Text(copy, project.Description, 12, Muted, 20);
            Text(copy, $"{project.Author}  ·  {project.Downloads:N0} 次下载", 11, Muted, 18);
            _listActions.Add(Button(row, "ResourceDetails." + project.Id, "详情", 68, () =>
            {
                _detailPage = 0; _shell.Stage.Navigation.Push(DetailPage); ReadDetail(project.Id);
            }));
        }
    }

    private void ReadDetail(string id)
    {
        Cancel(); _detailId = id; _detail = null;
        Clear(_detailBody, _detailActions); Text(_detailBody, "正在加载详情…", 14, Muted, 54);
        string loader = _filter.Kind == ResourceKind.DataPack ? "datapack" : _filter.Kind == ResourceKind.ResourcePack ? "" : _filter.Loader;
        if (_queries.TryResolve(ResourceCatalogContract.Detail, out var route))
        {
            _reading = _queries.QueryAsync<ResourceDetailQuery, ResourceDetail>(route, new(id, _filter.GameVersion, loader), cancellationToken: _stop.Token).AsTask();
            Wake(_reading);
        }
    }

    private void ShowDetail()
    {
        Clear(_detailBody, _detailActions);
        var detail = _detail!;
        Text(_detailBody, detail.Project.Title, 26, Ink, 40, 650);
        Text(_detailBody, detail.Project.Description, 14, Muted, 68, lines: 3);
        var tools = Stack(_detailBody, "ResourceDetailTools", horizontal: true);
        var info = Text(tools, $"Modrinth  ·  {detail.Project.Downloads:N0} 次下载  ·  {detail.License}", 12, Muted, 34); E(info).Weight = 1;
        _detailActions.Add(Button(tools, "ResourceWebsite", "项目主页 ↗", 110, () => _open(new Uri(detail.Project.Website))));
        Text(_detailBody, $"版本  ·  {detail.Versions.Count} 个" + (_filter.GameVersion.Length > 0 ? "  ·  Minecraft " + _filter.GameVersion : ""), 16, Ink, 36, 600);
        if (detail.Versions.Count == 0) Text(_detailBody, "没有符合当前筛选条件的版本。", 14, Muted, 48);
        foreach (var version in detail.Versions.Skip(_detailPage * 20).Take(20))
        {
            var row = Card(_detailBody, "ResourceVersion." + version.Id);
            var copy = Stack(row, "ResourceVersionCopy"); E(copy).Weight = 1;
            Text(copy, version.Name, 15, Ink, 28, 550);
            Text(copy, $"{version.Channel}  ·  {string.Join(" / ", version.Loaders)}  ·  Minecraft {string.Join(", ", version.Games)}", 12, Muted, 34, lines: 2);
            _detailActions.Add(Button(row, "ResourceDownload." + version.Id, "下载页面 ↗", 112, () => _open(new Uri(version.Website))));
        }
        if (detail.Versions.Count > 20)
        {
            var pages = Stack(_detailBody, "ResourceVersionPages", horizontal: true);
            var previous = Button(pages, "ResourceVersionPrevious", "上一页", 68, () => { _detailPage--; ShowDetail(); });
            var next = Button(pages, "ResourceVersionNext", "下一页", 68, () => { _detailPage++; ShowDetail(); });
            _detailActions.Add(previous); _detailActions.Add(next);
            _shell.Tree.GetComponent<XsrUiInput>(previous)!.Enabled = _detailPage > 0; _shell.Tree.GetComponent<XsrUiInput>(next)!.Enabled = (_detailPage + 1) * 20 < detail.Versions.Count;
            Text(pages, $"{_detailPage + 1} / {(detail.Versions.Count + 19) / 20}", 12, Muted, 34);
        }
        _shell.Tree.GetComponent<XsrUiScroll>(_detailBody)!.OffsetY = 0;
    }

    private void UpdatePagination()
    {
        _shell.Tree.SetComponent(_status, new XsrUiText(_searching is not null ? "Modrinth · 加载中" : _result is null ? "Modrinth · 加载失败" : $"Modrinth · {_result.Total:N0} 个结果 · 第 {_filter.Page + 1} 页"));
        _shell.Tree.GetComponent<XsrUiInput>(_previous)!.Enabled = _searching is null && _filter.Page > 0;
        _shell.Tree.GetComponent<XsrUiInput>(_next)!.Enabled = _searching is null && _result is not null && (_filter.Page + 1) * 20 < _result.Total;
    }
    private void ShowFailure(XsrUiEntityId parent, string message, Action retry, List<XsrUiEntityId> actions)
    {
        Clear(parent, actions); Text(parent, message, 14, Muted, 54);
        actions.Add(Button(parent, "ResourceRetry", "重试", 80, retry));
    }
    internal void ConfigureInstanceFilter(XsrQueryRouter queries, Func<MinecraftInstallEditQuery?> selected)
    { _instanceQueries = queries; _selectedInstance = selected; }
    private void UseCurrentInstance()
    {
        if (_selectedInstance?.Invoke() is not { } query || _instanceQueries is null || !_instanceQueries.TryResolve(MinecraftInstallEditContract.Query, out var route)) return;
        Cancel();
        _instanceRequest = query;
        _instanceReading = _instanceQueries.QueryAsync<MinecraftInstallEditQuery, MinecraftInstallEditSnapshot>(route, query, cancellationToken: _stop.Token).AsTask();
        _shell.Tree.SetComponent(_status, new XsrUiText("正在读取当前版本…"));
        Wake(_instanceReading);
    }
    private void Cancel() { _stop.Cancel(); _stop.Dispose(); _stop = new(); _searching = null; _reading = null; _instanceReading = null; }
    private void CancelIcons() { _iconStop.Cancel(); _iconStop.Dispose(); _iconStop = new(); _icons.Clear(); }
    private void Wake(Task task) => _ = task.ContinueWith(_ =>
    {
        if (_disposed) return;
        try { _store.Publish(_store.Resolve(ResourcesPresentationState.Wake), Interlocked.Increment(ref _wake)); }
        catch (ObjectDisposedException) { }
    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private void Clear(XsrUiEntityId parent, List<XsrUiEntityId> actions)
    {
        foreach (var action in actions)
        {
            _actions.Remove(action);
            foreach (var key in _entities.Where(pair => pair.Value == action).Select(pair => pair.Key).ToArray()) _entities.Remove(key);
        }
        actions.Clear();
        foreach (var child in _shell.Tree.Children(parent).ToArray()) _shell.Tree.Destroy(child);
    }
    private XsrUiElement E(XsrUiEntityId entity) => _shell.Tree.GetComponent<XsrUiElement>(entity)!;
    private XsrUiEntityId Element(XsrUiEntityId parent, string name, XsrUiSemanticRole role = XsrUiSemanticRole.None, string? label = null)
    {
        var entity = _shell.Tree.Create(name); if (parent.IsAssigned) _shell.Tree.Attach(entity, parent);
        _shell.Tree.SetComponent(entity, new XsrUiElement()); _shell.Tree.SetComponent(entity, new XsrUiSemantic(role, label)); return entity;
    }
    private XsrUiEntityId Stack(XsrUiEntityId parent, string name, bool horizontal = false)
    {
        var entity = Element(parent, name); _shell.Tree.SetComponent(entity, new XsrUiStackPanel(horizontal ? XsrUiOrientation.Horizontal : XsrUiOrientation.Vertical) { Spacing = horizontal ? 12 : 2 }); return entity;
    }
    private XsrUiEntityId Card(XsrUiEntityId parent, string name)
    {
        var surface = Stack(parent, name); Style(surface, White, Ink, 10);
        var body = Stack(surface, name + ".Body", true); E(body).Padding = new(12, 8, 12, 8); return body;
    }
    private XsrUiEntityId Text(XsrUiEntityId parent, string value, double size, XsrUiColor color, double height, double weight = 400, int lines = 1)
    {
        var entity = Element(parent, "ResourceText", XsrUiSemanticRole.Text, value); E(entity).Height = height;
        _shell.Tree.SetComponent(entity, new XsrUiText(value) { MaxLines = lines, TrimOverflow = true }); Style(entity, XsrUiColor.Transparent, color, 0, size, weight); return entity;
    }
    private XsrUiEntityId Button(XsrUiEntityId parent, string name, string label, double width, Action action)
    {
        var entity = Element(parent, name, XsrUiSemanticRole.Button, label); E(entity).Height = 34;
        _shell.Tree.SetComponent(entity, new XsrUiText(label));
        // Stable names for acceptance tests and keyboard navigation.
        _entities[name] = entity;
        E(entity).Width = width; E(entity).VerticalAlignment = XsrUiAlignment.Center;
        _shell.Tree.SetComponent(entity, new XsrUiInput { Focusable = true, Clickable = true });
        _shell.Tree.SetComponent(entity, new XsrUiCommandBinding(Action)); Style(entity, Tint, Blue, 17);
        _shell.Tree.GetComponent<XsrUiVisualStyle>(entity)!.TextAlignment = XsrUiTextAlignment.Center;
        _actions[entity] = action; return entity;
    }
    internal XsrUiEntityId Find(string key) => _entities[key];
    private XsrUiEntityId Input(XsrUiEntityId parent, string name, string placeholder, double width)
    {
        var surface = Stack(parent, name + ".Surface"); Style(surface, Tint, Ink, 10);
        if (width > 0) E(surface).Width = width; else E(surface).Weight = 1;
        var entity = Element(surface, name, XsrUiSemanticRole.TextInput, placeholder); E(entity).Height = 34; E(entity).Padding = new(10, 0, 10, 0);
        _shell.Tree.SetComponent(entity, new XsrUiInput { Focusable = true, Clickable = true });
        _shell.Tree.SetComponent(entity, new XsrUiTextInput { Placeholder = placeholder }); Style(entity, XsrUiColor.Transparent, Ink, 0);
        return entity;
    }
    private void Segment(XsrUiEntityId parent, string name, string[] labels, Action<int> selected, double width = 84)
    {
        var track = Stack(parent, name, true); _shell.Tree.GetComponent<XsrUiStackPanel>(track)!.Spacing = 0; E(track).Width = width * labels.Length;
        Style(track, Tint, Ink, 10);
        var thumb = Element(track, name + "Thumb"); E(thumb).IsVisible = false; Style(thumb, White, Ink, 8);
        _shell.Tree.SetComponent(thumb, new XsrUiTransition()); _shell.Tree.SetComponent(track, new XsrUiSegmentedTrack(thumb));
        List<XsrUiEntityId> options = [];
        for (int i = 0; i < labels.Length; i++)
        {
            int index = i;
            var option = Button(track, name + "." + i, labels[i], width, () =>
            {
                for (int j = 0; j < options.Count; j++) _shell.Tree.GetComponent<XsrUiSelection>(options[j])!.IsSelected = j == index;
                _shell.Tree.GetComponent<XsrUiSegmentedTrack>(track)!.Selected = options[index]; selected(index);
            });
            options.Add(option); Style(option, XsrUiColor.Transparent, Ink, 8);
            _shell.Tree.GetComponent<XsrUiVisualStyle>(option)!.TextAlignment = XsrUiTextAlignment.Center;
            _shell.Tree.SetComponent(option, new XsrUiSelection { IsSelected = i == 0 });
        }
        _shell.Tree.GetComponent<XsrUiSegmentedTrack>(track)!.Selected = options[0];
    }
    private void Style(XsrUiEntityId entity, XsrUiColor background, XsrUiColor foreground, double radius, double size = 13, double weight = 400)
    {
        _shell.Tree.SetComponent(entity, new XsrUiVisualStyle
        {
            Background = background,
            Foreground = foreground,
            CornerRadius = radius,
            FontSize = size,
            FontWeight = weight,
            Surface = XsrUiSurfaceKind.Solid,
            Hover = DesktopUiPalette.CapsuleHover
        });
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
    }
    public void Dispose() { _disposed = true; _iconStop.Cancel(); _iconStop.Dispose(); _stop.Cancel(); _stop.Dispose(); _intents.IntentEmitted -= OnIntent; _shell.Renderer.FramePreparing -= OnFrame; }
}
