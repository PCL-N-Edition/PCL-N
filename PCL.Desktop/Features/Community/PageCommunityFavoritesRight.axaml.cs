// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PCL.Core.Logging;
using PCL.Desktop.Controls.Legacy;

namespace PCL.Desktop.Features.Community;

public sealed record CommunityFavoriteInputRequest(
    string Title,
    string Caption,
    string Content,
    string HintText,
    Action<string?> Complete,
    int MaxLength = 1000);

public sealed record CommunityFavoriteConfirmationRequest(
    string Title,
    string Caption,
    string PrimaryButton,
    Action<bool> Complete,
    bool IsWarning = false);

public partial class PageCommunityFavoritesRight : MyPageRight
{
    private readonly CommunityFavoritesStore _store;
    private readonly HashSet<string> _metadataResolutionAttempts = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _metadataResolutionCancellation = new();
    private bool _refreshingFolders;
    private bool _storeSubscribed;

    public PageCommunityFavoritesRight()
        : this(new CommunityFavoritesStore())
    {
    }

    public PageCommunityFavoritesRight(CommunityFavoritesStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        AvaloniaXamlLoader.Load(this);
        PanScroll = this.FindControl<MyScrollViewer>("PanBack");
        if (this.FindControl<MyComboBox>("ComboFolders") is { } folders)
            folders.SelectionChanged += (_, _) => SelectFolderFromComboBox();
        if (this.FindControl<MyIconButton>("BtnManageFolders") is { } manage)
            manage.Click += (_, _) => OpenManagementMenu(manage);
        SubscribeStore();
        AttachedToVisualTree += (_, _) =>
        {
            if (_metadataResolutionCancellation.IsCancellationRequested)
            {
                _metadataResolutionCancellation.Dispose();
                _metadataResolutionCancellation = new CancellationTokenSource();
                _metadataResolutionAttempts.Clear();
            }
            SubscribeStore();
            Render();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            UnsubscribeStore();
            _metadataResolutionCancellation.Cancel();
        };
        Render();
    }

    public event EventHandler<CommunityFavoriteEntry>? OpenProjectRequested;

    public event EventHandler<CommunityResourceDownloadRequest>? DownloadRequested;

    public event EventHandler<CommunityFavoriteInputRequest>? InputRequested;

    public event EventHandler<CommunityFavoriteConfirmationRequest>? ConfirmationRequested;

    public event EventHandler<(string Title, string Message)>? MessageRequested;

    public void Refresh()
    {
        _metadataResolutionAttempts.Clear();
        Render();
    }

    private void SubscribeStore()
    {
        if (_storeSubscribed)
            return;
        _store.Changed += Store_Changed;
        _storeSubscribed = true;
    }

    private void UnsubscribeStore()
    {
        if (!_storeSubscribed)
            return;
        _store.Changed -= Store_Changed;
        _storeSubscribed = false;
    }

    private void Store_Changed(object? sender, EventArgs e)
    {
        // ComboBox selection commits against its current item view. Rebuild on the next
        // dispatcher turn so changing the selected folder cannot invalidate that view mid-event.
        Dispatcher.UIThread.Post(Render);
    }

    private void Render()
    {
        RefreshFolderControls();
        if (this.FindControl<StackPanel>("PanFavorites") is not { } panel)
            return;
        panel.Children.Clear();
        CommunityFavoriteFolder selected = _store.SelectedFolder;
        if (this.FindControl<TextBlock>("LabFolderSummary") is { } summary)
            summary.Text = $"{selected.Name} · {selected.Items.Count} 项";

        if (selected.Items.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "当前收藏夹为空。",
                Margin = new Thickness(8, 14),
                Opacity = 0.7d,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
            return;
        }

        foreach (CommunityFavoriteEntry favorite in selected.Items)
            panel.Children.Add(CreateItem(favorite, selected.Id));
        ControlVisualHelpers.AnimateListEntrance(panel, "Community Favorites");
        if (TopLevel.GetTopLevel(this) is not null)
            QueueMetadataResolution(selected);
    }

    private void QueueMetadataResolution(CommunityFavoriteFolder folder)
    {
        List<CommunityFavoriteEntry> pending = folder.Items
            .Where(static favorite => CommunityFavoritesStore.IsImportedPlaceholder(favorite.Entry))
            .Where(favorite => _metadataResolutionAttempts.Add(CreateResolutionKey(folder.Id, favorite.Entry)))
            .ToList();
        if (pending.Count == 0)
            return;
        _ = ResolveMetadataAsync(folder.Id, pending, _metadataResolutionCancellation.Token);
    }

    private async Task ResolveMetadataAsync(
        string folderId,
        List<CommunityFavoriteEntry> favorites,
        CancellationToken cancellationToken)
    {
        try
        {
            using CompositeCommunityResourceCatalog catalog = new();
            using SemaphoreSlim throttle = new(initialCount: 6);
            Task<CommunityResourceEntry?>[] lookups = favorites
                .Select(favorite => ResolveOneAsync(catalog, throttle, favorite.Entry, cancellationToken))
                .ToArray();
            CommunityResourceEntry?[] results = await Task.WhenAll(lookups).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            List<CommunityResourceEntry> resolved = results.OfType<CommunityResourceEntry>().ToList();
            if (resolved.Count > 0)
                _store.ApplyResolvedMetadata(folderId, resolved);
            if (resolved.Count < favorites.Count)
            {
                PortableLog.Debug(
                    "CommunityUI",
                    $"CE 收藏夹项目解析完成；成功={resolved.Count}；失败={favorites.Count - resolved.Count}。");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            foreach (CommunityFavoriteEntry favorite in favorites)
                _metadataResolutionAttempts.Remove(CreateResolutionKey(folderId, favorite.Entry));
        }
        catch (Exception ex)
        {
            PortableLog.Warn(ex, "CommunityUI", "CE 收藏夹项目元数据解析失败，将保留项目 ID。");
        }
    }

    private static async Task<CommunityResourceEntry?> ResolveOneAsync(
        CompositeCommunityResourceCatalog catalog,
        SemaphoreSlim throttle,
        CommunityResourceEntry placeholder,
        CancellationToken cancellationToken)
    {
        await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await catalog
                .GetProjectAsync(placeholder.Source, placeholder.ProjectId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        finally
        {
            throttle.Release();
        }
    }

    private static string CreateResolutionKey(string folderId, CommunityResourceEntry entry) =>
        $"{folderId}:{(int)entry.Source}:{entry.ProjectId}";

    private void RefreshFolderControls()
    {
        if (this.FindControl<MyComboBox>("ComboFolders") is not { } combo)
            return;

        _refreshingFolders = true;
        try
        {
            combo.Items.Clear();
            string selectedId = _store.SelectedFolderId;
            MyComboBoxItem? selectedItem = null;
            foreach (CommunityFavoriteFolder folder in _store.Folders)
            {
                MyComboBoxItem item = new()
                {
                    Content = folder.Name,
                    Tag = folder.Id
                };
                combo.Items.Add(item);
                if (string.Equals(folder.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                    selectedItem = item;
            }

            combo.SelectedItem = selectedItem ?? combo.Items.Cast<object?>().FirstOrDefault();
        }
        finally
        {
            _refreshingFolders = false;
        }
    }

    private void SelectFolderFromComboBox()
    {
        if (_refreshingFolders ||
            this.FindControl<MyComboBox>("ComboFolders")?.SelectedItem is not MyComboBoxItem { Tag: string folderId })
        {
            return;
        }

        _store.SelectFolder(folderId);
    }

    private void OpenManagementMenu(Control target)
    {
        CommunityFavoriteFolder selected = _store.SelectedFolder;
        ContextMenu menu = new()
        {
            Placement = PlacementMode.Bottom,
            MinWidth = 190d
        };
        menu.Items.Add(CreateMenuItem("分享当前收藏夹", "lucide/share-2", () => _ = CopyShareJsonAsync()));
        menu.Items.Add(CreateMenuItem("导入到当前收藏夹", "lucide/circle-plus", PromptImportIntoCurrent));
        menu.Items.Add(CreateMenuItem("导入为新收藏夹", "lucide/folder-down", PromptImportAsNew));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("新建收藏夹", "lucide/folder-plus", PromptCreateFolder));
        menu.Items.Add(CreateMenuItem("重命名收藏夹", "lucide/pencil", () => PromptRenameFolder(selected)));
        menu.Items.Add(CreateMenuItem("删除收藏夹", "lucide/trash-2", () => PromptDeleteFolder(selected)));
        target.ContextMenu = menu;
        menu.Open(target);
    }

    private static MenuItem CreateMenuItem(string header, string icon, Action action)
    {
        MenuItem item = new()
        {
            Header = header,
            MinWidth = 180d,
            MinHeight = 32d,
            Padding = new Thickness(14d, 7d),
            Icon = new SvgIcon
            {
                Icon = icon,
                Width = 14d,
                Height = 14d
            }
        };
        item.Click += (_, args) =>
        {
            args.Handled = true;
            action();
        };
        return item;
    }

    private void PromptCreateFolder()
    {
        RequestInput(
            "新建收藏夹",
            "请输入收藏夹名称。",
            string.Empty,
            "收藏夹名称",
            result =>
            {
                if (!string.IsNullOrWhiteSpace(result))
                    RunStoreAction(() => _store.CreateFolder(result), "新建收藏夹失败");
            });
    }

    private void PromptRenameFolder(CommunityFavoriteFolder folder)
    {
        RequestInput(
            "重命名收藏夹",
            "请输入新的收藏夹名称。",
            folder.Name,
            "收藏夹名称",
            result =>
            {
                if (!string.IsNullOrWhiteSpace(result))
                    RunStoreAction(() => _store.RenameFolder(folder.Id, result), "重命名收藏夹失败");
            });
    }

    private void PromptDeleteFolder(CommunityFavoriteFolder folder)
    {
        if (_store.Folders.Count == 1)
        {
            ShowMessage("无法删除收藏夹", "至少需要保留一个收藏夹。");
            return;
        }

        if (ConfirmationRequested is null)
            return;
        ConfirmationRequested.Invoke(
            this,
            new CommunityFavoriteConfirmationRequest(
                "删除收藏夹",
                $"确定删除“{folder.Name}”及其中的 {folder.Items.Count} 项收藏吗？",
                "删除",
                confirmed =>
                {
                    if (confirmed)
                        RunStoreAction(() => _store.DeleteFolder(folder.Id), "删除收藏夹失败");
                },
                IsWarning: true));
    }

    private void PromptImportIntoCurrent()
    {
        string folderId = _store.SelectedFolderId;
        RequestShareJson(json =>
        {
            RunStoreAction(() =>
            {
                int added = _store.ImportShareJson(json, folderId);
                ShowMessage("导入完成", added > 0 ? $"已导入 {added} 项收藏。" : "没有需要新增的收藏。");
            }, "导入收藏夹失败");
        });
    }

    private void PromptImportAsNew()
    {
        RequestShareJson(json =>
        {
            RequestInput(
                "新收藏夹名称",
                "请输入用于保存导入内容的收藏夹名称。",
                string.Empty,
                "收藏夹名称",
                name =>
                {
                    if (!string.IsNullOrWhiteSpace(name))
                        RunStoreAction(() => _store.CreateFolderFromShare(name, json), "导入收藏夹失败");
                });
        });
    }

    private void RequestShareJson(Action<string> complete)
    {
        RequestInput(
            "导入 CE 收藏夹",
            "请粘贴 PCL CE 分享生成的 JSON ID 数组。",
            string.Empty,
            "例如 [\"AANobbMI\",\"1479191\"]",
            result =>
            {
                if (!string.IsNullOrWhiteSpace(result))
                    complete(result);
            },
            maxLength: 256 * 1024);
    }

    private void RequestInput(
        string title,
        string caption,
        string content,
        string hintText,
        Action<string?> complete,
        int maxLength = 1000)
    {
        if (InputRequested is null)
            return;
        InputRequested.Invoke(
            this,
            new CommunityFavoriteInputRequest(
                title,
                caption,
                content,
                hintText,
                complete,
                maxLength));
    }

    private async Task CopyShareJsonAsync()
    {
        if (_store.Items.Count == 0)
        {
            ShowMessage("无法分享收藏夹", "当前收藏夹为空。");
            return;
        }

        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
                throw new InvalidOperationException("剪贴板不可用。");
            await clipboard.SetTextAsync(_store.ExportShareJson()).ConfigureAwait(true);
            ShowMessage("已复制", "当前收藏夹的 CE 分享内容已复制到剪贴板。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ShowMessage("分享收藏夹失败", ex.Message);
        }
    }

    private void RunStoreAction(Action action, string title)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                   IOException or UnauthorizedAccessException or
                                   KeyNotFoundException)
        {
            ShowMessage(title, ex.Message);
        }
    }

    private void ShowMessage(string title, string message) =>
        MessageRequested?.Invoke(this, (title, message));

    private MyListItem CreateItem(CommunityFavoriteEntry favorite, string folderId)
    {
        MyIconButton remove = new()
        {
            SvgIcon = "lucide/star-off",
            ToolTip = "从当前收藏夹移除",
            Width = 25,
            Height = 25,
            Margin = new Thickness(0, 0, 4, 0)
        };
        remove.Click += (_, _) => _store.Toggle(favorite.Entry, favorite.Category, folderId);

        MyIconButton download = new()
        {
            SvgIcon = "lucide/download",
            ToolTip = "下载到当前实例（右键另存为）",
            Width = 25,
            Height = 25
        };
        CommunitySearchOptions favoriteOptions = CreateDownloadOptions(favorite.Entry);
        download.Click += (_, _) => DownloadRequested?.Invoke(
            this,
            new CommunityResourceDownloadRequest(
                favorite.Entry,
                favorite.Category,
                favoriteOptions));
        download.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(download).Properties.IsRightButtonPressed)
            {
                e.Handled = true;
                DownloadRequested?.Invoke(
                    this,
                    new CommunityResourceDownloadRequest(
                        favorite.Entry,
                        favorite.Category,
                        favoriteOptions,
                        SaveAs: true));
            }
        };

        MyListItem item = new()
        {
            Title = favorite.Entry.DisplayTitle,
            Info = favorite.Entry.DisplayDescription + "  ·  " +
                   favorite.Entry.SourceDisplayName,
            Height = 64d,
            Type = MyListItem.CheckType.Clickable,
            Tag = favorite,
            Logo = favorite.Entry.IconUrl ?? string.Empty,
            SvgIcon = string.IsNullOrWhiteSpace(favorite.Entry.IconUrl) ? "lucide/package" : string.Empty,
            LogoScale = 1.05d,
            Buttons = [download, remove]
        };
        item.Click += (_, _) => OpenProjectRequested?.Invoke(this, favorite);
        return item;
    }

    internal static CommunitySearchOptions CreateDownloadOptions(CommunityResourceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        bool hasBothSources = entry.ModrinthProject is not null && entry.CurseForgeProject is not null;
        return new CommunitySearchOptions(Source: hasBothSources ? CommunityResourceSource.All : entry.Source);
    }
}
