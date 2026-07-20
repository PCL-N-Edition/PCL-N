// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Instances;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Features.Instances.Views;
using PCL.Desktop.Features.Launching.Views;

namespace PCL.Desktop.Features.Instances;

/// <summary>
/// Host-scoped cache of version-manage left rail and right sub-pages.
/// Host supplies interaction bindings; surface owns page lifetime.
/// </summary>
public sealed class InstancesManageSurface
{
    private object? _hostToken;
    private InstancesManageBindings? _bindings;
    private PageInstanceLeft? _left;
    private PageInstanceManageRight? _managePage;
    private PageInstanceSetupRight? _setupPage;
    private PageInstanceExportRight? _exportPage;
    private PageInstanceInstallRight? _installPage;
    private PageInstanceSavesRight? _savesPage;
    private PageInstanceSavesInfoRight? _savesInfoPage;
    private PageInstanceScreenshotRight? _screenshotPage;
    private PageInstanceToolsRight? _toolsPage;
    private PageInstanceModDisabledRight? _modDisabledPage;
    private PageInstanceResourceRight? _resourcePage;
    private PageInstanceResourceRight? _datapackPage;
    private PageInstanceServerRight? _serverPage;

    public LaunchInstanceInfo? ManagedInstance { get; private set; }

    public PageInstanceLeft? Left => _left;

    public PageInstanceManageRight? ManagePage => _managePage;

    public PageInstanceSetupRight? SetupPage => _setupPage;

    public PageInstanceExportRight? ExportPage => _exportPage;

    public PageInstanceInstallRight? InstallPage => _installPage;

    public PageInstanceSavesRight? SavesPage => _savesPage;

    public PageInstanceSavesInfoRight? SavesInfoPage => _savesInfoPage;

    public PageInstanceScreenshotRight? ScreenshotPage => _screenshotPage;

    public PageInstanceToolsRight? ToolsPage => _toolsPage;

    public PageInstanceModDisabledRight? ModDisabledPage => _modDisabledPage;

    public PageInstanceResourceRight? ResourcePage => _resourcePage;

    public PageInstanceResourceRight? DatapackPage => _datapackPage;

    public PageInstanceServerRight? ServerPage => _serverPage;

    public void WireOnce(object hostToken, InstancesManageBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(hostToken);
        ArgumentNullException.ThrowIfNull(bindings);
        if (!ReferenceEquals(_hostToken, hostToken))
        {
            _hostToken = hostToken;
            ClearPages();
            ManagedInstance = null;
        }

        _bindings = bindings;
    }

    /// <summary>
    /// Prepare left/right pages for the given instance sub-page (does not mount hosts).
    /// </summary>
    public (PageInstanceLeft Left, MyPageRight Right, InstancePageSubType SubPage) Prepare(
        LaunchInstanceInfo instance,
        InstancePageSubType subPage)
    {
        ArgumentNullException.ThrowIfNull(instance);
        _ = RequireBindings();

        ManagedInstance = instance;
        PageInstanceLeft left = EnsureLeft();
        left.SetInstance(instance);
        subPage = left.NormalizePage(subPage);
        left.SelectPage(subPage);
        MyPageRight right = GetRightPage(instance, subPage);
        return (left, right, subPage);
    }

    public PageInstanceSavesInfoRight EnsureSavesInfoPage()
    {
        InstancesManageBindings b = RequireBindings();
        if (_savesInfoPage is not null)
            return _savesInfoPage;

        PageInstanceSavesInfoRight page = new();
        page.StatusMessage += (_, message) => b.StatusMessage(message);
        page.DatapackManageRequested += (_, saveFolder) => b.ShowDatapacks(saveFolder);
        _savesInfoPage = page;
        return page;
    }

    public PageInstanceResourceRight EnsureDatapackPage()
    {
        InstancesManageBindings b = RequireBindings();
        if (_datapackPage is not null)
            return _datapackPage;

        PageInstanceResourceRight page = new();
        page.OpenFolderRequested += (_, path) => b.OpenPath(path);
        page.DownloadRequested += (_, _) => b.OpenCommunityDataPacks();
        page.StatusMessage += (_, message) =>
        {
            b.StatusMessage(message);
            b.ShowHint(message);
        };
        _datapackPage = page;
        return page;
    }

    public void RefreshSubPage(InstancePageSubType subPage)
    {
        if (subPage == InstancePageSubType.Overall)
            _ = RequireBindings().RefreshInstancesAsync(ManagedInstance?.InstanceDirectory);
        else if (subPage == InstancePageSubType.Servers)
            _serverPage?.Reload();
        else if (subPage == InstancePageSubType.Export)
            _exportPage?.RefreshAll();
        else if (subPage == InstancePageSubType.Install)
            _installPage?.RefreshAll();
        else if (subPage == InstancePageSubType.Saves)
            _savesPage?.Reload();
        else if (subPage == InstancePageSubType.Screenshots)
            _ = _screenshotPage?.Reload();
        else if (subPage is InstancePageSubType.Mods or InstancePageSubType.ResourcePacks
                 or InstancePageSubType.Shaders or InstancePageSubType.Schematics)
            _resourcePage?.Reload();
        else
            _toolsPage?.Reload();
    }

    private void ClearPages()
    {
        _left = null;
        _managePage = null;
        _setupPage = null;
        _exportPage = null;
        _installPage = null;
        _savesPage = null;
        _savesInfoPage = null;
        _screenshotPage = null;
        _toolsPage = null;
        _modDisabledPage = null;
        _resourcePage = null;
        _datapackPage = null;
        _serverPage = null;
    }

    private InstancesManageBindings RequireBindings() =>
        _bindings ?? throw new InvalidOperationException("InstancesManageSurface 尚未 WireOnce。");

    private PageInstanceLeft EnsureLeft()
    {
        InstancesManageBindings b = RequireBindings();
        if (_left is not null)
            return _left;

        PageInstanceLeft page = new();
        page.PageChanged += (_, subPage) =>
        {
            if (ManagedInstance is not null)
                b.SelectSubPage(ManagedInstance, subPage);
        };
        page.RefreshRequested += (_, subPage) => RefreshSubPage(subPage);
        page.ResetRequested += (_, _) =>
        {
            if (ManagedInstance is not null)
                b.ResetSettings(ManagedInstance);
        };
        _left = page;
        return page;
    }

    private MyPageRight GetRightPage(LaunchInstanceInfo instance, InstancePageSubType subPage)
    {
        if (subPage == InstancePageSubType.Overall)
        {
            PageInstanceManageRight page = EnsureManagePage();
            page.SetInstance(instance);
            return page;
        }

        if (subPage == InstancePageSubType.Servers)
        {
            PageInstanceServerRight page = EnsureServerPage();
            page.SetInstance(instance);
            return page;
        }

        if (subPage == InstancePageSubType.Setup)
        {
            PageInstanceSetupRight page = EnsureSetupPage();
            page.SetInstance(instance);
            return page;
        }

        if (subPage == InstancePageSubType.Export)
        {
            PageInstanceExportRight page = EnsureExportPage();
            page.SetInstance(instance);
            return page;
        }

        if (subPage == InstancePageSubType.Install)
        {
            PageInstanceInstallRight page = EnsureInstallPage();
            page.SetInstance(instance);
            return page;
        }

        if (subPage == InstancePageSubType.Screenshots)
        {
            PageInstanceScreenshotRight page = EnsureScreenshotPage();
            page.SetInstance(instance);
            return page;
        }

        if (subPage == InstancePageSubType.Saves)
        {
            PageInstanceSavesRight page = EnsureSavesPage();
            page.SetInstance(instance);
            return page;
        }

        if (subPage == InstancePageSubType.ModsDisabled)
            return EnsureModDisabledPage();

        if (subPage is InstancePageSubType.Mods or InstancePageSubType.ResourcePacks
            or InstancePageSubType.Shaders or InstancePageSubType.Schematics)
        {
            PageInstanceResourceRight page = EnsureResourcePage();
            page.SetContext(instance, subPage);
            return page;
        }

        PageInstanceToolsRight tools = EnsureToolsPage();
        tools.SetContext(instance, subPage);
        return tools;
    }

    private PageInstanceManageRight EnsureManagePage()
    {
        InstancesManageBindings b = RequireBindings();
        if (_managePage is not null)
            return _managePage;

        PageInstanceManageRight page = new();
        page.OpenFolderRequested += (_, instance) => b.OpenPath(instance.InstanceDirectory);
        page.OpenPathRequested += (_, path) => b.OpenPath(path);
        page.RenameRequested += (_, instance) => b.RenameInstance(instance);
        page.DeleteRequested += (_, instance) => b.DeleteInstance(instance);
        page.EditDescriptionRequested += (_, instance) => b.EditDescription(instance);
        page.ToggleStarRequested += (_, instance) => _ = b.ToggleStarAsync(instance);
        page.ExportLaunchScriptRequested += (_, instance) => _ = b.ExportLaunchScriptAsync(instance);
        page.TestLaunchRequested += (_, instance) => _ = b.TestLaunchAsync(instance);
        page.RepairFilesRequested += (_, instance) => _ = b.RepairFilesAsync(instance);
        page.ResetSettingsRequested += (_, instance) => b.ResetSettings(instance);
        page.PatchCoreRequested += (_, instance) => _ = b.PatchCoreAsync(instance);
        _managePage = page;
        return page;
    }

    private PageInstanceSetupRight EnsureSetupPage()
    {
        InstancesManageBindings b = RequireBindings();
        if (_setupPage is not null)
            return _setupPage;

        PageInstanceSetupRight page = new();
        page.OpenGlobalSettingsRequested += (_, _) => b.OpenGlobalSettings();
        page.MessageRequested += (_, args) => b.ShowMessage(args.Title, args.Message, args.PrimaryButton);
        page.ConfirmRequested += (_, args) => b.Confirm(
            args.Title,
            args.Message,
            args.Complete,
            args.PrimaryButton,
            args.SecondaryButton,
            args.IsWarn);
        page.CreateAuthProfileRequested += (_, authServer) => b.CreateAuthProfile(authServer);
        _setupPage = page;
        return page;
    }

    private PageInstanceExportRight EnsureExportPage()
    {
        InstancesManageBindings b = RequireBindings();
        if (_exportPage is not null)
            return _exportPage;

        PageInstanceExportRight page = new();
        page.ExportRequested += (_, request) => _ = b.ExportZipAsync(request);
        page.ImportConfigRequested += (_, _) => _ = b.ImportExportConfigAsync(page);
        page.ExportConfigRequested += (_, rules) => _ = b.ExportExportConfigAsync(rules);
        _exportPage = page;
        return page;
    }

    private PageInstanceInstallRight EnsureInstallPage()
    {
        InstancesManageBindings b = RequireBindings();
        if (_installPage is not null)
            return _installPage;

        PageInstanceInstallRight page = new();
        page.ModifyRequested += (_, request) => _ = b.OpenDownloadInstallAsync(request);
        _installPage = page;
        return page;
    }

    private PageInstanceScreenshotRight EnsureScreenshotPage()
    {
        InstancesManageBindings b = RequireBindings();
        if (_screenshotPage is not null)
            return _screenshotPage;

        PageInstanceScreenshotRight page = new();
        page.OpenFolderRequested += (_, path) => b.OpenPath(path);
        page.OpenFileRequested += (_, path) => b.OpenExistingPath(path);
        page.StatusMessage += (_, message) => b.StatusMessage(message);
        _screenshotPage = page;
        return page;
    }

    private PageInstanceSavesRight EnsureSavesPage()
    {
        InstancesManageBindings b = RequireBindings();
        if (_savesPage is not null)
            return _savesPage;

        PageInstanceSavesRight page = new();
        page.OpenFolderRequested += (_, path) => b.OpenPath(path);
        page.SaveDetailsRequested += (_, path) => _ = b.ShowSaveDetailsAsync(path);
        page.QuickPlayRequested += (_, worldName) => b.QuickPlayWorld(worldName);
        page.StatusMessage += (_, message) => b.StatusMessage(message);
        _savesPage = page;
        return page;
    }

    private PageInstanceToolsRight EnsureToolsPage()
    {
        InstancesManageBindings b = RequireBindings();
        if (_toolsPage is not null)
            return _toolsPage;

        PageInstanceToolsRight page = new();
        page.OpenFolderRequested += (_, path) => b.OpenPath(path);
        _toolsPage = page;
        return page;
    }

    private PageInstanceModDisabledRight EnsureModDisabledPage()
    {
        InstancesManageBindings b = RequireBindings();
        if (_modDisabledPage is not null)
            return _modDisabledPage;

        PageInstanceModDisabledRight page = new();
        page.DownloadRequested += (_, _) => b.NavigateDownload();
        page.InstanceSelectRequested += (_, _) => b.NavigateInstanceSelect();
        _modDisabledPage = page;
        return page;
    }

    private PageInstanceResourceRight EnsureResourcePage()
    {
        InstancesManageBindings b = RequireBindings();
        if (_resourcePage is not null)
            return _resourcePage;

        PageInstanceResourceRight page = new();
        page.OpenFolderRequested += (_, path) => b.OpenPath(path);
        page.DownloadRequested += (_, subPage) => b.OpenCommunityForResource(subPage);
        page.StatusMessage += (_, message) =>
        {
            b.StatusMessage(message);
            b.ShowHint(message);
        };
        _resourcePage = page;
        return page;
    }

    private PageInstanceServerRight EnsureServerPage()
    {
        InstancesManageBindings b = RequireBindings();
        if (_serverPage is not null)
            return _serverPage;

        PageInstanceServerRight page = new();
        page.RefreshRequested += (_, _) => page.Reload();
        page.AddServerRequested += (_, instance) => b.AddServer(instance, page);
        page.ConnectServerRequested += (_, server) => b.ConnectServer(server);
        page.EditServerRequested += (_, server) => b.EditServer(page, server);
        page.RemoveServerRequested += (_, server) => b.RemoveServer(page, server);
        _serverPage = page;
        return page;
    }
}

/// <summary>Host callbacks for version-manage pages.</summary>
public sealed class InstancesManageBindings
{
    public required Action<LaunchInstanceInfo, InstancePageSubType> SelectSubPage { get; init; }

    public required Func<string?, Task> RefreshInstancesAsync { get; init; }

    public required Action<LaunchInstanceInfo> ResetSettings { get; init; }

    public required Action<string> OpenPath { get; init; }

    public required Action<string> OpenExistingPath { get; init; }

    public required Action<string> StatusMessage { get; init; }

    public required Action<string> ShowHint { get; init; }

    public required Action<LaunchInstanceInfo> RenameInstance { get; init; }

    public required Action<LaunchInstanceInfo> DeleteInstance { get; init; }

    public required Action<LaunchInstanceInfo> EditDescription { get; init; }

    public required Func<LaunchInstanceInfo, Task> ToggleStarAsync { get; init; }

    public required Func<LaunchInstanceInfo, Task> ExportLaunchScriptAsync { get; init; }

    public required Func<LaunchInstanceInfo, Task> TestLaunchAsync { get; init; }

    public required Func<LaunchInstanceInfo, Task> RepairFilesAsync { get; init; }

    public required Func<LaunchInstanceInfo, Task> PatchCoreAsync { get; init; }

    public required Action OpenGlobalSettings { get; init; }

    public required Action<string, string, string?> ShowMessage { get; init; }

    public required Action<string, string, Action<bool>, string?, string?, bool> Confirm { get; init; }

    public required Action<string> CreateAuthProfile { get; init; }

    public required Func<InstanceExportPageRequest, Task> ExportZipAsync { get; init; }

    public required Func<PageInstanceExportRight, Task> ImportExportConfigAsync { get; init; }

    public required Func<IReadOnlyList<string>, Task> ExportExportConfigAsync { get; init; }

    public required Func<InstanceInstallModifyRequest, Task> OpenDownloadInstallAsync { get; init; }

    public required Func<string, Task> ShowSaveDetailsAsync { get; init; }

    public required Action<string> QuickPlayWorld { get; init; }

    public required Action NavigateDownload { get; init; }

    public required Action NavigateInstanceSelect { get; init; }

    public required Action<InstancePageSubType> OpenCommunityForResource { get; init; }

    public required Action OpenCommunityDataPacks { get; init; }

    public required Action<string> ShowDatapacks { get; init; }

    public required Action<LaunchInstanceInfo, PageInstanceServerRight> AddServer { get; init; }

    public required Action<MinecraftServerEntry> ConnectServer { get; init; }

    public required Action<PageInstanceServerRight, MinecraftServerEntry> EditServer { get; init; }

    public required Action<PageInstanceServerRight, MinecraftServerEntry> RemoveServer { get; init; }
}
