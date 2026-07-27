// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Features.Instances.Views;
using PCL.Desktop.Features.Launching.Views;
using PCL.Desktop.Session;
using PCL.Desktop.Shell;

namespace PCL.Desktop.Features.Instances;

/// <summary>
/// Owns version-select left/right pages and applies classic vs full-page layout from
/// <see cref="ExperimentalUiProfile"/>. Bound to <see cref="MinecraftFolderStore"/>.
/// </summary>
public sealed class InstancesSelectSurface
{
    public const string SubPageId = "instances.select";

    private readonly MinecraftFolderStore _folderStore;
    private readonly ExperimentalUiProfileSource _profileSource;
    private object? _hostToken;
    private PageInstanceSelectLeft? _left;
    private PageInstanceSelectRight? _right;

    public InstancesSelectSurface(
        MinecraftFolderStore folderStore,
        ExperimentalUiProfileSource profileSource)
    {
        _folderStore = folderStore;
        _profileSource = profileSource;
    }

    public PageInstanceSelectRight? RightPage => _right;

    public PageInstanceSelectLeft? LeftPage => _left;

    public bool IsFullPageLayout =>
        _profileSource.RefreshFromSettings().Select == InstanceSelectLayout.FullPageSidebar;

    /// <summary>
    /// Wire host callbacks. Page cache is dropped when <paramref name="hostToken"/> changes
    /// so controls are not re-parented across MainWindow instances (headless tests).
    /// </summary>
    public void WireOnce(object hostToken, InstancesSelectBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(hostToken);
        ArgumentNullException.ThrowIfNull(bindings);
        if (!ReferenceEquals(_hostToken, hostToken))
        {
            _hostToken = hostToken;
            _left = null;
            _right = null;
        }

        if (_right is null || _left is null)
        {
            _right = CreateRightPage(bindings);
            _left = CreateLeftPage(bindings);
        }
    }

    public void Apply(
        Border leftHost,
        Border rightHost,
        IReadOnlyList<LaunchInstanceInfo> instances,
        LaunchInstanceInfo? selectedInstance)
    {
        if (_right is null)
            throw new InvalidOperationException("InstancesSelectSurface 尚未 WireOnce。");

        bool fullPage = IsFullPageLayout;
        _right.SetFullPageLayout(fullPage);
        _right.SetInstances(instances, selectedInstance);
        string? preferredFolderRoot = ResolvePreferredFolderRoot(
            selectedInstance,
            _folderStore.Folders,
            _folderStore.SelectedRoot);
        if (preferredFolderRoot is { Length: > 0 } &&
            !string.Equals(
                _folderStore.SelectedRoot,
                preferredFolderRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            _folderStore.SetSelectedRootWithoutPersist(preferredFolderRoot);
        }

        if (fullPage)
        {
            if (leftHost.Child is MyPageLeft oldLeft)
                oldLeft.TriggerHideAnimation();
            leftHost.Child = null;
            _right.SetFolders(_folderStore.Folders, preferredFolderRoot);
            SynchronizeEffectiveFolderSelection();
        }
        else
        {
            PageInstanceSelectLeft left = _left
                ?? throw new InvalidOperationException("Classic layout requires left select page.");
            left.SetFolders(_folderStore.Folders, preferredFolderRoot);
            if (!ReferenceEquals(leftHost.Child, left))
            {
                if (leftHost.Child is MyPageLeft previousLeft)
                    previousLeft.TriggerHideAnimation();
                leftHost.Child = left;
            }

            left.TriggerShowAnimation();
        }

        if (!ReferenceEquals(rightHost.Child, _right))
        {
            if (rightHost.Child is MyPageRight oldRight)
                oldRight.PageOnExit();
            rightHost.Child = _right;
        }

        rightHost.Opacity = 1d;
        _right.PageOnEnter();
    }

    public void RefreshFolderLists()
    {
        if (_left is not null)
            _left.SetFolders(_folderStore.Folders, _folderStore.SelectedRoot);
        if (_right is { IsFullPageLayout: true })
        {
            _right.SetFolders(_folderStore.Folders, _folderStore.SelectedRoot);
            SynchronizeEffectiveFolderSelection();
        }
    }

    public void SetInstances(IReadOnlyList<LaunchInstanceInfo> instances, LaunchInstanceInfo? selected) =>
        _right?.SetInstances(instances, selected);

    internal static string? ResolvePreferredFolderRoot(
        LaunchInstanceInfo? selectedInstance,
        IReadOnlyList<MinecraftFolderInfo> folders,
        string? savedRoot)
    {
        string? instanceRoot =
            SessionPath.TryGetMinecraftRootFromInstanceDirectory(selectedInstance?.InstanceDirectory);
        if (instanceRoot is not null)
        {
            MinecraftFolderInfo? match = folders.FirstOrDefault(folder =>
                string.Equals(
                    SessionPath.NormalizeDirectory(folder.RootDirectory),
                    instanceRoot,
                    StringComparison.OrdinalIgnoreCase));
            string? matchedRoot = SessionPath.NormalizeDirectory(match?.RootDirectory);
            if (matchedRoot is not null && Directory.Exists(matchedRoot))
                return matchedRoot;
        }

        return SessionPath.NormalizeDirectory(savedRoot) ?? savedRoot;
    }

    private void SynchronizeEffectiveFolderSelection()
    {
        if (_right?.SelectedRootDirectory is not { Length: > 0 } effectiveRoot ||
            string.Equals(
                _folderStore.SelectedRoot,
                effectiveRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _folderStore.SetSelectedRootWithoutPersist(effectiveRoot);
    }

    private PageInstanceSelectLeft CreateLeftPage(InstancesSelectBindings b)
    {
        PageInstanceSelectLeft page = new();
        WireFolderRail(page, b);
        return page;
    }

    private PageInstanceSelectRight CreateRightPage(InstancesSelectBindings b)
    {
        PageInstanceSelectRight page = new();
        WireFolderRail(page, b);
        page.RefreshRequested += async (_, _) =>
        {
            IReadOnlyList<LaunchInstanceInfo> instances = await b.RefreshInstancesAsync().ConfigureAwait(true);
            page.SetInstances(instances, b.GetSelectedInstance());
        };
        page.DownloadRequested += (_, _) => b.NavigateDownload();
        page.InstanceOpenFolderRequested += (_, instance) => b.OpenInstanceFolder(instance);
        page.InstanceDeleteRequested += (_, instance) => b.DeleteInstance(instance);
        page.InstanceSelected += (_, instance) => b.SelectInstance(instance);
        page.InstanceManageRequested += (_, instance) => b.ManageInstance(instance);
        return page;
    }

    private void WireFolderRail(PageInstanceSelectLeft page, InstancesSelectBindings b)
    {
        page.FolderSelected += (_, folder) => _ = b.SelectFolderAsync(folder, false);
        page.FolderRefreshRequested += (_, folder) => _ = b.SelectFolderAsync(folder, true);
        page.FolderOpenRequested += (_, folder) => b.OpenPath(folder.RootDirectory);
        page.FolderRenameRequested += (_, folder) => b.PromptRenameFolder(folder);
        page.FolderRemoveRequested += (_, folder) =>
        {
            b.RemoveFolder(folder);
            RefreshFolderLists();
        };
        page.CreateFolderRequested += async (_, _) =>
        {
            await b.CreateDefaultFolderAsync().ConfigureAwait(true);
            RefreshFolderLists();
        };
        page.AddFolderRequested += async (_, _) =>
        {
            await b.AddFolderAsync().ConfigureAwait(true);
            RefreshFolderLists();
        };
        page.ImportModpackRequested += (_, _) => _ = b.ImportModpackAsync();
    }

    private void WireFolderRail(PageInstanceSelectRight page, InstancesSelectBindings b)
    {
        page.FolderSelected += (_, folder) => _ = b.SelectFolderAsync(folder, false);
        page.FolderRefreshRequested += (_, folder) => _ = b.SelectFolderAsync(folder, true);
        page.FolderOpenRequested += (_, folder) => b.OpenPath(folder.RootDirectory);
        page.FolderRenameRequested += (_, folder) => b.PromptRenameFolder(folder);
        page.FolderRemoveRequested += (_, folder) =>
        {
            b.RemoveFolder(folder);
            RefreshFolderLists();
        };
        page.CreateFolderRequested += async (_, _) =>
        {
            await b.CreateDefaultFolderAsync().ConfigureAwait(true);
            RefreshFolderLists();
        };
        page.AddFolderRequested += async (_, _) =>
        {
            await b.AddFolderAsync().ConfigureAwait(true);
            RefreshFolderLists();
        };
        page.ImportModpackRequested += (_, _) => _ = b.ImportModpackAsync();
    }
}
