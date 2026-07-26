// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using PCL.Application.Downloads;
using PCL.Application.Instances;
using PCL.Core.Utils;
using PCL.Core.Utils.Hash;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Features.Community;
using PCL.Desktop.Features.Launching.Views;
using PCL.Desktop.Features.Shared;

namespace PCL.Desktop.Features.Instances.Views;

public sealed record InstanceResourceProjectRequest(
    CommunityResourceEntry Entry,
    CommunityResourceCategory Category,
    CommunitySearchOptions Options);

public partial class PageInstanceResourceRight : MyPageRight
{
    private const int RenderBatchSize = 16;
    private readonly Func<CompositeCommunityResourceCatalog> _catalogFactory;
    private readonly Func<string, MinecraftModMetadata?> _metadataReader;
    private readonly MyLocalModItem.SwipeSelect _swipeSelection = new();
    private LaunchInstanceInfo? _instance;
    private InstancePageSubType _page;
    private InstanceResourceKind _kind = InstanceResourceKind.Mod;
    private ResourceFilter _filter;
    private ResourceSort _sort = ResourceSort.FileName;
    private string _folder = string.Empty;
    private List<ResourceEntry> _entries = [];
    private bool _isLoading;
    private bool _catalogUiRefreshScheduled;
    private bool _isUpdatingFilter;
    private bool _isUpdatingSelection;
    private int _contextVersion;
    private int _reloadVersion;
    private int _renderVersion;
    private int _catalogScanVersion;
    private int _searchVersion;
    private CancellationTokenSource? _contextCancellation;
    private CancellationTokenSource? _reloadCancellation;
    private CancellationTokenSource? _catalogScanCancellation;
    private CancellationTokenSource? _searchCancellation;
    private Dictionary<string, LocalCatalogMatch> _catalogByPath =
        new Dictionary<string, LocalCatalogMatch>(GetPathComparer());
    private HashSet<string> _searchResultPaths = new(GetPathComparer());
    private Dictionary<string, MyLocalModItem> _entryItems =
        new Dictionary<string, MyLocalModItem>(GetPathComparer());
    private readonly HashSet<string> _selectedPaths = new(GetPathComparer());

    public PageInstanceResourceRight()
        : this(static () => new CompositeCommunityResourceCatalog())
    {
    }

    internal PageInstanceResourceRight(
        Func<CompositeCommunityResourceCatalog> catalogFactory,
        Func<string, MinecraftModMetadata?>? metadataReader = null)
    {
        _catalogFactory = catalogFactory ?? throw new ArgumentNullException(nameof(catalogFactory));
        _metadataReader = metadataReader ?? ReadModMetadata;
        AvaloniaXamlLoader.Load(this);
        PanScroll = this.FindControl<MyScrollViewer>("PanBack");
        AddHandler(
            InputElement.PointerReleasedEvent,
            (_, args) => MyLocalModItem.CompleteSwipeSelection(_swipeSelection, args),
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        WireControls();
    }

    public event EventHandler<string>? OpenFolderRequested;

    public event EventHandler<InstancePageSubType>? DownloadRequested;

    public event EventHandler<InstanceResourceProjectRequest>? OpenProjectRequested;

    public event EventHandler<string>? StatusMessage;

    public string ResourceDirectory => _folder;

    public override void Dispose()
    {
        Interlocked.Increment(ref _contextVersion);
        Interlocked.Increment(ref _renderVersion);
        CancelAndDispose(ref _contextCancellation);
        CancelPendingWork();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    public void SetContext(LaunchInstanceInfo instance, InstancePageSubType page)
    {
        ArgumentNullException.ThrowIfNull(instance);
        InstanceResourceKind kind = InstancePageRegistry.GetResourceKind(page);
        if (kind == InstanceResourceKind.None)
            kind = InstanceResourceKind.Mod;

        string relativePath = InstancePageRegistry.GetFolderRelativePath(page);
        if (string.IsNullOrWhiteSpace(relativePath))
            relativePath = "mods";

        int context = Interlocked.Increment(ref _contextVersion);
        Interlocked.Increment(ref _renderVersion);
        CancelAndDispose(ref _contextCancellation);
        CancelPendingWork();
        CancellationTokenSource cancellation = new();
        _contextCancellation = cancellation;
        _ = SetContextAsync(instance, page, kind, relativePath, context, cancellation.Token);
    }

    private async Task SetContextAsync(
        LaunchInstanceInfo instance,
        InstancePageSubType page,
        InstanceResourceKind kind,
        string relativePath,
        int context,
        CancellationToken cancellationToken)
    {
        try
        {
            string gameDir = await Task.Run(
                    () => InstanceGameDirectory.ResolveAsync(instance, cancellationToken))
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (context != _contextVersion)
                    return;

                _instance = instance;
                _page = page;
                _kind = kind;
                _folder = Path.Combine(gameDir, relativePath);
                ApplyKindChrome();
                Reload();
            }, DispatcherPriority.Background, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public void SetDataPackFolder(string saveFolder)
    {
        Interlocked.Increment(ref _contextVersion);
        CancelAndDispose(ref _contextCancellation);
        CancelPendingWork();
        _instance = null;
        _page = InstancePageSubType.Saves;
        _kind = InstanceResourceKind.DataPack;
        _folder = Path.Combine(saveFolder, "datapacks");
        ApplyKindChrome();
        Reload();
    }

    public void Reload()
    {
        if (string.IsNullOrWhiteSpace(_folder))
            return;

        CancelPendingWork();
        int reload = Interlocked.Increment(ref _reloadVersion);
        int context = _contextVersion;
        string folder = _folder;
        InstanceResourceKind kind = _kind;
        CancellationTokenSource cancellation = new();
        _reloadCancellation = cancellation;

        _entries = [];
        _isLoading = true;
        _catalogByPath = new Dictionary<string, LocalCatalogMatch>(GetPathComparer());
        _searchResultPaths = new HashSet<string>(GetPathComparer());
        _entryItems = new Dictionary<string, MyLocalModItem>(GetPathComparer());
        _selectedPaths.Clear();
        UpdateSelectionBar();
        RefreshUI();
        _ = ReloadAsync(folder, kind, context, reload, cancellation.Token);
    }

    private async Task ReloadAsync(
        string folder,
        InstanceResourceKind kind,
        int context,
        int reload,
        CancellationToken cancellationToken)
    {
        try
        {
            List<ResourceEntry> entries = await Task.Run(
                    () => LoadResourceEntries(folder, kind, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            await PublishLoadedEntriesAsync(entries, context, reload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (context == _contextVersion && reload == _reloadVersion)
                {
                    _isLoading = false;
                    RefreshUI();
                    StatusMessage?.Invoke(this, Text("Instance.Resource.LoadFailed"));
                }
            });
        }
    }

    private List<ResourceEntry> LoadResourceEntries(
        string folder,
        InstanceResourceKind kind,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        List<ResourceEntry> entries = [];
        foreach (string path in Directory.EnumerateFileSystemEntries(folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsAcceptedPath(path, kind))
                entries.Add(CreateResourceEntry(path, kind));
        }
        return entries;
    }

    private async Task PublishLoadedEntriesAsync(
        List<ResourceEntry> entries,
        int context,
        int reload,
        CancellationToken cancellationToken)
    {
        List<ResourceEntry>? showing = null;
        StackPanel? list = null;
        int render = 0;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!IsReloadCurrent(context, reload, cancellationToken))
                return;

            _entries = entries;
            _isLoading = false;
            _catalogByPath = new Dictionary<string, LocalCatalogMatch>(GetPathComparer());
            _searchResultPaths = new HashSet<string>(GetPathComparer());
            _entryItems = new Dictionary<string, MyLocalModItem>(GetPathComparer());
            render = Interlocked.Increment(ref _renderVersion);
            UpdateFilterControls();
            showing = GetShowingEntries();
            UpdateViewChrome(showing);
            list = this.FindControl<StackPanel>("PanList");
            list?.Children.Clear();
        });

        if (showing is null || list is null)
            return;

        for (int start = 0; start < showing.Count; start += RenderBatchSize)
        {
            int batchStart = start;
            bool published = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!IsReloadCurrent(context, reload, cancellationToken) || render != _renderVersion)
                    return false;

                int end = Math.Min(batchStart + RenderBatchSize, showing.Count);
                for (int index = batchStart; index < end; index++)
                {
                    ResourceEntry entry = showing[index];
                    MyLocalModItem item = CreateEntryItem(entry);
                    _entryItems[entry.FullPath] = item;
                    list.Children.Add(item);
                }
                return true;
            }, DispatcherPriority.Background, cancellationToken);
            if (!published)
                break;
            await Task.Yield();
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!IsReloadCurrent(context, reload, cancellationToken))
                return;
            StartCatalogScan();
            if (IsSearching)
                StartSearch(debounce: false);
        }, DispatcherPriority.Background, cancellationToken);
    }

    private bool IsReloadCurrent(int context, int reload, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        context == _contextVersion &&
        reload == _reloadVersion;

    private void WireControls()
    {
        WireButton("BtnManageOpen", OpenCurrentFolder);
        WireButton("BtnHintOpen", OpenCurrentFolder);
        WireButton("BtnManageDownload", RequestDownload);
        WireButton("BtnHintDownload", RequestDownload);
        WireButton("BtnManageInstall", () => _ = InstallFromFilesAsync());
        WireButton("BtnHintInstall", () => _ = InstallFromFilesAsync());
        WireButton("BtnManageSelectAll", ToggleAllSelected);
        WireIconTextButton("BtnSelectUpdate", () => _ = UpdateSelectedAsync());
        WireIconTextButton("BtnSelectEnable", () => _ = SetSelectedEnabledAsync(enable: true));
        WireIconTextButton("BtnSelectDisable", () => _ = SetSelectedEnabledAsync(enable: false));
        WireIconTextButton("BtnSelectDelete", () => _ = DeleteSelectedAsync());
        WireIconTextButton("BtnSelectCancel", () => ChangeAllSelected(false));

        if (this.FindControl<MySearchBox>("SearchBox") is { } searchBox)
        {
            searchBox.TextChanged += (_, _) => StartSearch(debounce: true);
            searchBox.KeyDown += (_, args) =>
            {
                if (IsSelectAllGesture(args) && string.IsNullOrEmpty(searchBox.Text))
                {
                    ChangeAllSelected(true);
                    args.Handled = true;
                }
            };
        }
        if (this.FindControl<MyIconTextButton>("BtnSort") is { } sort)
            sort.Click += (_, _) => CycleSort();

        KeyDown += (_, args) =>
        {
            if (IsSelectAllGesture(args))
            {
                ChangeAllSelected(true);
                args.Handled = true;
            }
        };

        foreach (MyRadioButton radioButton in new[]
                 {
                     this.FindControl<MyRadioButton>("BtnFilterAll"),
                     this.FindControl<MyRadioButton>("BtnFilterEnabled"),
                     this.FindControl<MyRadioButton>("BtnFilterDisabled"),
                     this.FindControl<MyRadioButton>("BtnFilterCanUpdate"),
                     this.FindControl<MyRadioButton>("BtnFilterError"),
                     this.FindControl<MyRadioButton>("BtnFilterDuplicate")
                 }.OfType<MyRadioButton>())
        {
            radioButton.Check += (sender, _) =>
            {
                if (_isUpdatingFilter)
                    return;
                if (sender.Tag is string text && int.TryParse(text, out int value))
                    _filter = (ResourceFilter)value;
                RefreshUI();
            };
        }
    }

    private void WireButton(string name, Action action)
    {
        if (this.FindControl<MyButton>(name) is { } button)
            button.Click += (_, _) => action();
    }

    private void ApplyKindChrome()
    {
        if (this.FindControl<MyCard>("PanListBack") is { } listBack)
            listBack.Title = Text("Instance.Resource.ListTitle", KindDisplayName(_kind));
        if (this.FindControl<TextBlock>("TxtEmptyTitle") is { } title)
            title.Text = Text("Instance.Resource.Empty.Title", KindDisplayName(_kind));
        if (this.FindControl<TextBlock>("TxtEmptyDescription") is { } description)
            description.Text = Text("Instance.Resource.Empty.Description", KindDisplayName(_kind));

        bool supportsDisable = _kind == InstanceResourceKind.Mod;
        if (this.FindControl<MyRadioButton>("BtnFilterEnabled") is { } enabled)
            enabled.IsVisible = supportsDisable;
        if (this.FindControl<MyRadioButton>("BtnFilterDisabled") is { } disabled)
            disabled.IsVisible = supportsDisable;
        if (this.FindControl<MyRadioButton>("BtnFilterCanUpdate") is { } canUpdate)
            canUpdate.IsVisible = supportsDisable;
        if (this.FindControl<MyRadioButton>("BtnFilterError") is { } error)
            error.IsVisible = supportsDisable;
        if (this.FindControl<MyRadioButton>("BtnFilterDuplicate") is { } duplicate)
            duplicate.IsVisible = supportsDisable;
        if (this.FindControl<MyIconTextButton>("BtnSelectEnable") is { } selectEnable)
            selectEnable.IsVisible = supportsDisable;
        if (this.FindControl<MyIconTextButton>("BtnSelectDisable") is { } selectDisable)
            selectDisable.IsVisible = supportsDisable;
        if (!supportsDisable)
        {
            _filter = ResourceFilter.All;
            this.FindControl<MyRadioButton>("BtnFilterAll")?.SetChecked(true, false, false);
        }

        bool canDownload = _kind is not InstanceResourceKind.Schematic;
        if (this.FindControl<MyButton>("BtnManageDownload") is { } download)
            download.IsVisible = canDownload;
        if (this.FindControl<MyButton>("BtnHintDownload") is { } hintDownload)
            hintDownload.IsVisible = canDownload;
    }

    private void RefreshUI()
    {
        int render = Interlocked.Increment(ref _renderVersion);
        UpdateFilterControls();
        List<ResourceEntry> showing = GetShowingEntries();
        HashSet<string> showingPaths = showing
            .Select(static entry => entry.FullPath)
            .ToHashSet(GetPathComparer());
        _selectedPaths.RemoveWhere(path => !showingPaths.Contains(path));
        UpdateSelectionBar();
        UpdateViewChrome(showing);

        if (this.FindControl<StackPanel>("PanList") is not { } list)
            return;

        list.Children.Clear();
        if (_entries.Count == 0)
            return;

        HashSet<string> activePaths = _entries
            .Select(static entry => entry.FullPath)
            .ToHashSet(GetPathComparer());
        foreach (string path in _entryItems.Keys.Where(path => !activePaths.Contains(path)).ToArray())
            _entryItems.Remove(path);

        int next = AppendEntryBatch(list, showing, 0);
        if (next < showing.Count)
            QueueEntryBatch(list, showing, next, render);
    }

    private void WireIconTextButton(string name, Action action)
    {
        if (this.FindControl<MyIconTextButton>(name) is { } button)
            button.Click += (_, _) => action();
    }

    private static bool IsSelectAllGesture(KeyEventArgs args) =>
        args.Key == Key.A &&
        (args.KeyModifiers.HasFlag(KeyModifiers.Control) ||
         args.KeyModifiers.HasFlag(KeyModifiers.Meta));

    private int AppendEntryBatch(
        StackPanel list,
        IReadOnlyList<ResourceEntry> showing,
        int start)
    {
        int end = Math.Min(start + RenderBatchSize, showing.Count);
        for (int index = start; index < end; index++)
        {
            ResourceEntry entry = showing[index];
            if (!_entryItems.TryGetValue(entry.FullPath, out MyLocalModItem? item))
            {
                item = CreateEntryItem(entry);
                _entryItems[entry.FullPath] = item;
            }
            else
            {
                UpdateEntryItem(item, entry);
            }
            SyncItemSelection(item, entry);
            list.Children.Add(item);
        }
        return end;
    }

    private void QueueEntryBatch(
        StackPanel list,
        IReadOnlyList<ResourceEntry> showing,
        int start,
        int render)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (render != _renderVersion)
                return;

            int next = AppendEntryBatch(list, showing, start);
            if (next < showing.Count)
                QueueEntryBatch(list, showing, next, render);
        }, DispatcherPriority.Background);
    }

    private void SyncItemSelection(MyLocalModItem item, ResourceEntry entry)
    {
        _isUpdatingSelection = true;
        try
        {
            item.Checked = _selectedPaths.Contains(entry.FullPath);
        }
        finally
        {
            _isUpdatingSelection = false;
        }
    }

    private List<ResourceEntry> GetShowingEntries()
    {
        List<ResourceEntry> showing = GetFilteredEntries().ToList();
        SortEntries(showing);
        return showing;
    }

    private void UpdateViewChrome(IReadOnlyCollection<ResourceEntry> showing)
    {
        if (this.FindControl<MyCard>("PanListBack") is { } listBack)
        {
            string kind = KindDisplayName(_kind);
            string count = showing.Count.ToString(CultureInfo.CurrentCulture);
            listBack.Title = _isLoading
                ? Text("Instance.Resource.Loading", kind)
                : IsSearching
                ? Text("Instance.Resource.SearchResultTitle", kind, count)
                : Text("Instance.Resource.ListTitleWithCount", kind, count);
        }

        bool isEmpty = _entries.Count == 0;
        if (this.FindControl<Control>("PanEmpty") is { } empty)
            empty.IsVisible = isEmpty && !_isLoading;
        if (this.FindControl<Control>("PanMain") is { } main)
            main.IsVisible = !isEmpty || _isLoading;
    }

    private MyLocalModItem CreateEntryItem(ResourceEntry entry)
    {
        MyLocalModItem item = new()
        {
            Tag = entry,
            CurrentSwipe = _swipeSelection
        };
        UpdateEntryItem(item, entry);
        item.Checked = _selectedPaths.Contains(entry.FullPath);
        item.Changed += (_, _) => EntrySelectionChanged(item, entry);
        item.Click += (_, _) => item.SetChecked(!item.Checked, user: true);
        item.UpdateRequested += (_, _) =>
        {
            _catalogByPath.TryGetValue(entry.FullPath, out LocalCatalogMatch? current);
            _ = ApplyCatalogUpdateAsync(entry, current);
        };

        List<MyIconButton> buttons =
        [
            new()
            {
                SvgIcon = "lucide/info",
                ToolTip = Text("Instance.Resource.Details")
            },
            new()
            {
                SvgIcon = "lucide/folder-open",
                ToolTip = Text("Common.Action.Open")
            }
        ];
        buttons[0].Click += (_, _) => OpenEntryDetails(entry);
        buttons[1].Click += (_, _) => OpenEntryLocation(entry);

        if (_kind == InstanceResourceKind.Mod && !entry.IsDirectory)
        {
            MyIconButton toggle = new()
            {
                SvgIcon = entry.IsDisabled ? "lucide/circle-check" : "lucide/circle-minus",
                ToolTip = entry.IsDisabled ? Text("Instance.Resource.Enable") : Text("Instance.Resource.Disable")
            };
            toggle.Click += (_, _) => ToggleModAsync(entry);
            buttons.Add(toggle);
        }

        MyIconButton delete = new()
        {
            SvgIcon = "lucide/trash-2",
            Theme = MyIconButton.Themes.Red,
            ToolTip = Text("Common.Action.Delete")
        };
        delete.Click += (_, _) => DeleteEntryAsync(entry);
        buttons.Add(delete);

        item.Buttons = buttons;
        return item;
    }

    private void EntrySelectionChanged(MyLocalModItem item, ResourceEntry entry)
    {
        if (_isUpdatingSelection)
            return;

        if (item.Checked)
            _selectedPaths.Add(entry.FullPath);
        else
            _selectedPaths.Remove(entry.FullPath);
        UpdateSelectionBar();
    }

    private void OpenEntryDetails(ResourceEntry entry)
    {
        if (!_catalogByPath.TryGetValue(entry.FullPath, out LocalCatalogMatch? match))
        {
            OpenEntryLocation(entry);
            return;
        }

        OpenProjectRequested?.Invoke(
            this,
            new InstanceResourceProjectRequest(
                match.Project,
                CommunityCategoryForKind(_kind),
                match.SearchOptions));
    }

    private void UpdateEntryItem(MyLocalModItem item, ResourceEntry entry)
    {
        _catalogByPath.TryGetValue(entry.FullPath, out LocalCatalogMatch? match);
        string title = entry.Metadata?.Name is { Length: > 0 } localName
            ? localName
            : GetDisplayName(entry);
        string subtitle = GetLocalVersion(entry);
        string description = GetEntryInfo(entry);
        IList<string> tags = [];
        if (match is not null)
        {
            CommunityResourceEntry project = match.Project;
            title = project.DisplayTitle;
            string originalTitle = project.OriginalTitle ?? project.Title;
            subtitle = string.Equals(title, originalTitle, StringComparison.OrdinalIgnoreCase)
                ? GetLocalVersion(entry)
                : JoinInfo(originalTitle, GetLocalVersion(entry));
            description = GetDisplayName(entry);
            if (!string.IsNullOrWhiteSpace(project.Description))
                description += ": " + NormalizeSingleLine(project.Description);
            tags = project.Tags.ToList();
        }

        item.Title = title;
        item.SubTitle = subtitle;
        item.Description = match?.HasUpdate == true
            ? JoinInfo("有更新可用 " + (match.LatestVersionNumber ?? string.Empty), description)
            : description;
        item.Logo = !string.IsNullOrWhiteSpace(match?.Project.IconUrl ?? match?.Identity.IconUrl)
            ? (match!.Project.IconUrl ?? match.Identity.IconUrl)!
            : GetEntryLogo(entry);
        item.State = IsUnavailable(entry)
            ? ResourceItemState.Unavailable
            : entry.IsDisabled ? ResourceItemState.Disabled : ResourceItemState.Fine;
        item.ShowUpdateButton = match?.HasUpdate == true;
        item.Tags = tags;
    }

    private void StartCatalogScan()
    {
        CancelAndDispose(ref _catalogScanCancellation);
        int scan = Interlocked.Increment(ref _catalogScanVersion);
        if (_kind is InstanceResourceKind.Schematic or InstanceResourceKind.None)
            return;

        ResourceEntry[] files = _entries.Where(static entry => !entry.IsDirectory).ToArray();
        if (files.Length == 0)
            return;

        CancellationTokenSource cancellation = new();
        _catalogScanCancellation = cancellation;
        int context = _contextVersion;
        LaunchInstanceInfo? instance = _instance;
        _ = Task.Run(() => ResolveCatalogMatchesAsync(
            files,
            instance,
            context,
            scan,
            cancellation.Token));
    }

    private async Task ResolveCatalogMatchesAsync(
        IReadOnlyList<ResourceEntry> files,
        LaunchInstanceInfo? instance,
        int context,
        int scan,
        CancellationToken cancellationToken)
    {
        try
        {
            string? gameVersion = TryGetGameVersion(instance);
            string? loaderHint = DetectLoaderHint(instance);
            CommunitySearchOptions options = new(
                CommunityResourceSort.Updated,
                gameVersion,
                loaderHint,
                null);

            using CompositeCommunityResourceCatalog catalog = _catalogFactory();
            using SemaphoreSlim gate = new(3, 3);
            ConcurrentDictionary<string, Lazy<Task<CommunityResourceEntry?>>> projectLookups =
                new(StringComparer.OrdinalIgnoreCase);
            Task<CatalogScanResult?>[] identityTasks = files
                .Select(entry => ResolveAndPublishCatalogIdentityAsync(
                    entry, catalog, gate, projectLookups, options, context, scan, cancellationToken))
                .ToArray();
            CatalogScanResult?[] identityResults = await Task.WhenAll(identityTasks).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            using SemaphoreSlim updateGate = new(3, 3);
            Task[] updateTasks = identityResults
                .OfType<CatalogScanResult>()
                .Select(result => ResolveAndPublishCatalogUpdateAsync(
                    result, catalog, updateGate, options, context, scan, cancellationToken))
                .ToArray();
            await Task.WhenAll(updateTasks).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested ||
                    context != _contextVersion ||
                    scan != _catalogScanVersion)
                {
                    return;
                }

                int updates = _catalogByPath.Values.Count(static match => match.HasUpdate);
                if (_catalogByPath.Count > 0)
                {
                    StatusMessage?.Invoke(
                        this,
                        updates > 0
                            ? $"已识别 {_catalogByPath.Count} 个资源站项目，其中 {updates} 个可更新"
                            : $"已识别 {_catalogByPath.Count} 个资源站项目");
                }
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or
                                   IOException or UnauthorizedAccessException or TimeoutException)
        {
            // Online metadata is optional; local resource management remains available.
        }
    }

    private async Task<CatalogScanResult?> ResolveAndPublishCatalogIdentityAsync(
        ResourceEntry entry,
        CompositeCommunityResourceCatalog catalog,
        SemaphoreSlim gate,
        ConcurrentDictionary<string, Lazy<Task<CommunityResourceEntry?>>> projectLookups,
        CommunitySearchOptions options,
        int context,
        int scan,
        CancellationToken cancellationToken)
    {
        CatalogScanResult? result = await ResolveCatalogMatchAsync(
                entry, catalog, gate, projectLookups, options, cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
            return null;

        await PublishCatalogMatchAsync(result, context, scan, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task ResolveAndPublishCatalogUpdateAsync(
        CatalogScanResult result,
        CompositeCommunityResourceCatalog catalog,
        SemaphoreSlim updateGate,
        CommunitySearchOptions options,
        int context,
        int scan,
        CancellationToken cancellationToken)
    {
        await updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CommunityResourceVersion? latest;
        try
        {
            latest = await TryResolveLatestVersionAsync(
                    catalog,
                    result.Match.Project,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            updateGate.Release();
        }

        CommunityResourceDownloadFile? primary = latest is { Files.Count: > 0 }
            ? latest.Files[0]
            : null;
        bool hasUpdate = primary is not null && IsNewer(
            result.Match.Identity,
            latest!,
            result.LocalSha256,
            result.LocalSha1);
        if (!hasUpdate)
            return;

        CatalogScanResult updated = result with
        {
            Match = result.Match with
            {
                HasUpdate = true,
                LatestVersionNumber = latest!.VersionNumber,
                PrimaryFile = primary
            }
        };
        await PublishCatalogMatchAsync(updated, context, scan, cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishCatalogMatchAsync(
        CatalogScanResult result,
        int context,
        int scan,
        CancellationToken cancellationToken)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested ||
                context != _contextVersion ||
                scan != _catalogScanVersion)
            {
                return;
            }

            _catalogByPath[result.FullPath] = result.Match;
            if (IsSearching)
            {
                StartSearch(debounce: true);
            }
            else
            {
                if (_entryItems.TryGetValue(result.FullPath, out MyLocalModItem? item) &&
                    _entries.FirstOrDefault(entry => GetPathComparer().Equals(entry.FullPath, result.FullPath)) is { } entry)
                {
                    UpdateEntryItem(item, entry);
                }
                ScheduleCatalogUiRefresh();
            }
        });
    }

    private void ScheduleCatalogUiRefresh()
    {
        if (_catalogUiRefreshScheduled)
            return;

        _catalogUiRefreshScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _catalogUiRefreshScheduled = false;
            if (IsSearching)
                return;

            if (_filter is ResourceFilter.CanUpdate or ResourceFilter.Duplicate)
                RefreshUI();
            else
                UpdateFilterControls();
            UpdateSelectionBar();
        }, DispatcherPriority.Background);
    }

    private static async Task<CatalogScanResult?> ResolveCatalogMatchAsync(
        ResourceEntry entry,
        CompositeCommunityResourceCatalog catalog,
        SemaphoreSlim gate,
        ConcurrentDictionary<string, Lazy<Task<CommunityResourceEntry?>>> projectLookups,
        CommunitySearchOptions options,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (string Sha1, uint Fingerprint, string Sha256)? hashes =
                await ComputeFileHashesAsync(entry.FullPath, cancellationToken).ConfigureAwait(false);
            if (hashes is null)
                return null;

            CommunityResourceFileMatches matches = await catalog.LookupFilesAsync(
                    hashes.Value.Sha1,
                    hashes.Value.Fingerprint,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            CommunityResourceFileMatches verifiedMatches = FilterVerifiedMatches(
                matches,
                hashes.Value.Sha256,
                hashes.Value.Sha1);
            CommunityResourceFileIdentity? identity = SelectCurrentIdentity(verifiedMatches);
            if (identity is null)
                return null;

            Task<CommunityResourceEntry?> modrinthProjectTask = ResolveProjectCachedAsync(
                catalog,
                verifiedMatches.Modrinth,
                projectLookups,
                cancellationToken);
            Task<CommunityResourceEntry?> curseForgeProjectTask = ResolveProjectCachedAsync(
                catalog,
                verifiedMatches.CurseForge,
                projectLookups,
                cancellationToken);
            await Task.WhenAll(modrinthProjectTask, curseForgeProjectTask).ConfigureAwait(false);
            CommunityResourceEntry? modrinthProject = await modrinthProjectTask.ConfigureAwait(false);
            CommunityResourceEntry? curseForgeProject = await curseForgeProjectTask.ConfigureAwait(false);
            CommunityResourceEntry project;
            if (modrinthProject is not null && curseForgeProject is not null)
            {
                project = CommunityResourceMerge.MergeKnownProjectPair(
                    modrinthProject,
                    curseForgeProject,
                    McModIndex.Current);
            }
            else
            {
                project = McModIndex.Current.Decorate(
                    modrinthProject ?? curseForgeProject ?? CreateFallbackProject(identity));
            }

            return new CatalogScanResult(
                entry.FullPath,
                new LocalCatalogMatch(
                    identity,
                    project,
                    false,
                    null,
                    null,
                    options),
                hashes.Value.Sha1,
                hashes.Value.Sha256);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or
                                   IOException or UnauthorizedAccessException or TimeoutException)
        {
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<CommunityResourceVersion?> TryResolveLatestVersionAsync(
        CompositeCommunityResourceCatalog catalog,
        CommunityResourceEntry project,
        CommunitySearchOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<CommunityResourceVersion> versions = await catalog.GetVersionsAsync(
                    project,
                    options with { Source = CommunityResourceSource.All },
                    cancellationToken)
                .ConfigureAwait(false);
            return versions.Count == 0 ? null : versions[0];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or
                                   IOException or TimeoutException)
        {
            // The exact file identity remains useful even when optional update lookup fails.
            return null;
        }
    }

    private static async Task<CommunityResourceEntry?> ResolveProjectAsync(
        CompositeCommunityResourceCatalog catalog,
        CommunityResourceSource source,
        string projectId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await catalog.GetProjectAsync(
                    source,
                    projectId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or JsonException or
                                          IOException or TimeoutException)
        {
            return null;
        }
    }

    private static async Task<CommunityResourceEntry?> ResolveProjectCachedAsync(
        CompositeCommunityResourceCatalog catalog,
        CommunityResourceFileIdentity? identity,
        ConcurrentDictionary<string, Lazy<Task<CommunityResourceEntry?>>> projectLookups,
        CancellationToken cancellationToken)
    {
        if (identity is null)
            return null;

        string projectId = identity.ProjectId.Trim();
        string key = ((int)identity.Source).ToString(CultureInfo.InvariantCulture) + "\0" + projectId;
        Lazy<Task<CommunityResourceEntry?>> lookup = projectLookups.GetOrAdd(
            key,
            _ => new Lazy<Task<CommunityResourceEntry?>>(
                () => ResolveProjectAsync(catalog, identity.Source, projectId, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        CommunityResourceEntry? project = await lookup.Value.ConfigureAwait(false);
        return project ?? CreateFallbackProject(identity);
    }

    private static bool IsNewer(
        CommunityResourceFileIdentity current,
        CommunityResourceVersion latest,
        string localSha256,
        string localSha1)
    {
        if (latest.Files.Any(file => FileContentEquals(file, localSha256, localSha1)))
            return false;
        if (latest.Source == current.Source &&
            string.Equals(latest.VersionId, current.VersionId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (latest.PublishedAt is { } latestAt && current.PublishedAt is { } currentAt)
            return latestAt > currentAt;
        return true;
    }

    private static CommunityResourceFileMatches FilterVerifiedMatches(
        CommunityResourceFileMatches matches,
        string localSha256,
        string localSha1) =>
        new(
            IsVerifiedMatch(matches.Modrinth, localSha256, localSha1) ? matches.Modrinth : null,
            IsVerifiedMatch(matches.CurseForge, localSha256, localSha1) ? matches.CurseForge : null);

    private static bool IsVerifiedMatch(
        CommunityResourceFileIdentity? identity,
        string localSha256,
        string localSha1)
    {
        if (identity is null)
            return false;
        if (identity.CurrentFile is not { } currentFile)
            return identity.Source != CommunityResourceSource.CurseForge;
        if (CommunityResourceMerge.NormalizeSha256(currentFile.Sha256) is not null)
            return Sha256Equals(currentFile.Sha256, localSha256);
        if (CommunityResourceMerge.NormalizeSha1(currentFile.Sha1) is not null)
            return Sha1Equals(currentFile.Sha1, localSha1);
        return identity.Source != CommunityResourceSource.CurseForge;
    }

    private static CommunityResourceFileIdentity? SelectCurrentIdentity(CommunityResourceFileMatches matches) =>
        matches.Modrinth ?? matches.CurseForge;

    private static bool FileContentEquals(
        CommunityResourceDownloadFile file,
        string localSha256,
        string localSha1)
    {
        if (CommunityResourceMerge.NormalizeSha256(file.Sha256) is not null)
            return Sha256Equals(file.Sha256, localSha256);
        return CommunityResourceMerge.NormalizeSha1(file.Sha1) is not null &&
               Sha1Equals(file.Sha1, localSha1);
    }

    private static bool Sha256Equals(string? value, string localSha256) =>
        string.Equals(
            CommunityResourceMerge.NormalizeSha256(value),
            localSha256,
            StringComparison.OrdinalIgnoreCase);

    private static bool Sha1Equals(string? value, string localSha1) =>
        string.Equals(
            CommunityResourceMerge.NormalizeSha1(value),
            localSha1,
            StringComparison.OrdinalIgnoreCase);

    private static CommunityResourceEntry CreateFallbackProject(CommunityResourceFileIdentity identity) =>
        new(
            identity.ProjectId,
            identity.ProjectSlug,
            identity.ProjectTitle,
            string.Empty,
            identity.ProjectType,
            identity.IconUrl,
            0L,
            null)
        {
            Source = identity.Source,
            ProjectUrl = identity.WebsiteUrl
        };

    private async Task ApplyCatalogUpdateAsync(
        ResourceEntry entry,
        LocalCatalogMatch? match,
        bool reload = true)
    {
        if (match is not { HasUpdate: true, PrimaryFile: { } file })
        {
            StatusMessage?.Invoke(this, "当前没有可应用的更新。");
            return;
        }

        try
        {
            StatusMessage?.Invoke(this, "正在更新 " + (match.Identity.ProjectTitle ?? GetDisplayName(entry)) + "…");
            string targetName = SanitizeFileName(file.FileName);
            string targetPath = Path.Combine(_folder, targetName);
            string tempPath = targetPath + ".download";
            ICommunityArtifactDownloader downloader = CommunityOnlineProviderRegistry.CreateArtifactDownloader();
            await downloader.DownloadAsync(
                    file.CandidateUrls,
                    tempPath,
                    static (_, _) => { })
                .ConfigureAwait(true);

            // Replace current file (keep disabled suffix if present).
            string finalPath = entry.IsDisabled && !targetPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                ? targetPath + ".disabled"
                : targetPath;

            if (!string.Equals(entry.FullPath, finalPath, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(entry.FullPath))
            {
                File.Delete(entry.FullPath);
            }

            if (File.Exists(finalPath))
                File.Delete(finalPath);
            File.Move(tempPath, finalPath);

            StatusMessage?.Invoke(this, "已更新：" + match.Identity.ProjectTitle);
            if (reload)
                Reload();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
        {
            StatusMessage?.Invoke(this, "更新失败：" + ex.Message);
        }
    }

    internal static string? DetectLoaderHint(LaunchInstanceInfo? instance)
    {
        if (instance is null)
            return null;
        try
        {
            MinecraftVersionJsonInfo info = MinecraftVersionJsonInspector.Read(instance);
            string joined = string.Join(' ', info.LoaderEntries);
            if (joined.Contains("org.quiltmc:quilt-loader:", StringComparison.OrdinalIgnoreCase))
                return "quilt";
            if (joined.Contains("net.neoforged:neoforge:", StringComparison.OrdinalIgnoreCase) ||
                joined.Contains("net.neoforge:forge:", StringComparison.OrdinalIgnoreCase))
            {
                return "neoforge";
            }
            if (joined.Contains("net.minecraftforge:forge:", StringComparison.OrdinalIgnoreCase))
                return "forge";
            if (joined.Contains("net.fabricmc:fabric-loader:", StringComparison.OrdinalIgnoreCase) ||
                joined.Contains("net.legacyfabric:", StringComparison.OrdinalIgnoreCase))
            {
                return "fabric";
            }
            if (joined.Contains("liteloader", StringComparison.OrdinalIgnoreCase))
                return "liteloader";
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string? TryGetGameVersion(LaunchInstanceInfo? instance)
    {
        if (instance is null)
            return null;
        try
        {
            string value = MinecraftVersionJsonInspector.Read(instance).MinecraftVersionId;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static async Task<(string Sha1, uint Fingerprint, string Sha256)?> ComputeFileHashesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] sha1;
            byte[] fingerprint;
            byte[] sha256;
            await using (FileStream stream = OpenReadShared(path))
            {
                sha1 = await SHA1Provider.Instance.ComputeHashAsync(stream, cancellationToken)
                    .ConfigureAwait(false);
            }
            await using (FileStream stream = OpenReadShared(path))
            {
                fingerprint = await MurmurHash2Provider.Instance.ComputeHashAsync(stream, cancellationToken)
                    .ConfigureAwait(false);
            }
            await using (FileStream stream = OpenReadShared(path))
            {
                sha256 = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken)
                    .ConfigureAwait(false);
            }

            return (
                Convert.ToHexStringLower(sha1),
                BinaryPrimitives.ReadUInt32LittleEndian(fingerprint),
                Convert.ToHexStringLower(sha256));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FileStream OpenReadShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static void OpenExternalUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // ignore
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "download.bin" : name;
    }

    private void StartSearch(bool debounce)
    {
        CancelAndDispose(ref _searchCancellation);
        int search = Interlocked.Increment(ref _searchVersion);
        string query = this.FindControl<MySearchBox>("SearchBox")?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            _searchResultPaths = new HashSet<string>(GetPathComparer());
            RefreshUI();
            return;
        }

        CancellationTokenSource cancellation = new();
        _searchCancellation = cancellation;
        int context = _contextVersion;
        ResourceEntry[] entries = _entries.ToArray();
        IReadOnlyDictionary<string, LocalCatalogMatch> catalog =
            new Dictionary<string, LocalCatalogMatch>(_catalogByPath, GetPathComparer());
        _ = SearchAsync(
            query,
            entries,
            catalog,
            context,
            search,
            debounce,
            cancellation.Token);
    }

    private async Task SearchAsync(
        string query,
        IReadOnlyList<ResourceEntry> entries,
        IReadOnlyDictionary<string, LocalCatalogMatch> catalog,
        int context,
        int search,
        bool debounce,
        CancellationToken cancellationToken)
    {
        try
        {
            if (debounce)
                await Task.Delay(350, cancellationToken).ConfigureAwait(false);

            ResourceEntry[] results = await Task.Run(
                    () => SearchEntries(entries, catalog, query),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            HashSet<string> paths = results
                .Select(static entry => entry.FullPath)
                .ToHashSet(GetPathComparer());

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested ||
                    context != _contextVersion ||
                    search != _searchVersion)
                {
                    return;
                }

                _searchResultPaths = paths;
                RefreshUI();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static ResourceEntry[] SearchEntries(
        IReadOnlyList<ResourceEntry> entries,
        IReadOnlyDictionary<string, LocalCatalogMatch> catalog,
        string query)
    {
        List<SearchEntry<ResourceEntry>> candidates = [];
        foreach (ResourceEntry entry in entries)
        {
            List<KeyValuePair<string, double>> sources = [];
            AddSearchSource(sources, GetDisplayName(entry), 1d);
            if (entry.Metadata is { } metadata)
            {
                AddSearchSource(sources, metadata.Name, 1d);
                AddSearchSource(sources, metadata.Id, 1d);
                AddSearchSource(sources, metadata.Version, 0.2d);
            }

            if (catalog.TryGetValue(entry.FullPath, out LocalCatalogMatch? match))
            {
                CommunityResourceEntry project = match.Project;
                AddSearchSource(sources, project.Title, 1d);
                AddSearchSource(sources, project.OriginalTitle, 1d);
                AddSearchSource(sources, project.ChineseName, 1d);
                AddSearchSource(sources, project.Description, 0.4d);
                AddSearchSource(sources, string.Concat(project.Tags), 0.2d);
            }

            candidates.Add(new SearchEntry<ResourceEntry>(entry, sources));
        }

        return SimilaritySearch.Search(candidates, query, 25, 0.35d)
            .Select(static result => result.Item)
            .ToArray();
    }

    private static void AddSearchSource(
        List<KeyValuePair<string, double>> sources,
        string? value,
        double weight)
    {
        if (!string.IsNullOrWhiteSpace(value))
            sources.Add(new KeyValuePair<string, double>(value, weight));
    }

    private IEnumerable<ResourceEntry> GetFilteredEntries()
    {
        HashSet<string>? duplicatePaths = _filter == ResourceFilter.Duplicate
            ? GetDuplicatePaths(GetFilterSource())
            : null;
        foreach (ResourceEntry entry in _entries)
        {
            if (IsSearching && !_searchResultPaths.Contains(entry.FullPath))
                continue;

            if (_kind == InstanceResourceKind.Mod)
            {
                if (_filter == ResourceFilter.Enabled && entry.IsDisabled)
                    continue;
                if (_filter == ResourceFilter.Disabled && !entry.IsDisabled)
                    continue;
                if (_filter == ResourceFilter.CanUpdate &&
                    (!_catalogByPath.TryGetValue(entry.FullPath, out LocalCatalogMatch? match) || !match.HasUpdate))
                {
                    continue;
                }
                if (_filter == ResourceFilter.Unavailable && !IsUnavailable(entry))
                    continue;
                if (_filter == ResourceFilter.Duplicate && duplicatePaths?.Contains(entry.FullPath) != true)
                    continue;
            }

            yield return entry;
        }
    }

    private void UpdateFilterControls()
    {
        if (_kind != InstanceResourceKind.Mod)
            return;

        ResourceEntry[] source = GetFilterSource().ToArray();
        int all = source.Length;
        int enabled = source.Count(entry => !entry.IsDisabled && !IsUnavailable(entry));
        int disabled = source.Count(static entry => entry.IsDisabled);
        int updatable = source.Count(entry =>
            _catalogByPath.TryGetValue(entry.FullPath, out LocalCatalogMatch? match) && match.HasUpdate);
        int unavailable = source.Count(IsUnavailable);
        int duplicate = GetDuplicatePaths(source).Count;

        if (!_isLoading && disabled == 0 && _filter is ResourceFilter.Enabled or ResourceFilter.Disabled)
        {
            _filter = ResourceFilter.All;
            _isUpdatingFilter = true;
            try
            {
                this.FindControl<MyRadioButton>("BtnFilterAll")?.SetChecked(true, false, false);
            }
            finally
            {
                _isUpdatingFilter = false;
            }
        }

        SetFilterState("BtnFilterAll", IsSearching
            ? Text("Instance.Resource.Filter.SearchResultWithCount", all.ToString(CultureInfo.CurrentCulture))
            : Text("Instance.Resource.Filter.AllWithCount", all.ToString(CultureInfo.CurrentCulture)), true);
        SetFilterState("BtnFilterEnabled", Text("Instance.Resource.Filter.EnabledWithCount", enabled.ToString(CultureInfo.CurrentCulture)),
            disabled > 0 && (_filter == ResourceFilter.Enabled || enabled > 0));
        SetFilterState("BtnFilterDisabled", Text("Instance.Resource.Filter.DisabledWithCount", disabled.ToString(CultureInfo.CurrentCulture)),
            disabled > 0);
        SetFilterState("BtnFilterCanUpdate", Text("Instance.Resource.Filter.UpdatableWithCount", updatable.ToString(CultureInfo.CurrentCulture)),
            _filter == ResourceFilter.CanUpdate || updatable > 0);
        SetFilterState("BtnFilterError", Text("Instance.Resource.Filter.ErrorWithCount", unavailable.ToString(CultureInfo.CurrentCulture)),
            _filter == ResourceFilter.Unavailable || unavailable > 0);
        SetFilterState("BtnFilterDuplicate", Text("Instance.Resource.Filter.DuplicateWithCount", duplicate.ToString(CultureInfo.CurrentCulture)),
            _filter == ResourceFilter.Duplicate || duplicate > 0);
    }

    private void ChangeAllSelected(bool value)
    {
        List<ResourceEntry> showing = GetShowingEntries();
        _isUpdatingSelection = true;
        try
        {
            _selectedPaths.Clear();
            if (value)
            {
                foreach (ResourceEntry entry in showing)
                    _selectedPaths.Add(entry.FullPath);
            }

            foreach ((string path, MyLocalModItem item) in _entryItems)
                item.Checked = value && _selectedPaths.Contains(path);
        }
        finally
        {
            _isUpdatingSelection = false;
        }

        UpdateSelectionBar();
    }

    private void ToggleAllSelected()
    {
        int showingCount = GetShowingEntries().Count;
        ChangeAllSelected(_selectedPaths.Count < showingCount);
    }

    private void UpdateSelectionBar()
    {
        int selectedCount = _selectedPaths.Count;
        bool selected = selectedCount > 0;
        if (this.FindControl<TextBlock>("LabSelect") is { } label && selected)
        {
            label.Text = Text(
                "Instance.Resource.SelectedCount",
                selectedCount.ToString(CultureInfo.CurrentCulture));
        }

        ResourceEntry[] entries = _entries
            .Where(entry => _selectedPaths.Contains(entry.FullPath))
            .ToArray();
        if (this.FindControl<MyIconTextButton>("BtnSelectEnable") is { } enable)
            enable.IsEnabled = entries.Any(static entry => entry.IsDisabled);
        if (this.FindControl<MyIconTextButton>("BtnSelectDisable") is { } disable)
            disable.IsEnabled = entries.Any(static entry => !entry.IsDisabled);
        if (this.FindControl<MyIconTextButton>("BtnSelectUpdate") is { } update)
        {
            update.IsEnabled = entries.Any(entry =>
                _catalogByPath.TryGetValue(entry.FullPath, out LocalCatalogMatch? match) && match.HasUpdate);
        }

        if (this.FindControl<MyCard>("CardSelect") is { } card &&
            this.FindControl<MyCard>("PanListBack") is { } listBack)
        {
            ListSelectionMotion.AnimateActionBar(
                this,
                card,
                listBack,
                selected,
                visibleBottomMargin: 95d,
                hiddenBottomMargin: 14d,
                animationKey: "PageInstanceResource SelectionBar");
        }
    }

    private IEnumerable<ResourceEntry> GetFilterSource() =>
        IsSearching
            ? _entries.Where(entry => _searchResultPaths.Contains(entry.FullPath))
            : _entries;

    private void SetFilterState(string name, string text, bool visible)
    {
        if (this.FindControl<MyRadioButton>(name) is not { } button)
            return;
        button.Text = text;
        button.IsVisible = visible;
    }

    private HashSet<string> GetDuplicatePaths(IEnumerable<ResourceEntry> source)
    {
        return source
            .Select(entry => (Entry: entry, Key: GetProjectIdentity(entry)))
            .Where(static item => item.Key is not null)
            .GroupBy(static item => item.Key!, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Skip(1).Any())
            .SelectMany(static group => group.Select(static item => item.Entry.FullPath))
            .ToHashSet(GetPathComparer());
    }

    private string? GetProjectIdentity(ResourceEntry entry)
    {
        if (_catalogByPath.TryGetValue(entry.FullPath, out LocalCatalogMatch? match))
        {
            if (match.Project.WikiId is > 0)
                return "wiki:" + match.Project.WikiId.Value.ToString(CultureInfo.InvariantCulture);
            if (match.Project.ModrinthProject is { } modrinth)
                return "modrinth:" + modrinth.ProjectId;
            if (match.Project.CurseForgeProject is { } curseForge)
                return "curseforge:" + curseForge.ProjectId;
            return match.Project.Source + ":" + match.Project.ProjectId;
        }

        return entry.Metadata?.Id is { Length: > 0 } id ? "local:" + id : null;
    }

    private bool IsUnavailable(ResourceEntry entry) =>
        _kind == InstanceResourceKind.Mod && !entry.IsDirectory && entry.Metadata is null;

    private void SortEntries(List<ResourceEntry> entries)
    {
        Comparison<ResourceEntry> comparison = _sort switch
        {
            ResourceSort.AddTime => (a, b) => b.CreationTime.CompareTo(a.CreationTime),
            ResourceSort.ModifyTime => (a, b) => b.ModifyTime.CompareTo(a.ModifyTime),
            ResourceSort.FileSize => (a, b) => b.Length.CompareTo(a.Length),
            _ => (a, b) => string.Compare(GetDisplayName(a), GetDisplayName(b), StringComparison.OrdinalIgnoreCase)
        };
        entries.Sort(comparison);
        if (this.FindControl<MyIconTextButton>("BtnSort") is { } sort)
            sort.Text = Text("Instance.Resource.Sort.Text", SortDisplayName(_sort));
    }

    private void CycleSort()
    {
        _sort = _sort switch
        {
            ResourceSort.FileName => ResourceSort.ModifyTime,
            ResourceSort.ModifyTime => ResourceSort.AddTime,
            ResourceSort.AddTime => ResourceSort.FileSize,
            _ => ResourceSort.FileName
        };
        RefreshUI();
    }

    private async Task SetSelectedEnabledAsync(bool enable)
    {
        ResourceEntry[] selected = _entries
            .Where(entry =>
                _selectedPaths.Contains(entry.FullPath) &&
                !entry.IsDirectory &&
                entry.IsDisabled == enable)
            .ToArray();
        if (selected.Length == 0)
            return;

        (int changed, int failed) = await Task.Run(() =>
        {
            int changedCount = 0;
            int failedCount = 0;
            foreach (ResourceEntry entry in selected)
            {
                try
                {
                    string target = enable
                        ? entry.FullPath[..^".disabled".Length]
                        : entry.FullPath + ".disabled";
                    if (File.Exists(target) || Directory.Exists(target))
                    {
                        failedCount++;
                        continue;
                    }

                    File.Move(entry.FullPath, target);
                    changedCount++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failedCount++;
                }
            }
            return (changedCount, failedCount);
        }).ConfigureAwait(true);

        if (changed > 0)
        {
            StatusMessage?.Invoke(
                this,
                Text(enable ? "Instance.Resource.Enabled" : "Instance.Resource.Disabled") +
                "：" + changed.ToString(CultureInfo.CurrentCulture));
        }
        if (failed > 0)
            StatusMessage?.Invoke(this, Text("Instance.Resource.ToggleFailed"));

        _selectedPaths.Clear();
        UpdateSelectionBar();
        Reload();
    }

    private async Task UpdateSelectedAsync()
    {
        (ResourceEntry Entry, LocalCatalogMatch Match)[] selected = _entries
            .Where(entry => _selectedPaths.Contains(entry.FullPath))
            .Select(entry =>
            {
                _catalogByPath.TryGetValue(entry.FullPath, out LocalCatalogMatch? match);
                return (Entry: entry, Match: match);
            })
            .Where(static item => item.Match?.HasUpdate == true)
            .Select(static item => (item.Entry, item.Match!))
            .ToArray();
        if (selected.Length == 0)
            return;

        foreach ((ResourceEntry entry, LocalCatalogMatch match) in selected)
            await ApplyCatalogUpdateAsync(entry, match, reload: false).ConfigureAwait(true);

        _selectedPaths.Clear();
        UpdateSelectionBar();
        Reload();
    }

    private async Task DeleteSelectedAsync()
    {
        ResourceEntry[] selected = _entries
            .Where(entry => _selectedPaths.Contains(entry.FullPath))
            .ToArray();
        if (selected.Length == 0)
            return;

        (int deleted, int failed) = await Task.Run(() =>
        {
            int deletedCount = 0;
            int failedCount = 0;
            foreach (ResourceEntry entry in selected)
            {
                try
                {
                    if (entry.IsDirectory)
                        Directory.Delete(entry.FullPath, recursive: true);
                    else if (File.Exists(entry.FullPath))
                        File.Delete(entry.FullPath);
                    deletedCount++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failedCount++;
                }
            }
            return (deletedCount, failedCount);
        }).ConfigureAwait(true);

        if (deleted > 0)
        {
            StatusMessage?.Invoke(
                this,
                Text("Instance.Resource.Deleted") + "：" + deleted.ToString(CultureInfo.CurrentCulture));
        }
        if (failed > 0)
            StatusMessage?.Invoke(this, Text("Instance.Resource.DeleteFailed"));

        _selectedPaths.Clear();
        UpdateSelectionBar();
        Reload();
    }

    private void OpenCurrentFolder()
    {
        if (string.IsNullOrWhiteSpace(_folder))
            return;

        Directory.CreateDirectory(_folder);
        OpenFolderRequested?.Invoke(this, _folder);
    }

    private void RequestDownload() => DownloadRequested?.Invoke(this, _page);

    private async Task InstallFromFilesAsync()
    {
        try
        {
            IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
                throw new InvalidOperationException("Storage provider is unavailable.");

            IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Text("Instance.Resource.InstallFromFiles"),
                AllowMultiple = true
            }).ConfigureAwait(true);

            int copied = 0;
            foreach (IStorageFile file in files)
            {
                string? source = file.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(source) || !File.Exists(source) || !IsAcceptedPath(source))
                    continue;

                string target = Path.Combine(_folder, Path.GetFileName(source));
                if (File.Exists(target))
                    continue;

                File.Copy(source, target);
                copied++;
            }

            if (copied > 0)
            {
                StatusMessage?.Invoke(this, Text("Instance.Resource.Install.Success", copied.ToString(CultureInfo.CurrentCulture)));
                Reload();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusMessage?.Invoke(this, Text("Instance.Resource.Install.Failed"));
        }
    }

    private async void ToggleModAsync(ResourceEntry entry)
    {
        try
        {
            await Task.Run(() =>
            {
                string target = entry.IsDisabled
                    ? entry.FullPath[..^".disabled".Length]
                    : entry.FullPath + ".disabled";
                if (File.Exists(target) || Directory.Exists(target))
                    throw new IOException("Target exists.");
                File.Move(entry.FullPath, target);
            }).ConfigureAwait(true);
            StatusMessage?.Invoke(this, entry.IsDisabled ? Text("Instance.Resource.Enabled") : Text("Instance.Resource.Disabled"));
            Reload();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage?.Invoke(this, Text("Instance.Resource.ToggleFailed"));
        }
    }

    private async void DeleteEntryAsync(ResourceEntry entry)
    {
        try
        {
            await Task.Run(() =>
            {
                if (entry.IsDirectory)
                    Directory.Delete(entry.FullPath, recursive: true);
                else if (File.Exists(entry.FullPath))
                    File.Delete(entry.FullPath);
            }).ConfigureAwait(true);
            StatusMessage?.Invoke(this, Text("Instance.Resource.Deleted"));
            Reload();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage?.Invoke(this, Text("Instance.Resource.DeleteFailed"));
        }
    }

    private void OpenEntryLocation(ResourceEntry entry)
    {
        string path = entry.IsDirectory
            ? entry.FullPath
            : Path.GetDirectoryName(entry.FullPath) ?? _folder;
        OpenFolderRequested?.Invoke(this, path);
    }

    private bool IsSearching => !string.IsNullOrWhiteSpace(this.FindControl<MySearchBox>("SearchBox")?.Text);

    private bool IsAcceptedPath(string path) => IsAcceptedPath(path, _kind);

    private static bool IsAcceptedPath(string path, InstanceResourceKind kind)
    {
        if (Directory.Exists(path))
            return kind is InstanceResourceKind.ResourcePack or InstanceResourceKind.ShaderPack or InstanceResourceKind.DataPack;

        string fileName = Path.GetFileName(path);
        string extension = Path.GetExtension(path);
        return kind switch
        {
            InstanceResourceKind.Mod => fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                                fileName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase),
            InstanceResourceKind.ResourcePack or InstanceResourceKind.ShaderPack or InstanceResourceKind.DataPack =>
                extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                // Iris / OptiFine may also drop loose folders; folders already accepted above.
                extension.Equals(".jar", StringComparison.OrdinalIgnoreCase),
            InstanceResourceKind.Schematic => extension.Equals(".schematic", StringComparison.OrdinalIgnoreCase) ||
                                      extension.Equals(".schem", StringComparison.OrdinalIgnoreCase) ||
                                      extension.Equals(".litematic", StringComparison.OrdinalIgnoreCase) ||
                                      extension.Equals(".nbt", StringComparison.OrdinalIgnoreCase) ||
                                      extension.Equals(".bp", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private ResourceEntry CreateResourceEntry(string path, InstanceResourceKind kind)
    {
        bool isDirectory = Directory.Exists(path);
        MinecraftModMetadata? metadata = null;
        if (kind == InstanceResourceKind.Mod && !isDirectory)
            metadata = _metadataReader(path);
        string? localLogo;
        if (kind == InstanceResourceKind.Mod)
        {
            localLogo = MinecraftArchiveIconExtractor.TryExtract(path, metadata?.IconEntryPath);
            localLogo ??= MinecraftArchiveIconExtractor.TryExtract(path, "pack.png");
        }
        else
        {
            string? iconEntryPath = kind is InstanceResourceKind.ResourcePack or
                InstanceResourceKind.ShaderPack or InstanceResourceKind.DataPack
                ? "pack.png"
                : null;
            localLogo = MinecraftArchiveIconExtractor.TryExtract(path, iconEntryPath);
        }

        return new ResourceEntry(
            path,
            isDirectory,
            IsDisabledPath(path),
            GetLength(path),
            File.GetCreationTime(path),
            File.GetLastWriteTime(path),
            metadata,
            localLogo);
    }

    private static MinecraftModMetadata? ReadModMetadata(string path) =>
        MinecraftModMetadataReader.TryRead(path, out MinecraftModMetadata? metadata)
            ? metadata
            : null;

    private static long GetLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0L;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0L;
        }
    }

    private static bool IsDisabledPath(string path) =>
        path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);

    private static string GetDisplayName(ResourceEntry entry)
    {
        string name = Path.GetFileName(entry.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return entry.IsDisabled && name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? name[..^".disabled".Length]
            : name;
    }

    private static string GetLocalVersion(ResourceEntry entry) =>
        entry.Metadata?.Version is { Length: > 0 } version &&
        !string.Equals(version, "unknown", StringComparison.OrdinalIgnoreCase)
            ? version
            : string.Empty;

    private static string JoinInfo(params string?[] values) =>
        string.Join(" · ", values.Where(static value => !string.IsNullOrWhiteSpace(value)));

    private static string NormalizeSingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private string GetEntryInfo(ResourceEntry entry)
    {
        string state = _kind == InstanceResourceKind.Mod
            ? entry.IsDisabled ? Text("Instance.Resource.State.Disabled") : Text("Instance.Resource.State.Enabled")
            : entry.IsDirectory ? Text("Instance.Resource.State.Folder") : Text("Instance.Resource.State.File");
        return Text(
            "Instance.Resource.Item.Info",
            state,
            FormatSize(entry.Length),
            entry.ModifyTime.ToString("d", CultureInfo.CurrentCulture));
    }

    private string GetEntryLogo(ResourceEntry entry) =>
        entry.LocalLogo ?? (_kind switch
        {
            InstanceResourceKind.Mod => entry.IsDisabled ? InstanceDisplayHelper.BlockAssetRoot + "RedstoneBlock.png" : InstanceDisplayHelper.BlockAssetRoot + "CommandBlock.png",
            InstanceResourceKind.ResourcePack => InstanceDisplayHelper.BlockAssetRoot + "Grass.png",
            InstanceResourceKind.ShaderPack => InstanceDisplayHelper.BlockAssetRoot + "GoldBlock.png",
            InstanceResourceKind.Schematic => InstanceDisplayHelper.BlockAssetRoot + "StructureBlock.png",
            InstanceResourceKind.DataPack => InstanceDisplayHelper.BlockAssetRoot + "CommandBlock.png",
            _ => InstanceDisplayHelper.DefaultLogo
        });

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024d && unit < units.Length - 1)
        {
            value /= 1024d;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[unit]}"
            : string.Format(CultureInfo.CurrentCulture, "{0:0.##} {1}", value, units[unit]);
    }

    private static string KindDisplayName(InstanceResourceKind kind) =>
        kind switch
        {
            InstanceResourceKind.ResourcePack => "资源包",
            InstanceResourceKind.ShaderPack => "光影",
            InstanceResourceKind.Schematic => "投影",
            InstanceResourceKind.DataPack => "数据包",
            _ => "Mod"
        };

    private static CommunityResourceCategory CommunityCategoryForKind(InstanceResourceKind kind) =>
        kind switch
        {
            InstanceResourceKind.ResourcePack => CommunityResourceCategory.ResourcePack,
            InstanceResourceKind.ShaderPack => CommunityResourceCategory.Shader,
            InstanceResourceKind.Schematic => CommunityResourceCategory.World,
            InstanceResourceKind.DataPack => CommunityResourceCategory.DataPack,
            _ => CommunityResourceCategory.Mod
        };

    private string SortDisplayName(ResourceSort sort) =>
        sort switch
        {
            ResourceSort.AddTime => Text("Instance.Resource.Sort.AddTime"),
            ResourceSort.ModifyTime => Text("Instance.Resource.Sort.ModifyTime"),
            ResourceSort.FileSize => Text("Instance.Resource.Sort.FileSize"),
            _ => Text("Instance.Resource.Sort.FileName")
        };

    private string Text(string key, params string[] args)
    {
        string? value = null;
        // Prefer app/theme resource dictionaries (PclTheme + localization).
        if (Avalonia.Application.Current?.TryGetResource(key, ActualThemeVariant, out object? appRes) == true &&
            appRes is string appText)
        {
            value = appText;
        }
        else if (TryGetResource(key, ActualThemeVariant, out object? localRes) && localRes is string localText)
        {
            value = localText;
        }

        value ??= BuiltInResourceText(key) ?? key;
        return args.Length == 0
            ? value
            : string.Format(CultureInfo.CurrentCulture, value, args);
    }

    private static string? BuiltInResourceText(string key) =>
        key switch
        {
            "Instance.Resource.ListTitle" => "{0} 列表",
            "Instance.Resource.ListTitleWithCount" => "{0} 列表 ({1})",
            "Instance.Resource.Loading" => "正在加载 {0}…",
            "Instance.Resource.SearchResultTitle" => "{0} 搜索结果 ({1})",
            "Instance.Resource.Sort.Text" => "排序：{0}",
            "Instance.Resource.Sort.FileName" => "文件名",
            "Instance.Resource.Sort.ModifyTime" => "修改时间",
            "Instance.Resource.Sort.AddTime" => "添加时间",
            "Instance.Resource.Sort.FileSize" => "文件大小",
            "Instance.Resource.Empty.Title" => "还没有 {0}",
            "Instance.Resource.Empty.Description" => "这个版本还没有 {0}。你可以下载新的内容，或从本地文件安装。",
            "Instance.Resource.Item.Info" => "{0} · {1} · 修改于 {2}",
            "Instance.Resource.State.Enabled" => "已启用",
            "Instance.Resource.State.Disabled" => "已禁用",
            "Instance.Resource.State.File" => "文件",
            "Instance.Resource.State.Folder" => "文件夹",
            "Common.Action.Open" => "打开",
            "Common.Action.Delete" => "删除",
            "Instance.Resource.Enable" => "启用",
            "Instance.Resource.Disable" => "禁用",
            _ => null
        };

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        CancellationTokenSource? previous = Interlocked.Exchange(ref source, null);
        previous?.Cancel();
        previous?.Dispose();
    }

    private void CancelPendingWork()
    {
        CancelAndDispose(ref _reloadCancellation);
        CancelAndDispose(ref _catalogScanCancellation);
        CancelAndDispose(ref _searchCancellation);
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private enum ResourceFilter
    {
        All = 0,
        Enabled = 1,
        Disabled = 2,
        CanUpdate = 3,
        Unavailable = 4,
        Duplicate = 5
    }

    private enum ResourceSort
    {
        FileName,
        ModifyTime,
        AddTime,
        FileSize
    }

    private sealed record ResourceEntry(
        string FullPath,
        bool IsDirectory,
        bool IsDisabled,
        long Length,
        DateTime CreationTime,
        DateTime ModifyTime,
        MinecraftModMetadata? Metadata,
        string? LocalLogo);

    private sealed record CatalogScanResult(
        string FullPath,
        LocalCatalogMatch Match,
        string LocalSha1,
        string LocalSha256);

    private sealed record LocalCatalogMatch(
        CommunityResourceFileIdentity Identity,
        CommunityResourceEntry Project,
        bool HasUpdate,
        string? LatestVersionNumber,
        CommunityResourceDownloadFile? PrimaryFile,
        CommunitySearchOptions SearchOptions);
}
