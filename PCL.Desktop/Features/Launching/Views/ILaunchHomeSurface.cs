// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;

namespace PCL.Desktop.Features.Launching.Views;

/// <summary>
/// Launch homepage surface shared by the classic rail layout and the experimental redesign.
/// </summary>
public interface ILaunchHomeSurface
{
    IReadOnlyList<LaunchInstanceInfo> Instances { get; }

    LaunchInstanceInfo? SelectedInstance { get; }

    string? PreferredInstanceDirectory { get; }

    string? MinecraftRootDirectory { get; }

    Control? CurrentLoginPage { get; }

    PageLaunchLeft.LaunchLoginPageType CurrentLoginPageType { get; }

    bool HasSelectedProfile { get; }

    bool IsLaunchInProgress { get; }

    double DisplayedLaunchProgress { get; }

    Func<bool>? CanLaunchByPageState { get; set; }

    event EventHandler? InstanceSelectRequested;

    event EventHandler? InstanceSettingsRequested;

    event EventHandler? DownloadRequested;

    event EventHandler<LaunchInstanceInfo>? LaunchRequested;

    event EventHandler? CancelLaunchRequested;

    event EventHandler<string>? StatusMessage;

    event EventHandler<PageLaunchLeft.LaunchLoginPageType>? LoginPageRequested;

    Task EnsureInstancesLoadedAsync();

    Task RefreshInstancesAsync();

    void SetInstances(IReadOnlyList<LaunchInstanceInfo> instances, LaunchInstanceInfo? selectedInstance = null);

    void SetPreferredInstanceDirectory(string? instanceDirectory);

    void SetMinecraftRootDirectory(string? minecraftRootDirectory);

    void SetInstanceLoading(bool isLoading);

    void SetSelectedProfilePresent(bool hasSelectedProfile);

    void SetLoginPage(
        Control page,
        bool animate,
        PageLaunchLeft.LaunchLoginPageType pageType = PageLaunchLeft.LaunchLoginPageType.None);

    void PageChangeToLogin();

    void ConfigureLaunchingHint(bool isEnabled);

    void ShowLaunching(LaunchInstanceInfo? instance);

    void ShowRepairing();

    void UpdateRepairStep(int current, int total);

    void ShowRepairWorkflow(
        string title,
        string stage,
        double progress,
        string? method = null,
        LaunchInstanceInfo? instance = null);

    void HideRepairing();

    void UpdateLaunchingStatus(string stage, double progress, string? method = null);

    void LaunchingRefresh(
        string stage,
        double actualProgress,
        bool isLaunched = false,
        string? method = null,
        string? downloadSpeed = null);

    void RefreshButtonsUI();

    void LaunchButtonClick();

    void RefreshPage(bool anim, PageLaunchLeft.LaunchLoginPageType targetLoginType = PageLaunchLeft.LaunchLoginPageType.None);

    void TriggerEnterAnimation();
}
