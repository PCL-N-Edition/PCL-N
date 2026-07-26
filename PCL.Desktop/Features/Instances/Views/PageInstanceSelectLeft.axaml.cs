// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using PCL.Desktop.Controls.Legacy;

namespace PCL.Desktop.Features.Instances.Views;

public sealed record MinecraftFolderInfo(string Name, string RootDirectory, bool IsCustom = false);

public partial class PageInstanceSelectLeft : MyPageLeft, IRefreshable
{
    private MinecraftFolderInfo[] _folders = [];
    private string? _selectedRootDirectory;

    public PageInstanceSelectLeft()
    {
        AvaloniaXamlLoader.Load(this);
        AnimatedControl = this.FindControl<Control>("PanList");
        ReloadList();
    }

    public event EventHandler<MinecraftFolderInfo>? FolderSelected;

    public event EventHandler<MinecraftFolderInfo>? FolderOpenRequested;

    public event EventHandler<MinecraftFolderInfo>? FolderRefreshRequested;

    public event EventHandler<MinecraftFolderInfo>? FolderRenameRequested;

    public event EventHandler<MinecraftFolderInfo>? FolderRemoveRequested;

    public event EventHandler? CreateFolderRequested;

    public event EventHandler? AddFolderRequested;

    public event EventHandler? ImportModpackRequested;

    public IReadOnlyList<MinecraftFolderInfo> Folders => _folders;

    public string? SelectedRootDirectory => _selectedRootDirectory;

    public void SetFolders(IReadOnlyList<MinecraftFolderInfo> folders, string? selectedRootDirectory)
    {
        _folders = FilterExistingFolders(folders);
        _selectedRootDirectory = ResolveSelectedFolderPath(selectedRootDirectory);
        ReloadList();
    }

    private string? ResolveSelectedFolderPath(string? preferredRoot)
    {
        string? preferred = NormalizePath(preferredRoot);
        if (preferred is not null)
        {
            MinecraftFolderInfo? match = _folders.FirstOrDefault(folder =>
                string.Equals(NormalizePath(folder.RootDirectory), preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return NormalizePath(match.RootDirectory);
        }

        if (_selectedRootDirectory is not null)
        {
            MinecraftFolderInfo? current = _folders.FirstOrDefault(folder =>
                string.Equals(
                    NormalizePath(folder.RootDirectory),
                    NormalizePath(_selectedRootDirectory),
                    StringComparison.OrdinalIgnoreCase));
            if (current is not null)
                return NormalizePath(current.RootDirectory);
        }

        return _folders.Length > 0 ? NormalizePath(_folders[0].RootDirectory) : preferred;
    }

    public void Refresh() => ReloadList();

    public bool TrySelectFolder(MinecraftFolderInfo folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!_folders.Contains(folder))
            return false;

        _selectedRootDirectory = NormalizePath(folder.RootDirectory);
        FolderSelected?.Invoke(this, folder);
        return true;
    }

    private void ReloadList()
    {
        if (this.FindControl<StackPanel>("PanList") is not { } panel)
            return;

        panel.Children.Clear();
        panel.Children.Add(CreateSectionTitle("游戏文件夹"));
        foreach (MinecraftFolderInfo folder in _folders)
            panel.Children.Add(CreateFolderItem(folder));

        panel.Children.Add(CreateSectionTitle("添加或导入"));
        string defaultRoot = Path.Combine(AppContext.BaseDirectory, ".minecraft");
        if (!Directory.Exists(defaultRoot))
        {
            panel.Children.Add(CreateActionItem(
                "新建游戏文件夹",
                "在启动器目录创建 .minecraft 文件夹",
                "lucide/folder-plus",
                () => CreateFolderRequested?.Invoke(this, EventArgs.Empty)));
        }

        panel.Children.Add(CreateActionItem(
            "添加已有文件夹",
            "把已有的 Minecraft 文件夹加入列表",
            "lucide/folder-input",
            () => AddFolderRequested?.Invoke(this, EventArgs.Empty)));
        panel.Children.Add(CreateActionItem(
            "导入整合包",
            "从本地压缩包导入游戏实例",
            "lucide/package-plus",
            () => ImportModpackRequested?.Invoke(this, EventArgs.Empty)));
        panel.Children.Add(new Border { Height = 10d, IsHitTestVisible = false });
    }

    private MyListItem CreateFolderItem(MinecraftFolderInfo folder)
    {
        string? folderPath = NormalizePath(folder.RootDirectory);

        MyIconButton openButton = new()
        {
            SvgIcon = "lucide/folder-open",
            LogoScale = 1.05d,
            ToolTip = "打开文件夹"
        };
        openButton.Click += (_, _) => FolderOpenRequested?.Invoke(this, folder);

        MyIconButton refreshButton = new()
        {
            SvgIcon = "lucide/refresh-cw",
            LogoScale = 0.9d,
            ToolTip = "刷新"
        };
        refreshButton.Click += (_, _) => FolderRefreshRequested?.Invoke(this, folder);

        // Presets and custom folders can leave the list; only custom may be renamed.
        List<MyIconButton> buttons = [openButton, refreshButton];
        if (folder.IsCustom)
        {
            MyIconButton renameButton = new()
            {
                SvgIcon = "lucide/pencil",
                LogoScale = 0.9d,
                ToolTip = "重命名"
            };
            renameButton.Click += (_, _) => FolderRenameRequested?.Invoke(this, folder);
            buttons.Add(renameButton);
        }

        MyIconButton removeButton = new()
        {
            SvgIcon = "lucide/list-x",
            LogoScale = 0.95d,
            ToolTip = "从列表移除"
        };
        removeButton.Click += (_, _) => FolderRemoveRequested?.Invoke(this, folder);
        buttons.Add(removeButton);

        MyListItem item = new()
        {
            Title = folder.Name,
            Info = folder.RootDirectory,
            Height = 44d,
            Type = MyListItem.CheckType.RadioBox,
            MinPaddingRight = 32d,
            IsScaleAnimationEnabled = false,
            Tag = folder,
            Buttons = buttons.ToArray()
        };
        item.SetChecked(
            folderPath is not null &&
            string.Equals(folderPath, _selectedRootDirectory, StringComparison.OrdinalIgnoreCase),
            user: false,
            animate: false);
        item.Click += (_, _) => TrySelectFolder(folder);
        return item;
    }

    private static MinecraftFolderInfo[] FilterExistingFolders(
        IReadOnlyList<MinecraftFolderInfo>? folders) =>
        folders?
            .Where(static folder =>
                NormalizePath(folder.RootDirectory) is { } path &&
                Directory.Exists(path))
            .ToArray()
        ?? [];

    private static TextBlock CreateSectionTitle(string text) =>
        new()
        {
            Text = text,
            Margin = new Thickness(13d, 16d, 5d, 6d),
            Opacity = 0.55d,
            FontSize = 11d,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            LetterSpacing = 0.35d
        };

    private static MyListItem CreateActionItem(string title, string toolTip, string icon, Action clicked)
    {
        MyListItem item = new()
        {
            Title = title,
            Height = 36d,
            Type = MyListItem.CheckType.Clickable,
            IsScaleAnimationEnabled = false,
            SvgIcon = icon,
            LogoScale = 0.95d
        };
        ToolTip.SetTip(item, toolTip);
        item.Click += (_, _) => clicked();
        return item;
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
