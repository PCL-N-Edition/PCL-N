// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using PCL.Desktop.Features;
using PCL.Desktop.Features.Community;
using PCL.Desktop.Features.Downloads;
using PCL.Desktop.Features.Instances;
using PCL.Desktop.Features.Launching;
using PCL.Desktop.Features.Settings;
using PCL.Desktop.Features.Tasks;
using PCL.Desktop.Session;
using PCL.Desktop.Shell;

namespace PCL.Desktop.Composition;

/// <summary>
/// Avalonia desktop composition root. Shell + Session stores are singletons;
/// page ViewModels register as transient as Features migrate.
/// </summary>
public static class DesktopCompositionRoot
{
    private static IServiceProvider? _services;

    public static IServiceProvider Services =>
        _services ?? throw new InvalidOperationException("DesktopCompositionRoot 尚未初始化。");

    public static bool IsInitialized => _services is not null;

    public static void Initialize()
    {
        if (_services is not null)
            return;

        ServiceCollection services = new();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();
    }

    /// <summary>
    /// Test / headless seam: replace the root with an explicit provider.
    /// </summary>
    public static void InitializeForTests(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public static void ResetForTests() => _services = null;

    public static T GetRequiredService<T>() where T : notnull =>
        Services.GetRequiredService<T>();

    public static void ConfigureCoreServices(IServiceCollection services)
    {
        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);

        services.AddSingleton<MinecraftFolderStore>();
        services.AddSingleton<InstanceSelectionStore>();
        services.AddSingleton<TaskSessionStore>();
        services.AddSingleton<GameSessionStore>();

        services.AddSingleton<ExperimentalUiProfileSource>();
        services.AddSingleton<AppShellViewModel>();
        services.AddSingleton<TitleBarViewModel>();
        services.AddSingleton<ExtraDockViewModel>();

        // Feature modules (Phase 3–4)
        IDesktopFeatureModule[] modules =
        [
            new LaunchFeatureModule(),
            new InstancesFeatureModule(),
            new DownloadFeatureModule(),
            new SettingsFeatureModule(),
            new CommunityFeatureModule(),
            new TasksFeatureModule()
        ];
        foreach (IDesktopFeatureModule module in modules)
            module.Register(services);

        services.AddSingleton<IReadOnlyList<IDesktopFeatureModule>>(_ => modules);
    }

    private static void ConfigureServices(IServiceCollection services) =>
        ConfigureCoreServices(services);
}
