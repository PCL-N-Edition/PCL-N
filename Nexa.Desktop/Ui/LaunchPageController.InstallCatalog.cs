using Nexa.Pxml;
using Nexa.Services.Minecraft.Install;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Ui;

internal sealed partial class LaunchPageController
{
    private readonly XsrCommandRouter? _installCatalogCommands;
    private readonly XsrQueryRouter? _installCatalogQueries;
    private readonly XsrCommandRouter? _installRunCommands;
    private bool _installGameChosen;
    private Task _installPrefetchTask = Task.CompletedTask;
    private void PrefetchInstallCatalog(string game)
    {
        if (_installCatalogCommands is not null && _installCatalogCommands.TryResolve(InstallCatalogRoutes.Prefetch, out XsrCommandId id))
            _installPrefetchTask = _installCatalogCommands.Dispatch(id, new InstallCatalogPrefetchCommand(game), cancellationToken: _lifetimeCancellation.Token).Completion;
    }
    private static InstallLoader? ParseInstallLoader(string? value) => Enum.TryParse(value?.Replace(" ", "", StringComparison.Ordinal), out InstallLoader loader) ? loader : null;
    private void ResetInstallSelection()
    {
        _editingInstall = false;
        _installEdit = null;
        _installEditRead = null;
        SetInstallEditorLabels(false);
        _installGameChosen = false;
        _selectedInstallVersion = "";
        _selectedInstallBuilds.Clear();
        _selectedInstallAddons.Clear();
        _selectedInstallLoader = "原版 Minecraft";
        _activeJavaInstallPage = "JavaMinecraftPage";
        _catalogRequest = "";
        _catalogSearch = "";
        _catalogRevision = -1;
        _shell.Renderer.SetTextInputValue(_javaInstallEntities["JavaInstallVersionInput"], "");
        UpdateJavaInstallSubpageVisibility("JavaMinecraftPage");
    }
    private void ChooseInstallGame(string version)
    {
        if (_editingInstall) return;
        _installGameChosen = true;
        _selectedInstallVersion = version;
        _selectedInstallBuilds.Clear();
        SelectInstallLoader("原版 Minecraft", string.Empty);
        UpdateJavaInstallSubpageVisibility();
        PrefetchInstallCatalog(version);
        _locateCatalogSelection = true;
        _catalogRevision = -1;
    }
    private Task _installCatalogTask = Task.CompletedTask;
    private string _catalogRequest = "";
    private long _catalogRevision = -1;
    private long _catalogStateRevision = -1;
    private int _catalogFirst = -1, _catalogCount;
    private bool _locateCatalogSelection;
    private string _catalogSearch = "";
    private long _filteredRevision = -1;
    private IReadOnlyList<InstallCatalogVersion> _filteredGames = [];
    private readonly Dictionary<string, InstallCatalogSnapshot> _catalogCache = [];
    private string CurrentCatalogKey => ActiveCatalogLoader is null ? "games" : _selectedInstallVersion + ":" + _activeJavaInstallPage;
    private readonly Dictionary<InstallLoader, string> _selectedInstallBuilds = [];
    private readonly Dictionary<string, XsrUiEntityId> _catalogHosts = [];
    private readonly Dictionary<XsrUiEntityId, InstallCatalogVersion> _catalogRows = [];
    private readonly Dictionary<string, XsrUiEntityId> _catalogStatus = [];
    private static readonly XsrSemanticId CatalogSelect = XsrSemanticId.Parse("ui.install.catalog.select");
    private static readonly XsrSemanticId CatalogRetry = XsrSemanticId.Parse("ui.install.catalog.retry");
    private static readonly PxmlHostIr CatalogRowTemplate = PxmlCompiler.Compile(PxmlParser.Parse("""
        <Button xmlns="https://pcln.dev/pxml/2026" Key="CatalogRow" Label="选择版本" Command="ui.install.catalog.select" Height="48">
          <StackPanel Orientation="Horizontal" Spacing="12" Weight="1" Margin="12,0,12,0">
            <Image Key="CatalogCheck" Source="lucide/check" Width="18" Height="18" VerticalAlignment="Center" />
            <Text Key="CatalogName" Weight="1" Height="24" VerticalAlignment="Center" />
            <Text Key="CatalogDetail" Width="160" Height="20" VerticalAlignment="Center" />
          </StackPanel>
        </Button>
        """));
    private InstallLoader? ActiveCatalogLoader => (_activeJavaInstallPage == "JavaOptiFabricPage" ? "OptiFabric" : _activeJavaInstallPage == "JavaFabricApiPage" ? "Fabric API" : _activeJavaInstallPage == "JavaQslPage" ? "QSL" : JavaInstallSubpages.First(p => p.PageKey == _activeJavaInstallPage).Loader) switch
    {
        "Fabric API" => InstallLoader.FabricApi,
        "QSL" => InstallLoader.Qsl,
        "OptiFabric" => InstallLoader.OptiFabric,
        "Forge" => InstallLoader.Forge,
        "Cleanroom" => InstallLoader.Cleanroom,
        "NeoForge" => InstallLoader.NeoForge,
        "Fabric" => InstallLoader.Fabric,
        "Legacy Fabric" => InstallLoader.LegacyFabric,
        "Quilt" => InstallLoader.Quilt,
        "LabyMod" => InstallLoader.LabyMod,
        "OptiFine" => InstallLoader.OptiFine,
        "LiteLoader" => InstallLoader.LiteLoader,
        _ => null,
    };
    private void InitializeInstallCatalog()
    {
        foreach (JavaInstallSubpage page in JavaInstallSubpages)
        {
            XsrUiEntityId parent = _javaInstallEntities[page.PageKey];
            // Version names, status and selections come from the service, never a baked-in list.
            XsrUiEntityId host = page.PageKey == "JavaMinecraftPage" ? _javaInstallEntities["JavaMinecraftVersions"] : _shell.Tree.Create(page.PageKey + "Rows");
            if (page.PageKey != "JavaMinecraftPage")
            {
                _shell.Tree.Attach(host, parent);
                _shell.Tree.SetComponent(host, new XsrUiElement());
                _shell.Tree.SetComponent(host, new XsrUiStackPanel(XsrUiOrientation.Vertical) { Spacing = 2 });
                _shell.Tree.SetComponent(parent, new XsrUiScroll { ShowsVerticalIndicator = true });
            }
            if (page.Loader is not null)
            {
                string loaderKey = "JavaLoader" + page.PageKey[4..^4];
                _shell.Tree.GetComponent<XsrUiElement>(_javaInstallEntities[loaderKey])!.IsVisible = false;
            }
            if (page.PageKey is "JavaFabricApiPage" or "JavaQslPage")
                _shell.Tree.GetComponent<XsrUiElement>(_javaInstallEntities[page.PageKey == "JavaFabricApiPage" ? "JavaFabricApiSelect" : "JavaQslSelect"])!.IsVisible = false;
            _shell.Tree.GetComponent<XsrUiStackPanel>(host)!.Spacing = 0;
            _shell.Tree.SetComponent(host, new XsrUiStableContent());
            _shell.Tree.SetComponent(parent, new XsrUiScrollGesture());
            _catalogHosts[page.PageKey] = host;
            XsrUiEntityId status = _shell.Tree.Create(page.PageKey + "Status");
            _shell.Tree.Attach(status, parent);
            _shell.Tree.SetComponent(status, new XsrUiElement { Height = 40 });
            _shell.Tree.SetComponent(status, new XsrUiInput { Focusable = true, Clickable = true });
            _shell.Tree.SetComponent(status, new XsrUiCommandBinding(CatalogRetry));
            _shell.Tree.SetComponent(status, new XsrUiSemantic(XsrUiSemanticRole.Button, "重新获取版本"));
            _shell.Tree.SetComponent(status, new XsrUiText("正在获取版本…"));
            _shell.Tree.SetComponent(status, new XsrUiVisualStyle { Foreground = SecondaryText, FontSize = 13, WrapText = true });
            _catalogStatus[page.PageKey] = status;
            // Keep dependency notices ahead of potentially very long version lists.
            _shell.Tree.Detach(host);
            _shell.Tree.Attach(host, parent);
        }
    }
    private void RequestInstallCatalog(bool force = false)
    {
        if (_installCatalogCommands is null || !_catalogHosts.ContainsKey(_activeJavaInstallPage)) return;
        InstallLoader? loader = ActiveCatalogLoader;
        string key = CurrentCatalogKey;
        if (!force && key == _catalogRequest) return;
        _catalogRequest = key;
        _locateCatalogSelection = true;
        _catalogRevision = -1;
        _catalogFirst = -1;
        if (!force && _catalogCache.ContainsKey(key)) return;
        if (!force && (_store.ReadAppliedValue(_store.Resolve(InstallCatalogStateContract.StateKey)) as InstallCatalogState)?.Catalogs
            .Any(item => item.Revision > 0 && item.Loader == loader && (loader is null || item.GameVersion == _selectedInstallVersion)) == true) return;
        _catalogCache.Remove(key);
        if (_installCatalogCommands.TryResolve(InstallCatalogRoutes.Read, out XsrCommandId id))
            _installCatalogTask = _installCatalogCommands.Dispatch(id, new InstallCatalogReadCommand(loader is null ? "" : _selectedInstallVersion, loader, force), cancellationToken: _lifetimeCancellation.Token).Completion;
    }
    private bool HandleInstallCatalogIntent(DesktopUiIntentEventArgs e)
    {
        if (_shell.Stage.Navigation.Current != _javaInstallPage) return false;
        if (e.Intent.Command == CatalogRetry)
        {
            RequestInstallCatalog(true);
            return true;
        }
        if (e.Intent.Command != CatalogSelect || !_catalogRows.TryGetValue(e.Intent.Source, out InstallCatalogVersion? version)) return false;
        if (ActiveCatalogLoader is null)
        {
            if (_editingInstall) return true;
            if (_installGameChosen && _selectedInstallVersion == version.Id)
            {
                _installGameChosen = false;
                PrefetchInstallCatalog("");
                _selectedInstallBuilds.Clear();
                SelectInstallLoader("原版 Minecraft", string.Empty);
                _shell.Renderer.SetTextInputValue(_javaInstallEntities["JavaInstallVersionInput"], "");
                _catalogRevision = -1;
                return true;
            }
            ChooseInstallGame(version.Id);
            _shell.Renderer.SetTextInputValue(_javaInstallEntities["JavaInstallVersionInput"], version.Id);
            SelectInstallLoader("原版 Minecraft", string.Empty);
        }
        else
        {
            var result = QueryInstallEligibility(ActiveCatalogLoader, [version.Id], version.Id);
            if (result is null || result.Rejection is not null) return true;
            _selectedInstallBuilds.Clear();
            _selectedInstallAddons.Clear();
            foreach (var item in result.Selection)
            {
                _selectedInstallBuilds[item.Loader] = item.Version;
                if (result.Loaders.Any(loader => loader.Loader == item.Loader && loader.IsAddon))
                    _selectedInstallAddons.Add(item.Loader == InstallLoader.FabricApi ? "Fabric API" : item.Loader == InstallLoader.Qsl ? "QSL" : item.Loader.ToString());
            }
            _selectedInstallLoader = result.PrimaryLoader is { } primary
                ? JavaInstallSubpages.First(page => ParseInstallLoader(page.Loader) == primary).Loader! : "原版 Minecraft";
            UpdateJavaInstallSubpageVisibility();
        }
        _catalogRevision = -1;
        return true;
    }
    private InstallEligibilityResult? QueryInstallEligibility(InstallLoader? catalog = null, IReadOnlyList<string>? candidates = null, string? toggle = null)
    {
        if (_installCatalogQueries is null || !_installCatalogQueries.TryResolve(InstallEligibilityContract.Query, out XsrQueryId id)) return null;
        var query = new InstallEligibilityQuery(_installGameChosen ? _selectedInstallVersion : "",
            Array.AsReadOnly(_selectedInstallBuilds.Select(pair => new InstallBuildSelection(pair.Key, pair.Value)).ToArray()),
            ParseInstallLoader(_selectedInstallLoader), catalog, candidates, toggle, _installEdit?.Selection);
        var result = _installCatalogQueries.QueryAsync<InstallEligibilityQuery, InstallEligibilityResult>(id, query);
        // This route is a bounded in-memory projection. Never synchronously wait for an incomplete query.
        if (!result.IsCompletedSuccessfully) return null;
        var completed = result.Result;
        return completed.IsSuccess ? completed.Value : null;
    }
    private void ProjectInstallCatalog()
    {
        if (_shell.Stage.Navigation.Current != _javaInstallPage || _installCatalogCommands is null) return;
        XsrUiEntityId inputEntity = _javaInstallEntities["JavaInstallVersionInput"];
        XsrUiTextInput input = _shell.Tree.GetComponent<XsrUiTextInput>(inputEntity)!;
        input.Placeholder = _installGameChosen ? "版本名称" : "搜索版本";
        _shell.Tree.GetComponent<XsrUiSemantic>(inputEntity)!.Label = input.Placeholder;
        RequestInstallCatalog();
        var catalogState = _store.ReadAppliedValue(_store.Resolve(InstallCatalogStateContract.StateKey)) as InstallCatalogState;
        if (catalogState is not null && catalogState.Revision != _catalogStateRevision)
        {
            _catalogStateRevision = catalogState.Revision;
            UpdateJavaInstallSubpageVisibility();
            _catalogRevision = -1;
            foreach (string key in _catalogCache.Keys.ToArray())
            {
                var cached = _catalogCache[key];
                if (catalogState.Catalogs.Any(item => item.Loader == cached.Loader && item.GameVersion == cached.GameVersion && item.Revision > cached.Revision))
                    _catalogCache.Remove(key);
            }
        }
        string cacheKey = CurrentCatalogKey;
        InstallCatalogSnapshot? snapshot = _installEdit is { } edit && ActiveCatalogLoader is null
            ? new InstallCatalogSnapshot(1, "", null, [new(edit.GameVersion, "不可更改")], false)
            : _catalogCache.GetValueOrDefault(cacheKey);
        if (snapshot is null)
        {
            snapshot = (_store.ReadAppliedValue(_store.Resolve(InstallCatalogStateContract.StateKey)) as InstallCatalogState)?.Catalogs
                .FirstOrDefault(item => item.Loader == ActiveCatalogLoader && (item.Loader is null || item.GameVersion == _selectedInstallVersion));
            if (snapshot is null || snapshot.Loader != ActiveCatalogLoader
                || snapshot.Loader is not null && snapshot.GameVersion != _selectedInstallVersion) return;
            if (!snapshot.Loading && snapshot.Error is null)
            {
                if (_catalogCache.Count >= 24) _catalogCache.Clear();
                _catalogCache[cacheKey] = snapshot;
            }
        }
        if (_installEdit is not null && snapshot.Loader is { } selectedLoader
            && _selectedInstallBuilds.TryGetValue(selectedLoader, out string? selectedBuild)
            && !snapshot.Versions.Any(version => version.Id == selectedBuild))
            snapshot = snapshot with { Versions = new[] { new InstallCatalogVersion(selectedBuild, "已安装") }.Concat(snapshot.Versions).ToArray() };
        if (!_catalogHosts.TryGetValue(_activeJavaInstallPage, out XsrUiEntityId host)) return;
        XsrUiScroll scroll = _shell.Tree.GetComponent<XsrUiScroll>(_javaInstallEntities[_activeJavaInstallPage])!;
        string query = !_installGameChosen && snapshot.Loader is null ? input.ReadDraft().Trim() : "";
        if (query != _catalogSearch)
        {
            _catalogSearch = query; _filteredRevision = -1; _catalogRevision = -1;
            if (snapshot.Loader is null) scroll.OffsetY = 0;
        }
        if (snapshot.Loader is null && query.Length > 0)
        {
            if (_filteredRevision != snapshot.Revision)
            {
                _filteredGames = snapshot.Versions.Where(version => version.Id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
                _filteredRevision = snapshot.Revision;
            }
            snapshot = snapshot with { Versions = _filteredGames };
        }
        if (_locateCatalogSelection && !snapshot.Loading)
        {
            _locateCatalogSelection = false;
            string? selected = snapshot.Loader is null ? _installGameChosen ? _selectedInstallVersion : null
                : _selectedInstallBuilds.GetValueOrDefault(snapshot.Loader.Value);
            int selectedIndex = -1;
            if (selected is not null)
                for (int index = 0; index < snapshot.Versions.Count; index++)
                    if (snapshot.Versions[index].Id == selected) { selectedIndex = index; break; }
            if (selectedIndex >= 0)
            {
                _shell.Renderer.FinishScrollInertia(_javaInstallEntities[_activeJavaInstallPage]);
                scroll.OffsetY = Math.Max(0, selectedIndex * 50 - Math.Max(0, _shell.Renderer.Viewport.Height - 240) / 2);
                _catalogRevision = -1;
            }
        }
        int first = Math.Clamp((int)(Math.Max(0, scroll.OffsetY - 48) / 50) - 4, 0, Math.Max(0, snapshot.Versions.Count - 1));
        int count = Math.Min(snapshot.Versions.Count - first, (int)Math.Ceiling(_shell.Renderer.Viewport.Height / 50) + 10);
        if (snapshot.Revision == _catalogRevision && first == _catalogFirst && count == _catalogCount) return;
        _catalogRevision = snapshot.Revision; _catalogFirst = first; _catalogCount = count;
        // A fixed-height window bounds ECS/native controls independently of catalog length.
        // Keep overlapping rows alive, preserving focus and avoiding repeated entry animations.
        var wanted = snapshot.Versions.Skip(first).Take(count).Select(v => v.Id).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, XsrUiEntityId> retained = [];
        foreach (XsrUiEntityId child in _shell.Tree.Children(host).ToArray())
        {
            if (_catalogRows.TryGetValue(child, out InstallCatalogVersion? old) && wanted.Contains(old.Id))
            { retained[old.Id] = child; _shell.Tree.Detach(child); }
            else { _catalogRows.Remove(child); _shell.Tree.Destroy(child); }
        }
        void Spacer(string name, double height)
        {
            if (height <= 0) return;
            XsrUiEntityId spacer = _shell.Tree.Create(name);
            _shell.Tree.SetComponent(spacer, new XsrUiElement { Height = height });
            _shell.Tree.Attach(spacer, host);
        }
        Spacer("CatalogBefore", first * 50);
        var eligibility = QueryInstallEligibility(snapshot.Loader, snapshot.Versions.Skip(first).Take(count).Select(v => v.Id).ToArray());
        string message = snapshot.Loading ? "正在获取版本…" : snapshot.Unsupported ?? (snapshot.Error is not null ? "获取失败，请重试。" : snapshot.Versions.Count == 0 ? (query.Length > 0 ? "没有匹配的版本。" : "此游戏版本暂无兼容版本。") : snapshot.Versions[0].Warning is not null ? "部分来源获取失败，点击重试。" : "");
        if (message.Length == 0 && snapshot.Loader is { } noticeLoader && snapshot.Versions.Count > 0)
            message = eligibility?.Builds.Values.Select(item => item.Notice).FirstOrDefault(notice => notice is not null) ?? "";
        XsrUiEntityId status = _catalogStatus[_activeJavaInstallPage];
        _shell.Tree.GetComponent<XsrUiText>(status)!.Content = message;
        _shell.Tree.GetComponent<XsrUiElement>(status)!.IsVisible = message.Length > 0;
        foreach (InstallCatalogVersion version in snapshot.Versions.Skip(first).Take(count))
        {
            string key = snapshot.Loader is null ? "game:" + version.Id : "loader:" + version.Id;
            bool selected = _installGameChosen && (snapshot.Loader is null ? version.Id == _selectedInstallVersion : _selectedInstallBuilds.GetValueOrDefault(snapshot.Loader.Value) == version.Id);
            string? conflict = snapshot.Loader is { } kind ? eligibility?.Builds.GetValueOrDefault(version.Id)?.Conflict : null;
            string detail = _editingInstall && snapshot.Loader is null ? "不可更改" : conflict ?? (snapshot.Loader is not null ? version.Detail : version.Stable ? "正式版" : "测试版");
            PxmlIrNode Project(PxmlIrNode node) => node with
            {
                Key = node.Key + ":" + key,
                Label = node.Key == "CatalogRow" ? (selected ? "已选择 " : "选择 ") + version.Id : node.Label,
                Content = node.Key switch { "CatalogName" => version.Id, "CatalogDetail" => detail, _ => node.Content },
                Children = [.. node.Children.Select(Project)],
            };
            XsrUiEntityId row;
            if (retained.TryGetValue(version.Id, out row)) _shell.Tree.Attach(row, host);
            else
            {
                row = PxmlUiLoader.Load(new(Project(CatalogRowTemplate.Root)), _shell.Tree, _store, host);
                _shell.Tree.GetComponent<XsrUiElement>(row)!.Margin = new XsrUiThickness(0, 0, 0, 2);
            }
            _shell.Tree.GetComponent<XsrUiSemantic>(row)!.Label = _editingInstall && snapshot.Loader is null
                ? "Minecraft " + version.Id + "，不可更改" : (selected ? "取消选择 " : "选择 ") + version.Id;
            _shell.Tree.GetComponent<XsrUiInput>(row)!.Enabled = !(_editingInstall && snapshot.Loader is null) && (selected || conflict is null);
            _catalogRows[row] = version;
            _shell.Tree.SetComponent(row, new XsrUiSelection { IsSelected = selected });
            ApplyVisual(row, selected ? ProfileSurface : XsrUiColor.Transparent, PrimaryText, XsrUiCornerRadii.Inset, hover: PickerBackground);
            _shell.Tree.Walk(row, entity =>
            {
                string name = _shell.Tree.Name(entity);
                if (name.StartsWith("CatalogDetail", StringComparison.Ordinal)) _shell.Tree.GetComponent<XsrUiText>(entity)!.Content = detail;
                StyleText(entity, name.StartsWith("CatalogDetail", StringComparison.Ordinal) ? SecondaryText : PrimaryText, 14);
                if (name.StartsWith("CatalogCheck", StringComparison.Ordinal)) ApplyVisual(entity, XsrUiColor.Transparent, selected ? BadgeText : XsrUiColor.Transparent, 0);
                return true;
            });
        }
        Spacer("CatalogAfter", (snapshot.Versions.Count - first - count) * 50);
        _shell.Tree.MarkDirty(_javaInstallPage, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }
}
