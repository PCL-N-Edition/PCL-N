// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PCL.Application.Instances;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Features.Launching.Views;

namespace PCL.Desktop.Features.Instances.Views;

public partial class PageInstanceSelectRight : MyPageRight, IDisposable
{
    private const int SearchNormalDelayMs = 75;
    private const int SearchQuickDelayMs = 50;
    private readonly DispatcherTimer _reloadTimer;
    private readonly object _metadataLock = new();
    private CancellationTokenSource? _metadataLoadCancellation;
    private Dictionary<string, InstanceMetadata> _metadataCache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<LaunchInstanceInfo> _instances = [];
    private IReadOnlyList<MinecraftFolderInfo> _folders = [];
    private string? _selectedRootDirectory;
    private LaunchInstanceInfo? _selectedInstance;
    private Task _metadataLoadTask = Task.CompletedTask;
    private DateTime _lastInputTime = DateTime.MinValue;
    private bool _isRefreshing;
    private bool _isLoading;
    private bool _showHidden;

    public PageInstanceSelectRight()
    {
        AvaloniaXamlLoader.Load(this);
        PanScroll = this.FindControl<MyScrollViewer>("PanBack");
        _reloadTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(SearchNormalDelayMs)
        };
        _reloadTimer.Tick += ReloadTimer_Tick;
        if (this.FindControl<MySearchBox>("PanVerSearchBox") is { } searchBox)
            searchBox.TextChanged += PanVerSearchBox_TextChanged;
        SetLoadingState(false);
        ReloadFolders();
    }

    public bool ShowHidden
    {
        get => _showHidden;
        set
        {
            if (_showHidden == value)
                return;

            _showHidden = value;
            ReloadList();
        }
    }

    public event EventHandler? RefreshRequested;

    public event EventHandler? DownloadRequested;

    public event EventHandler<LaunchInstanceInfo>? InstanceSelected;

    public event EventHandler<LaunchInstanceInfo>? InstanceManageRequested;

    public event EventHandler<LaunchInstanceInfo>? InstanceOpenFolderRequested;

    public event EventHandler<LaunchInstanceInfo>? InstanceDeleteRequested;

    public event EventHandler<MinecraftFolderInfo>? FolderSelected;

    public event EventHandler<MinecraftFolderInfo>? FolderOpenRequested;

    public event EventHandler<MinecraftFolderInfo>? FolderRefreshRequested;

    public event EventHandler<MinecraftFolderInfo>? FolderRenameRequested;

    public event EventHandler<MinecraftFolderInfo>? FolderRemoveRequested;

    public event EventHandler? CreateFolderRequested;

    public event EventHandler? AddFolderRequested;

    public event EventHandler? ImportModpackRequested;

    public void SetFolders(IReadOnlyList<MinecraftFolderInfo> folders, string? selectedRootDirectory)
    {
        _folders = folders ?? [];
        _selectedRootDirectory = NormalizeFolderPath(selectedRootDirectory);
        if (_selectedRootDirectory is null && _folders.Count > 0)
            _selectedRootDirectory = NormalizeFolderPath(_folders[0].RootDirectory);
        ReloadFolders();
        UpdateLibrarySubtitle();
    }

    public bool TrySelectInstance(LaunchInstanceInfo instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!_instances.Contains(instance))
            return false;

        if (!InstanceDisplayHelper.IsValid(instance))
        {
            InstanceOpenFolderRequested?.Invoke(this, instance);
            return false;
        }

        InstanceSelected?.Invoke(this, instance);
        return true;
    }

    public void SetLoadingState(bool isLoading = true)
    {
        _isLoading = isLoading;
        SetVisible("PanLoad", isLoading);
        SetVisible("PanAllBack", !isLoading);
        SetLoadingAnimationState(isLoading);
        if (!isLoading)
            ReloadList();
    }

    public void SetInstances(IReadOnlyList<LaunchInstanceInfo> instances, LaunchInstanceInfo? selectedInstance)
    {
        _instances = instances;
        _selectedInstance = selectedInstance;
        StartMetadataLoad(instances);
        SetLoadingState(false);
        UpdateLibrarySubtitle();
    }

    private void UpdateLibrarySubtitle()
    {
        if (this.FindControl<TextBlock>("LabLibrarySubtitle") is not { } subtitle)
            return;

        MinecraftFolderInfo? selected = _folders.FirstOrDefault(folder =>
            string.Equals(
                NormalizeFolderPath(folder.RootDirectory),
                _selectedRootDirectory,
                StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            subtitle.Text = ResourceText("Select.Instance.Hero.Subtitle", "从左侧选择游戏文件夹，然后点选要启动的版本。");
            return;
        }

        int count = _instances.Count;
        subtitle.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            ResourceText("Select.Instance.Hero.Subtitle.Folder", "{0} · {1} 个版本"),
            selected.Name,
            count);
    }

    private void ReloadFolders()
    {
        if (this.FindControl<StackPanel>("PanFolders") is not { } panel)
            return;

        panel.Children.Clear();
        panel.Children.Add(CreateSidebarSection(
            ResourceText("Select.Instance.Sidebar.Section.Folders", "游戏文件夹")));
        foreach (MinecraftFolderInfo folder in _folders)
            panel.Children.Add(CreateFolderRow(folder));

        panel.Children.Add(CreateSidebarSection(
            ResourceText("Select.Instance.Sidebar.Section.Actions", "添加或导入")));
        string defaultRoot = Path.Combine(AppContext.BaseDirectory, ".minecraft");
        if (!Directory.Exists(defaultRoot))
        {
            panel.Children.Add(CreateSidebarAction(
                ResourceText("Select.Instance.Sidebar.CreateFolder", "新建游戏文件夹"),
                ResourceText("Select.Instance.Sidebar.CreateFolder.Tip", "在启动器目录创建 .minecraft"),
                "lucide/folder-plus",
                () => CreateFolderRequested?.Invoke(this, EventArgs.Empty)));
        }

        panel.Children.Add(CreateSidebarAction(
            ResourceText("Select.Instance.Sidebar.AddFolder", "添加已有文件夹"),
            ResourceText("Select.Instance.Sidebar.AddFolder.Tip", "把已有的 Minecraft 文件夹加入列表"),
            "lucide/folder-input",
            () => AddFolderRequested?.Invoke(this, EventArgs.Empty)));
        panel.Children.Add(CreateSidebarAction(
            ResourceText("Select.Instance.Sidebar.ImportModpack", "导入整合包"),
            ResourceText("Select.Instance.Sidebar.ImportModpack.Tip", "从本地压缩包导入游戏实例"),
            "lucide/package-plus",
            () => ImportModpackRequested?.Invoke(this, EventArgs.Empty)));
    }

    private Border CreateFolderRow(MinecraftFolderInfo folder)
    {
        bool selected = string.Equals(
            NormalizeFolderPath(folder.RootDirectory),
            _selectedRootDirectory,
            StringComparison.OrdinalIgnoreCase);

        Border row = new()
        {
            Margin = new Thickness(0, 2),
            Padding = new Thickness(10, 9),
            CornerRadius = new CornerRadius(10),
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = selected
                ? ResolveBrush("ColorBrush7") ?? Brushes.Transparent
                : Brushes.Transparent,
            Tag = folder
        };

        Grid grid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        StackPanel text = new()
        {
            Spacing = 2,
            Children =
            {
                new TextBlock
                {
                    Text = folder.Name,
                    FontSize = 13.5,
                    FontWeight = selected ? FontWeight.SemiBold : FontWeight.Medium,
                    Foreground = ResolveBrush("ColorBrush1") ?? Brushes.Black,
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                new TextBlock
                {
                    Text = folder.RootDirectory,
                    FontSize = 11,
                    Opacity = 0.62,
                    Foreground = ResolveBrush("ColorBrushGray2") ?? Brushes.Gray,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        };
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        StackPanel actions = new()
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 0,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children =
            {
                CreateTinyIcon("lucide/folder-open", "打开文件夹", () => FolderOpenRequested?.Invoke(this, folder)),
                CreateTinyIcon("lucide/refresh-cw", "刷新", () => FolderRefreshRequested?.Invoke(this, folder))
            }
        };
        if (folder.IsCustom)
        {
            actions.Children.Add(CreateTinyIcon("lucide/pencil", "重命名", () => FolderRenameRequested?.Invoke(this, folder)));
            actions.Children.Add(CreateTinyIcon("lucide/list-x", "从列表移除", () => FolderRemoveRequested?.Invoke(this, folder)));
        }

        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        row.Child = grid;
        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
                return;
            e.Handled = true;
            _selectedRootDirectory = NormalizeFolderPath(folder.RootDirectory);
            ReloadFolders();
            FolderSelected?.Invoke(this, folder);
        };
        return row;
    }

    private static MyIconButton CreateTinyIcon(string icon, string tip, Action click)
    {
        MyIconButton button = new()
        {
            Width = 24,
            Height = 24,
            SvgIcon = icon,
            LogoScale = 0.9d,
            ToolTip = tip
        };
        button.Click += (_, _) => click();
        return button;
    }

    private static TextBlock CreateSidebarSection(string text) =>
        new()
        {
            Text = text,
            Margin = new Thickness(8, 14, 8, 6),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            LetterSpacing = 0.35,
            Opacity = 0.55
        };

    private static MyListItem CreateSidebarAction(string title, string tip, string icon, Action click)
    {
        MyListItem item = new()
        {
            Title = title,
            Height = 36d,
            Type = MyListItem.CheckType.Clickable,
            IsScaleAnimationEnabled = false,
            SvgIcon = icon,
            LogoScale = 0.92d,
            Margin = new Thickness(0, 1)
        };
        ToolTip.SetTip(item, tip);
        item.Click += (_, _) => click();
        return item;
    }

    private static string? NormalizeFolderPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            return Path.GetFullPath(path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public async Task ReloadMetadataAsync(CancellationToken cancellationToken = default)
    {
        await CancelMetadataLoadAsync().ConfigureAwait(false);

        Dictionary<string, InstanceMetadata> loaded =
            await ReadMetadataAsync(_instances.ToArray(), cancellationToken).ConfigureAwait(false);
        ApplyMetadataSnapshot(loaded);

        if (Dispatcher.UIThread.CheckAccess())
            ReloadList();
        else
            await Dispatcher.UIThread.InvokeAsync(ReloadList, DispatcherPriority.Background, cancellationToken);
    }

    public override void Dispose()
    {
        _metadataLoadCancellation?.Cancel();
        _metadataLoadCancellation?.Dispose();
        _metadataLoadCancellation = null;
        _reloadTimer.Stop();
        _reloadTimer.Tick -= ReloadTimer_Tick;
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task CancelMetadataLoadAsync()
    {
        CancellationTokenSource? cancellation = _metadataLoadCancellation;
        if (cancellation is null)
            return;

        cancellation.Cancel();
        try
        {
            await _metadataLoadTask.ConfigureAwait(false);
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(_metadataLoadCancellation, cancellation))
                _metadataLoadCancellation = null;
            _metadataLoadTask = Task.CompletedTask;
        }
    }

    private void StartMetadataLoad(IReadOnlyList<LaunchInstanceInfo> instances)
    {
        _metadataLoadCancellation?.Cancel();
        _metadataLoadCancellation?.Dispose();
        _metadataLoadCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _metadataLoadCancellation.Token;
        LaunchInstanceInfo[] snapshot = instances.ToArray();
        Dictionary<string, InstanceMetadata> fallback = new(StringComparer.OrdinalIgnoreCase);
        foreach (LaunchInstanceInfo instance in snapshot)
            fallback[instance.InstanceDirectory] = new InstanceMetadata();

        lock (_metadataLock)
        {
            _metadataCache = fallback;
        }

        _metadataLoadTask = LoadMetadataAsync(snapshot, cancellationToken);
    }

    private async Task LoadMetadataAsync(LaunchInstanceInfo[] instances, CancellationToken cancellationToken)
    {
        try
        {
            Dictionary<string, InstanceMetadata> loaded = await ReadMetadataAsync(instances, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyMetadataSnapshot(loaded);

            Dispatcher.UIThread.Post(
                () =>
                {
                    if (!cancellationToken.IsCancellationRequested)
                        ReloadList();
                },
                DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<Dictionary<string, InstanceMetadata>> ReadMetadataAsync(
        LaunchInstanceInfo[] instances,
        CancellationToken cancellationToken)
    {
        Dictionary<string, InstanceMetadata> loaded = new(StringComparer.OrdinalIgnoreCase);
        foreach (LaunchInstanceInfo instance in instances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            loaded[instance.InstanceDirectory] =
                await InstanceMetadataStore.LoadAsync(instance.InstanceDirectory, cancellationToken)
                    .ConfigureAwait(false);
        }

        return loaded;
    }

    private void ApplyMetadataSnapshot(Dictionary<string, InstanceMetadata> metadata)
    {
        lock (_metadataLock)
        {
            _metadataCache = metadata;
        }
    }

    private void PanVerSearchBox_TextChanged(object sender, EventArgs e)
    {
        _lastInputTime = DateTime.Now;
        _isRefreshing = false;

        string text = this.FindControl<MySearchBox>("PanVerSearchBox")?.Text ?? string.Empty;
        int delay = string.IsNullOrWhiteSpace(text) ? SearchQuickDelayMs : SearchNormalDelayMs;
        if (Math.Abs(_reloadTimer.Interval.TotalMilliseconds - delay) > 0.1d)
            _reloadTimer.Interval = TimeSpan.FromMilliseconds(delay);

        if (!_reloadTimer.IsEnabled)
            _reloadTimer.Start();
    }

    private void ReloadTimer_Tick(object? sender, EventArgs e)
    {
        double elapsed = (DateTime.Now - _lastInputTime).TotalMilliseconds;
        if (elapsed < _reloadTimer.Interval.TotalMilliseconds || _isRefreshing)
            return;

        _isRefreshing = true;
        ReloadList();
        _isRefreshing = false;
        _reloadTimer.Stop();
    }

    private void ReloadList()
    {
        if (_isLoading)
            return;

        StackPanel? panel = this.FindControl<StackPanel>("PanMain");
        if (panel is null)
            return;

        string searchText = this.FindControl<MySearchBox>("PanVerSearchBox")?.Text?.Trim() ?? string.Empty;
        InstanceEntry[] allEntries = _instances
            .Select(instance => new InstanceEntry(instance, GetCachedMetadata(instance)))
            .ToArray();
        InstanceEntry[] visibleEntries = allEntries
            .Where(entry => _showHidden ? entry.Metadata.CardType == 1 : entry.Metadata.CardType != 1)
            .ToArray();
        InstanceEntry[] filteredInstances = visibleEntries
            .Where(entry => IsSearchMatch(entry, searchText))
            .ToArray();
        bool hasHiddenInstances = allEntries.Any(static entry => entry.Metadata.CardType == 1);

        panel.Children.Clear();
        if (filteredInstances.Length > 0)
        {
            foreach (IGrouping<int, InstanceEntry> group in filteredInstances
                         .GroupBy(static entry => entry.Metadata.IsStarred ? -1 : Math.Clamp(entry.Metadata.CardType, 0, 5))
                         .OrderBy(static group => group.Key is -1 ? 0 : group.Key + 1))
            {
                panel.Children.Add(CreateInstanceCard(group.ToArray()));
            }

            if (panel.Children.Count == 1 && panel.Children[0] is MyCard { IsSwapped: true } onlyCard)
                onlyCard.IsSwapped = false;
            ControlVisualHelpers.AnimateListEntrance(panel, "Instance Select List");
        }

        if (this.FindControl<Control>("PanVerSearchBox") is { } search)
            search.IsVisible = _instances.Count > 0;
        if (this.FindControl<Control>("BtnDownloadHeader") is { } headerDownload)
            headerDownload.IsVisible = _instances.Count > 0 || !_showHidden;
        SetVisible("BtnEmptyDownload", !_showHidden);
        if (_instances.Count == 0)
        {
            SetVisible("PanBack", false);
            SetVisible("PanEmpty", true);
            SetVisible("PanEmptySearch", false);
            SetText(
                "LabEmptyTitle",
                _showHidden
                    ? ResourceText("Select.Instance.Hidden.EmptyTitle", "没有隐藏版本")
                    : ResourceText("Select.Instance.Empty.Title", "还没有游戏版本"));
            SetText(
                "LabEmptyContent",
                _showHidden
                    ? ResourceText("Select.Instance.Hidden.EmptyMessage", "被隐藏的版本会显示在这里。")
                    : ResourceText("Select.Instance.Empty.Message", "你可以下载一个 Minecraft 原版版本，或把已有版本放入 .minecraft/versions。"));
            return;
        }

        SetVisible("BtnEmptyDownload", true);
        if (filteredInstances.Length == 0)
        {
            if (_showHidden && !hasHiddenInstances)
            {
                SetVisible("PanBack", false);
                SetVisible("PanEmpty", true);
                SetVisible("PanEmptySearch", false);
                SetVisible("BtnEmptyDownload", false);
                SetVisible("PanVerSearchBox", false);
                SetText("LabEmptyTitle", ResourceText("Select.Instance.Hidden.EmptyTitle", "没有隐藏版本"));
                SetText("LabEmptyContent", ResourceText("Select.Instance.Hidden.EmptyMessage", "被隐藏的版本会显示在这里。"));
                return;
            }

            SetVisible("PanBack", true);
            SetVisible("PanEmpty", false);
            SetVisible("PanEmptySearch", true);
            SetText(
                "LabEmptySearchTitle",
                _showHidden
                    ? ResourceText("Select.Instance.Hidden.EmptySearchTitle", "没有匹配的隐藏版本")
                    : ResourceText("Select.Instance.EmptySearch.Title", "没有搜索结果"));
            SetText(
                "LabEmptySearchContent",
                string.IsNullOrWhiteSpace(searchText)
                    ? ResourceText("Select.Instance.Search.EmptyInput", "请输入关键词后再搜索。")
                    : ResourceText(
                        _showHidden
                            ? "Select.Instance.Search.NoHiddenResult"
                            : "Select.Instance.Search.NoResult",
                        _showHidden
                            ? $"隐藏版本中没有找到包含“{searchText}”的结果。"
                            : $"没有找到包含“{searchText}”的本地版本。",
                        searchText));
            return;
        }

        SetVisible("PanBack", true);
        SetVisible("PanEmpty", false);
        SetVisible("PanEmptySearch", false);
    }

    private static bool IsSearchMatch(InstanceEntry entry, string searchText) =>
        string.IsNullOrWhiteSpace(searchText) ||
        entry.Instance.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
        entry.Instance.InstanceDirectory.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
        entry.Metadata.Description.Contains(searchText, StringComparison.OrdinalIgnoreCase);

    private MyCard CreateInstanceCard(InstanceEntry[] instances)
    {
        StackPanel stack = new()
        {
            Margin = new Thickness(16d, MyCard.SwapedHeight, 14d, 6d),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            RenderTransform = new TranslateTransform(),
            Tag = instances
        };
        InstanceEntry first = instances[0];
        bool isStarredCard = first.Metadata.IsStarred;
        int cardType = isStarredCard ? 0 : Math.Clamp(first.Metadata.CardType, 0, 5);
        MyCard card = new()
        {
            Title = InstanceDisplayHelper.GetCardTitle(cardType, isStarredCard, instances.Length),
            Margin = new Thickness(0d, 0d, 0d, 12d),
            SwapControl = stack
        };
        card.Children.Add(stack);

        void Install(StackPanel target)
        {
            if (target.Tag is not InstanceEntry[] entries)
                return;

            foreach (InstanceEntry entry in entries)
                target.Children.Add(CreateInstanceItem(entry));
        }

        if (ShouldStartCollapsed(cardType, isStarredCard))
        {
            card.IsSwapped = true;
            card.InstallMethod = Install;
        }
        else
        {
            MyCard.StackInstall(ref stack, Install);
        }

        return card;
    }

    private static bool ShouldStartCollapsed(int cardType, bool isStarredCard) =>
        !isStarredCard && cardType is 4 or 5;

    private InstanceMetadata GetCachedMetadata(LaunchInstanceInfo instance)
    {
        lock (_metadataLock)
        {
            return _metadataCache.TryGetValue(instance.InstanceDirectory, out InstanceMetadata? metadata)
                ? metadata
                : new InstanceMetadata();
        }
    }

    private MyListItem CreateInstanceItem(InstanceEntry entry)
    {
        LaunchInstanceInfo instance = entry.Instance;
        bool isValid = InstanceDisplayHelper.IsValid(instance);
        MyIconButton btnOpenFolder = new()
        {
            LogoScale = 1.1d,
            SvgIcon = "lucide/folder-open",
            ToolTip = "打开版本文件夹"
        };
        btnOpenFolder.Click += (_, _) => InstanceOpenFolderRequested?.Invoke(this, instance);

        MyIconButton btnDelete = new()
        {
            LogoScale = 1.1d,
            SvgIcon = "lucide/trash-2",
            ToolTip = "删除版本"
        };
        btnDelete.Click += (_, _) => InstanceDeleteRequested?.Invoke(this, instance);

        MyIconButton btnSettings = new()
        {
            LogoScale = 1.1d,
            SvgIcon = isValid ? "lucide/settings" : "lucide/folder-open",
            ToolTip = isValid ? "版本设置" : "打开版本文件夹"
        };
        btnSettings.Click += (_, _) =>
        {
            if (isValid)
                InstanceManageRequested?.Invoke(this, instance);
            else
                InstanceOpenFolderRequested?.Invoke(this, instance);
        };

        bool isSelected = _selectedInstance is not null &&
            string.Equals(_selectedInstance.InstanceDirectory, instance.InstanceDirectory, StringComparison.OrdinalIgnoreCase);
        string detail = string.IsNullOrWhiteSpace(entry.Metadata.Description)
            ? instance.InstanceDirectory
            : entry.Metadata.Description;
        MyListItem item = new()
        {
            Title = instance.Name,
            Info = isSelected
                ? ResourceText("Select.Instance.CurrentSelection", "当前选择") + " · " + detail
                : detail,
            Height = 48d,
            Tag = instance,
            Type = MyListItem.CheckType.Clickable,
            Logo = InstanceDisplayHelper.ResolveLogo(instance, entry.Metadata),
            LogoScale = 0.92d,
            MinPaddingRight = 8d,
            Buttons = [btnOpenFolder, btnDelete, btnSettings]
        };

        item.Click += (_, _) => TrySelectInstance(instance);
        return item;
    }

    private void BtnRefresh_Click(object? sender, EventArgs e)
    {
        SetLoadingState();
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Load_Click(object? sender, PointerReleasedEventArgs e)
    {
        SetLoadingState();
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void BtnDownload_Click(object? sender, EventArgs e) =>
        DownloadRequested?.Invoke(this, EventArgs.Empty);

    private void SetVisible(string name, bool isVisible)
    {
        if (this.FindControl<Control>(name) is { } control)
            control.IsVisible = isVisible;
    }

    private void SetText(string name, string text)
    {
        if (this.FindControl<TextBlock>(name) is { } block)
            block.Text = text;
    }

    private void SetLoadingAnimationState(bool isLoading)
    {
        if (this.FindControl<MyLoading>("Load") is not { } loading)
            return;

        if (loading.State is not MyLoadingStateSimulator simulator)
        {
            simulator = new MyLoadingStateSimulator();
            loading.State = simulator;
        }

        simulator.LoadingState = isLoading ? MyLoading.MyLoadingState.Run : MyLoading.MyLoadingState.Stop;
    }

    private string ResourceText(string key, string fallback, params object[] args)
    {
        string text = fallback;
        if (this.TryFindResource(key, ActualThemeVariant, out object? value) && value is string resourceText)
            text = resourceText;

        return args.Length == 0
            ? text
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, text, args);
    }

    private IBrush? ResolveBrush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush
            ? brush
            : null;

    private readonly record struct InstanceEntry(LaunchInstanceInfo Instance, InstanceMetadata Metadata);
}
