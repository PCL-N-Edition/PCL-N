// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PCL.Core.Logging;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Localization;

namespace PCL.Desktop.Features.Community;

/// <summary>
/// Independent resource detail page — structure mirrors WPF <c>PageDownloadCompDetail</c>:
/// intro card (project row + action buttons), chip filters, version-group cards, load overlay.
/// </summary>
public partial class PageCommunityDetail : MyPageRight, IDisposable
{
    private readonly ICommunityResourceCatalog _catalog;
    private readonly bool _ownsCatalog;
    private readonly CommunityFavoritesStore? _favorites;
    private CommunityResourceEntry? _entry;
    private CommunitySearchOptions _baseOptions = new();
    private CommunityResourceCategory _category = CommunityResourceCategory.Mod;
    private IReadOnlyList<CommunityResourceVersion> _allVersions = [];
    /// <summary>Major game line filter (e.g. <c>1.20</c>), null = 全部. Minor versions live under the major card.</summary>
    private string? _instanceFilter;
    private string? _loaderFilter;
    /// <summary>When set, prefer expanding this full version (e.g. <c>1.20.1</c>) under its major card.</summary>
    private string? _preferredMinorVersion;
    private CancellationTokenSource? _loadCancellation;
    private bool _disposed;
    private bool _filtersReady;

    public PageCommunityDetail()
        : this(new CompositeCommunityResourceCatalog(), ownsCatalog: true)
    {
    }

    public PageCommunityDetail(
        ICommunityResourceCatalog catalog,
        bool ownsCatalog = false,
        CommunityFavoritesStore? favorites = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _ownsCatalog = ownsCatalog;
        _favorites = favorites;
        AvaloniaXamlLoader.Load(this);
        PanScroll = this.FindControl<MyScrollViewer>("PanBack");

        if (this.FindControl<MyIconTextButton>("BtnIntroWeb") is { } web)
            web.Click += (_, _) =>
            {
                if (_entry is not null)
                    OpenWebRequested?.Invoke(this, _entry);
            };
        if (this.FindControl<MyIconTextButton>("BtnIntroMcMod") is { } mcMod)
            mcMod.Click += (_, _) =>
            {
                if (_entry is not null)
                    OpenUrlRequested?.Invoke(this, _entry.McModUrl ?? CreateMcModSearchUrl(_entry.Title));
            };
        if (this.FindControl<MyIconTextButton>("BtnIntroTranslation") is { } translation)
            translation.Click += async (_, _) => await ShowTranslationAsync().ConfigureAwait(true);
        if (this.FindControl<MyIconTextButton>("BtnIntroCopy") is { } copyName)
            copyName.Click += (_, _) => _ = CopyTextAsync(_entry?.Title ?? string.Empty);
        if (this.FindControl<MyIconTextButton>("BtnIntroLinkCopy") is { } copyLink)
            copyLink.Click += (_, _) => _ = CopyTextAsync(_entry?.WebsiteUrl ?? string.Empty);
        if (this.FindControl<MyIconTextButton>("BtnIntroFavorite") is { } favorite)
            favorite.Click += (_, _) => ToggleFavorite();

        SetLoading(false);
    }

    // Host may subscribe for title-bar / gesture back; currently title bar owns navigation.
#pragma warning disable CS0067
    public event EventHandler? BackRequested;
#pragma warning restore CS0067

    public event EventHandler<CommunityResourceEntry>? OpenWebRequested;

    public event EventHandler<string>? OpenUrlRequested;

    public event EventHandler<(string Title, string Message)>? MessageRequested;

    public event EventHandler<CommunityResourceDownloadRequest>? DownloadRequested;

    public CommunityResourceEntry? Entry => _entry;

    public async Task ShowAsync(
        CommunityResourceEntry entry,
        CommunityResourceCategory category,
        CommunitySearchOptions? options = null)
    {
        _entry = entry ?? throw new ArgumentNullException(nameof(entry));
        PortableLog.Info("CommunityUI", $"打开资源详情：{entry.Title}；来源={entry.Source}；分类={category}。");
        _category = category;
        _baseOptions = options ?? new CommunitySearchOptions();
        _instanceFilter = null;
        _loaderFilter = null;
        _preferredMinorVersion = null;
        _filtersReady = false;
        BindIntro(entry);
        UpdateFavoriteButton();
        await ReloadVersionsAsync().ConfigureAwait(true);
    }

    public override void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        if (_ownsCatalog && _catalog is IDisposable d)
            d.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private void BindIntro(CommunityResourceEntry entry)
    {
        if (this.FindControl<MyListItem>("ItemProject") is { } item)
        {
            item.Title = entry.DisplayTitle;
            string downloads = entry.Downloads > 0
                ? entry.Downloads.ToString("N0", CultureInfo.CurrentCulture) + " 次下载"
                : string.Empty;
            item.Info = string.IsNullOrWhiteSpace(entry.DisplayDescription)
                ? downloads
                : entry.DisplayDescription + (string.IsNullOrEmpty(downloads) ? string.Empty : " · " + downloads);
            item.Logo = entry.IconUrl ?? string.Empty;
            item.SvgIcon = string.IsNullOrWhiteSpace(entry.IconUrl) ? "lucide/package" : string.Empty;
            item.LogoScale = 1.08d;
            item.Type = MyListItem.CheckType.None;
        }

        if (this.FindControl<MyIconTextButton>("BtnIntroWeb") is { } web)
            web.Text = entry.Source == CommunityResourceSource.CurseForge ? "CurseForge" : "Modrinth";
        if (this.FindControl<MyIconTextButton>("BtnIntroMcMod") is { } mcMod)
            mcMod.IsVisible = _entry?.McModUrl is not null &&
                              _category is CommunityResourceCategory.Mod or CommunityResourceCategory.DataPack;
        if (this.FindControl<MyIconTextButton>("BtnIntroTranslation") is { } translation)
            translation.IsVisible = AvaloniaLocalizationManager.CurrentLanguageCode == AvaloniaLocalizationManager.ChineseLanguage &&
                                    _category is CommunityResourceCategory.Mod or CommunityResourceCategory.DataPack;
    }

    private async Task ShowTranslationAsync()
    {
        CommunityResourceEntry? entry = _entry;
        if (entry is null)
            return;
        CancellationToken token = _loadCancellation?.Token ?? CancellationToken.None;
        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(35) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-N/1.0");
            McimTranslationResult result = await new McimTranslationService(client)
                .GetAsync(entry, token)
                .ConfigureAwait(true);
            if (_disposed || !ReferenceEquals(_entry, entry) || token.IsCancellationRequested)
                return;
            string message = result.NotFound || string.IsNullOrWhiteSpace(result.Text)
                ? "MCIM 暂无此项目的中文描述。"
                : result.Text;
            MessageRequested?.Invoke(this, ("中文描述", message));
        }
        catch (OperationCanceledException)
        {
            if (!_disposed && ReferenceEquals(_entry, entry) && !token.IsCancellationRequested)
                MessageRequested?.Invoke(this, ("中文描述", "中文描述请求已取消或超时。"));
        }
        catch (Exception ex)
        {
            PortableLog.Warn(ex, "MCIM", $"中文描述请求失败；项目={entry.ProjectId}。");
            if (!_disposed && ReferenceEquals(_entry, entry) && !token.IsCancellationRequested)
                MessageRequested?.Invoke(this, ("中文描述", "暂时无法获取中文描述，请稍后重试。"));
        }
    }

    internal static string CreateMcModSearchUrl(string title) =>
        "https://www.mcmod.cn/s?key=" + Uri.EscapeDataString(title) + "&site=all&filter=0";

    private void ToggleFavorite()
    {
        if (_favorites is null || _entry is null)
            return;
        _favorites.Toggle(_entry, _category);
        UpdateFavoriteButton();
    }

    private void UpdateFavoriteButton()
    {
        if (this.FindControl<MyIconTextButton>("BtnIntroFavorite") is not { } button)
            return;
        button.IsVisible = _favorites is not null;
        bool favorite = _favorites is not null && _entry is not null && _favorites.Contains(_entry);
        button.Text = favorite ? "取消收藏" : "收藏";
        button.SvgIcon = favorite ? "lucide/star-off" : "lucide/star";
    }

    private async Task ReloadVersionsAsync()
    {
        if (_entry is null || _disposed)
            return;

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        CancellationTokenSource loadCancellation = new();
        _loadCancellation = loadCancellation;
        CancellationToken token = loadCancellation.Token;
        CommunityResourceEntry entry = _entry;
        SetLoading(true);
        SetError(null);

        try
        {
            // Fetch without client-side filters first so chips can list all options (WPF CompFilesGet).
            CommunitySearchOptions fetchOptions = new(
                _baseOptions.Sort,
                GameVersion: null,
                Loader: null,
                Tag: _baseOptions.Tag,
                Source: entry.Source);
            IReadOnlyList<CommunityResourceVersion> versions =
                await _catalog.GetVersionsAsync(entry, fetchOptions, token).ConfigureAwait(false);
            versions = await CommunityResourceDependencyResolver
                .EnrichNamesAsync(_catalog, versions, token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!IsCurrentLoad(loadCancellation, entry))
                    return;
                _allVersions = versions;
                BuildFilterChips(versions);
                ApplyFiltersAndRender();
                SetLoading(false);
            });
        }
        catch (OperationCanceledException)
        {
            PortableLog.Debug("CommunityUI", $"资源版本加载已取消：{entry.Title}");
        }
        catch (Exception ex)
        {
            if (!IsCurrentLoad(loadCancellation, entry))
            {
                PortableLog.Debug(ex, "CommunityUI", $"忽略已过期资源版本请求的异常：{entry.Title}");
                return;
            }

            PortableLog.Error(ex, "CommunityUI", $"加载资源版本失败：{entry.Title}；来源={entry.Source}。");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!IsCurrentLoad(loadCancellation, entry))
                    return;
                SetError(ex.Message);
                SetLoading(false);
            });
        }
    }

    private bool IsCurrentLoad(
        CancellationTokenSource loadCancellation,
        CommunityResourceEntry entry) =>
        !_disposed &&
        !loadCancellation.IsCancellationRequested &&
        ReferenceEquals(_loadCancellation, loadCancellation) &&
        ReferenceEquals(_entry, entry);

    private void BuildFilterChips(IReadOnlyList<CommunityResourceVersion> versions)
    {
        Panel? panVersion = this.FindControl<Panel>("PanInstanceFilter");
        Panel? panLoader = this.FindControl<Panel>("PanModLoaderFilter");
        Control? cardFilter = this.FindControl<Control>("CardFilter");
        if (panVersion is null || panLoader is null)
            return;

        panVersion.Children.Clear();
        panLoader.Children.Clear();

        HashSet<string> majorLines = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> loaders = new(StringComparer.OrdinalIgnoreCase);
        foreach (CommunityResourceVersion v in versions)
        {
            foreach (string g in v.GameVersions)
            {
                if (string.IsNullOrWhiteSpace(g))
                    continue;
                majorLines.Add(GetMajorGameLine(g));
            }

            foreach (string l in v.Loaders)
            {
                if (!string.IsNullOrWhiteSpace(l))
                    loaders.Add(l);
            }
        }

        // Chips list major lines only (1.21 / 1.20 / 1.16…); minors live under each major card.
        List<string> orderedMajors = majorLines
            .OrderByDescending(static s => s, MinecraftVersionNameComparer.Instance)
            .ToList();
        List<string> orderedLoaders = loaders
            .OrderBy(static s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool showFilters = orderedMajors.Count >= 2 || orderedLoaders.Count >= 1;
        if (cardFilter is not null)
            cardFilter.IsVisible = showFilters;

        if (!showFilters)
        {
            _filtersReady = true;
            return;
        }

        // List-page filter may be full (1.20.1) or major (1.20) — map to major chip + remember minor.
        string? preferredVersion = _baseOptions.GameVersion;
        _preferredMinorVersion = null;
        if (!string.IsNullOrWhiteSpace(preferredVersion))
        {
            string preferredMajor = GetMajorGameLine(preferredVersion);
            if (orderedMajors.Any(v => string.Equals(v, preferredMajor, StringComparison.OrdinalIgnoreCase)))
            {
                _instanceFilter = preferredMajor;
                if (!string.Equals(preferredVersion, preferredMajor, StringComparison.OrdinalIgnoreCase))
                    _preferredMinorVersion = preferredVersion.Trim();
            }
            else
            {
                _instanceFilter = null;
            }
        }
        else
        {
            _instanceFilter = null;
        }

        panVersion.Children.Add(CreateFilterLabel("版本"));
        AddChip(panVersion, "全部", isVersion: true, selected: _instanceFilter is null);
        foreach (string major in orderedMajors)
        {
            bool selected = _instanceFilter is not null &&
                            string.Equals(major, _instanceFilter, StringComparison.OrdinalIgnoreCase);
            AddChip(panVersion, major, isVersion: true, selected: selected);
        }

        // Loader chips (mods / modpacks)
        bool showLoaderRow = _category is CommunityResourceCategory.Mod or CommunityResourceCategory.Modpack
                             && orderedLoaders.Count > 0;
        panLoader.IsVisible = showLoaderRow;
        if (showLoaderRow)
        {
            string? preferredLoader = _baseOptions.Loader;
            if (!string.IsNullOrWhiteSpace(preferredLoader) &&
                orderedLoaders.Any(l => string.Equals(l, preferredLoader, StringComparison.OrdinalIgnoreCase)))
            {
                _loaderFilter = preferredLoader;
            }
            else
            {
                _loaderFilter = null;
            }

            panLoader.Children.Add(CreateFilterLabel("加载器"));
            AddChip(panLoader, "全部", isVersion: false, selected: _loaderFilter is null);
            foreach (string l in orderedLoaders)
            {
                bool selected = _loaderFilter is not null &&
                                string.Equals(l, _loaderFilter, StringComparison.OrdinalIgnoreCase);
                AddChip(panLoader, l, isVersion: false, selected: selected);
            }
        }
        else
        {
            _loaderFilter = null;
        }

        _filtersReady = true;
    }

    private static TextBlock CreateFilterLabel(string text) =>
        new()
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 6, 0),
            Opacity = 0.75,
            FontSize = 12
        };

    private void AddChip(Panel panel, string text, bool isVersion, bool selected)
    {
        MyRadioButton chip = new()
        {
            Text = text,
            ColorType = MyRadioButton.ColorState.Highlight,
            Margin = new Thickness(2, 2, 2, 2),
            Checked = selected,
            // Keep chips compact so they wrap inside CardFilter (WPF-style).
            MaxWidth = 118,
            MinHeight = 26
        };
        // Hide logo host for text-only chips (WPF LabText-only radio).
        chip.SvgIcon = string.Empty;
        chip.Logo = string.Empty;
        chip.Check += (sender, _) =>
        {
            if (!_filtersReady || sender is not MyRadioButton rb)
                return;
            if (isVersion)
            {
                _instanceFilter = string.Equals(rb.Text, "全部", StringComparison.Ordinal) ? null : rb.Text;
                // Manual chip change clears the one-shot preferred minor from the list page.
                _preferredMinorVersion = null;
            }
            else
            {
                _loaderFilter = string.Equals(rb.Text, "全部", StringComparison.Ordinal) ? null : rb.Text;
            }

            ApplyFiltersAndRender();
        };
        panel.Children.Add(chip);
    }

    private void ApplyFiltersAndRender()
    {
        if (this.FindControl<StackPanel>("PanResults") is not { } panResults)
            return;

        panResults.Children.Clear();
        List<CommunityResourceVersion> filtered = _allVersions.ToList();
        if (!string.IsNullOrWhiteSpace(_instanceFilter))
        {
            string major = _instanceFilter;
            filtered = filtered
                .Where(v => v.GameVersions.Any(g => MatchesMajorLine(g, major)))
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(_loaderFilter))
        {
            filtered = filtered
                .Where(v =>
                    v.Loaders.Any(l => string.Equals(l, _loaderFilter, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        // Hierarchy: major line cards (1.20 / 1.21) → nested minor cards (1.20.1 / 1.20.2) → files.
        Dictionary<string, Dictionary<string, List<CommunityResourceVersion>>> majorGroups =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (CommunityResourceVersion version in filtered)
        {
            IEnumerable<string> gameVersions = version.GameVersions
                .Where(static g => !string.IsNullOrWhiteSpace(g));

            if (!string.IsNullOrWhiteSpace(_instanceFilter))
            {
                // Under a major filter, only place into minors that belong to that major.
                foreach (string g in gameVersions.Where(g => MatchesMajorLine(g, _instanceFilter!)))
                    AddToMajorMinorGroup(majorGroups, GetMajorGameLine(g), g.Trim(), version);
                continue;
            }

            // 全部: assign each file once to its newest supported game version's major/minor.
            string? newest = gameVersions
                .OrderByDescending(static g => g, MinecraftVersionNameComparer.Instance)
                .FirstOrDefault();
            if (newest is null)
            {
                AddToMajorMinorGroup(majorGroups, "其他", "其他", version);
                continue;
            }

            AddToMajorMinorGroup(majorGroups, GetMajorGameLine(newest), newest.Trim(), version);
        }

        if (majorGroups.Count == 0)
        {
            panResults.Children.Add(new TextBlock
            {
                Text = "没有匹配的版本文件。请调整筛选条件。",
                Margin = new Thickness(8, 12),
                Opacity = 0.7
            });
            return;
        }

        List<KeyValuePair<string, Dictionary<string, List<CommunityResourceVersion>>>> orderedMajors = majorGroups
            .OrderByDescending(static g => g.Key, MinecraftVersionNameComparer.Instance)
            .ToList();

        int majorIndex = 0;
        foreach ((string majorTitle, Dictionary<string, List<CommunityResourceVersion>> minors) in orderedMajors)
        {
            int fileCount = minors.Values.Sum(static list => list.Count);
            List<KeyValuePair<string, List<CommunityResourceVersion>>> orderedMinors = minors
                .OrderByDescending(static m => m.Key, MinecraftVersionNameComparer.Instance)
                .ToList();

            bool expandMajor = majorIndex == 0 ||
                               (!string.IsNullOrWhiteSpace(_instanceFilter) &&
                                string.Equals(majorTitle, _instanceFilter, StringComparison.OrdinalIgnoreCase));

            MyCard majorCard = new()
            {
                Title = majorTitle + "（" + fileCount.ToString(CultureInfo.CurrentCulture) + "）",
                Margin = new Thickness(0, 0, 0, 15),
                CanSwap = true,
                IsSwapped = !expandMajor
            };

            StackPanel majorStack = new()
            {
                Margin = new Thickness(12d, MyCard.SwapedHeight, 12d, 8d),
                VerticalAlignment = VerticalAlignment.Top,
                Tag = new MajorGroupTag(majorTitle, orderedMinors)
            };
            majorCard.SwapControl = majorStack;
            majorCard.InstallMethod = InstallMajorGroupStack;
            majorCard.Children.Add(majorStack);
            panResults.Children.Add(majorCard);

            if (!majorCard.IsSwapped)
                majorCard.StackInstall();

            majorIndex++;
        }

        if (panResults.Children.Count == 1 && panResults.Children[0] is MyCard only)
        {
            only.IsSwapped = false;
            only.StackInstall();
        }
    }

    private void InstallMajorGroupStack(StackPanel stack)
    {
        if (stack.Tag is not MajorGroupTag tag)
            return;

        stack.Children.Clear();

        // Single minor under this major: skip the extra nested card.
        if (tag.Minors.Count == 1)
        {
            InstallFileRows(stack, tag.Minors[0].Value);
            return;
        }

        int minorIndex = 0;
        foreach ((string minorTitle, List<CommunityResourceVersion> files) in tag.Minors)
        {
            List<CommunityResourceVersion> sorted = files
                .OrderByDescending(static v => v.PublishedAt ?? DateTimeOffset.MinValue)
                .ToList();

            bool preferThisMinor = _preferredMinorVersion is { Length: > 0 } preferred &&
                                   string.Equals(minorTitle, preferred, StringComparison.OrdinalIgnoreCase);
            // Preferred minor expanded first; otherwise newest minor open, rest collapsed.
            bool expandMinor = preferThisMinor ||
                               (minorIndex == 0 && string.IsNullOrWhiteSpace(_preferredMinorVersion));

            MyCard minorCard = new()
            {
                Title = minorTitle + "（" + sorted.Count.ToString(CultureInfo.CurrentCulture) + "）",
                Margin = new Thickness(0, 0, 0, 10),
                CanSwap = true,
                IsSwapped = !expandMinor
            };

            StackPanel minorStack = new()
            {
                Margin = new Thickness(12d, MyCard.SwapedHeight, 10d, 6d),
                VerticalAlignment = VerticalAlignment.Top,
                Tag = sorted
            };
            minorCard.SwapControl = minorStack;
            minorCard.InstallMethod = InstallVersionStack;
            minorCard.Children.Add(minorStack);
            stack.Children.Add(minorCard);

            if (!minorCard.IsSwapped)
                minorCard.StackInstall();

            minorIndex++;
        }
    }

    private void InstallVersionStack(StackPanel stack)
    {
        if (stack.Tag is not List<CommunityResourceVersion> list)
            return;
        InstallFileRows(stack, list);
    }

    private void InstallFileRows(StackPanel stack, List<CommunityResourceVersion> list)
    {
        if (_entry is null)
            return;

        stack.Children.Clear();
        CommunitySearchOptions options = new(
            _baseOptions.Sort,
            _instanceFilter,
            _loaderFilter,
            _baseOptions.Tag);

        foreach (CommunityResourceVersion version in list.OrderByDescending(v => v.PublishedAt ?? DateTimeOffset.MinValue))
        {
            CommunityResourceDownloadFile? primary = version.Files.Count > 0 ? version.Files[0] : null;
            if (primary is null)
                continue;

            string loaders = version.Loaders.Count > 0 ? string.Join(", ", version.Loaders) : "—";
            string published = version.PublishedAt is { } p
                ? p.ToLocalTime().ToString("yyyy/MM/dd", CultureInfo.CurrentCulture)
                : "—";
            string size = primary.Size > 0 ? FormatSize(primary.Size) : "—";
            string dependencies = FormatDependencies(version.Dependencies);
            string gameRange = version.GameVersions.Count > 0
                ? string.Join(", ", version.GameVersions
                    .OrderByDescending(static g => g, MinecraftVersionNameComparer.Instance)
                    .Take(4))
                : "—";

            MyIconButton download = new()
            {
                SvgIcon = "lucide/download",
                LogoScale = 0.85d,
                ToolTip = "下载 " + primary.FileName + "（右键另存为）",
                Width = 25,
                Height = 25
            };
            CommunityResourceEntry entry = _entry;
            CommunityResourceDownloadFile file = primary;
            download.Click += (_, _) => DownloadRequested?.Invoke(
                this,
                new CommunityResourceDownloadRequest(entry, _category, options, file, version));
            download.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(download).Properties.IsRightButtonPressed)
                {
                    e.Handled = true;
                    DownloadRequested?.Invoke(
                        this,
                        new CommunityResourceDownloadRequest(entry, _category, options, file, version, SaveAs: true));
                }
            };

            MyListItem item = new()
            {
                Title = string.IsNullOrWhiteSpace(version.Name)
                    ? version.VersionNumber
                    : version.Name,
                Info = gameRange + " · " + loaders + " · " + size + " · " + published + " · " + primary.FileName +
                       (string.IsNullOrWhiteSpace(dependencies) ? string.Empty : "\n" + dependencies),
                Height = string.IsNullOrWhiteSpace(dependencies) ? 48d : 66d,
                Type = MyListItem.CheckType.Clickable,
                SvgIcon = "lucide/file-archive",
                Buttons = [download],
                Tag = version
            };
            item.Click += (_, _) => DownloadRequested?.Invoke(
                this,
                new CommunityResourceDownloadRequest(entry, _category, options, file, version));
            stack.Children.Add(item);
        }
    }

    private static void AddToMajorMinorGroup(
        Dictionary<string, Dictionary<string, List<CommunityResourceVersion>>> majorGroups,
        string major,
        string minor,
        CommunityResourceVersion version)
    {
        if (!majorGroups.TryGetValue(major, out Dictionary<string, List<CommunityResourceVersion>>? minors))
        {
            minors = new Dictionary<string, List<CommunityResourceVersion>>(StringComparer.OrdinalIgnoreCase);
            majorGroups[major] = minors;
        }

        if (!minors.TryGetValue(minor, out List<CommunityResourceVersion>? list))
        {
            list = [];
            minors[minor] = list;
        }

        if (list.Any(v => string.Equals(v.VersionId, version.VersionId, StringComparison.OrdinalIgnoreCase)))
            return;
        list.Add(version);
    }

    /// <summary>
    /// Minecraft “大版本” line: <c>1.20.1</c>/<c>1.20</c> → <c>1.20</c>; <c>26.2-rc-1</c> → <c>26.2</c>.
    /// Non-semver labels are kept as-is.
    /// </summary>
    private static string GetMajorGameLine(string gameVersion)
    {
        if (string.IsNullOrWhiteSpace(gameVersion))
            return "其他";

        string raw = gameVersion.Trim();
        string core = raw.Split('-', '+')[0].Trim();
        if (!Version.TryParse(core, out Version? v))
            return raw;

        // Two-component line is what players call the major (1.16 / 1.20 / 26.1).
        return v.Major.ToString(CultureInfo.InvariantCulture) + "." +
               v.Minor.ToString(CultureInfo.InvariantCulture);
    }

    private static bool MatchesMajorLine(string gameVersion, string majorFilter)
    {
        if (string.Equals(gameVersion, majorFilter, StringComparison.OrdinalIgnoreCase))
            return true;
        return string.Equals(GetMajorGameLine(gameVersion), majorFilter, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record MajorGroupTag(
        string Major,
        List<KeyValuePair<string, List<CommunityResourceVersion>>> Minors);

    private static string FormatDependencies(IReadOnlyList<CommunityResourceDependency> dependencies)
    {
        List<string> groups = [];
        AddGroup(CommunityResourceDependencyType.Required, "必需前置");
        AddGroup(CommunityResourceDependencyType.Optional, "可选前置");
        AddGroup(CommunityResourceDependencyType.Incompatible, "不兼容");
        return string.Join("；", groups);

        void AddGroup(CommunityResourceDependencyType type, string label)
        {
            string[] names = dependencies
                .Where(dependency => dependency.Type == type)
                .Select(static dependency => dependency.DisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (names.Length > 0)
                groups.Add(label + "：" + string.Join("、", names));
        }
    }

    private void SetLoading(bool loading)
    {
        if (this.FindControl<Control>("PanLoad") is { } load)
            load.IsVisible = loading;
        if (this.FindControl<Control>("PanMain") is { } main)
            main.IsVisible = !loading;
        if (this.FindControl<MyLoading>("Load") is { } spinner)
            spinner.State.LoadingState = loading ? MyLoading.MyLoadingState.Run : MyLoading.MyLoadingState.Stop;
    }

    private void SetError(string? message)
    {
        if (this.FindControl<MyHint>("HintError") is not { } hint)
            return;
        if (string.IsNullOrWhiteSpace(message))
        {
            hint.IsVisible = false;
            return;
        }

        hint.IsVisible = true;
        hint.Text = "获取版本失败：" + message;
    }

    private async Task CopyTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        try
        {
            TopLevel? top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is null)
                return;
            await top.Clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
        catch
        {
            // ignore
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return bytes + " B";
        if (bytes < 1024 * 1024)
            return (bytes / 1024d).ToString("0.0", CultureInfo.CurrentCulture) + " KB";
        return (bytes / (1024d * 1024d)).ToString("0.00", CultureInfo.CurrentCulture) + " MB";
    }

    /// <summary>Orders Minecraft version labels newest-first (1.21 &gt; 1.20.1 &gt; 1.12.2).</summary>
    private sealed class MinecraftVersionNameComparer : IComparer<string>
    {
        public static MinecraftVersionNameComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;
            if (string.Equals(x, "其他", StringComparison.Ordinal) &&
                !string.Equals(y, "其他", StringComparison.Ordinal))
            {
                return -1;
            }

            if (string.Equals(y, "其他", StringComparison.Ordinal) &&
                !string.Equals(x, "其他", StringComparison.Ordinal))
            {
                return 1;
            }

            Version? vx = TryParseMc(x);
            Version? vy = TryParseMc(y);
            if (vx is not null && vy is not null)
                return vx.CompareTo(vy);
            if (vx is not null)
                return 1;
            if (vy is not null)
                return -1;
            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        }

        private static Version? TryParseMc(string text)
        {
            string core = text.Split('-', '+')[0].Trim();
            return Version.TryParse(core, out Version? v) ? v : null;
        }
    }
}
