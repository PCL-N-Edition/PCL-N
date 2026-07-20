// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using PCL.Desktop.Composition;
using PCL.Desktop.Features.Instances.Views;
using PCL.Desktop.Messaging;
using PCL.Desktop.Features;
using PCL.Desktop.Features.Instances;
using PCL.Desktop.Features.Community;
using PCL.Desktop.Features.Downloads;
using PCL.Desktop.Features.Launching;
using PCL.Desktop.Features.Settings;
using PCL.Desktop.Features.Tasks;
using PCL.Desktop.Session;
using PCL.Desktop.Shell;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class DesktopCompositionTests
{
    [TestInitialize]
    public void Init()
    {
        DesktopCompositionRoot.ResetForTests();
        DesktopCompositionRoot.Initialize();
    }

    [TestCleanup]
    public void Cleanup() => DesktopCompositionRoot.ResetForTests();

    [TestMethod]
    public void CompositionRoot_ResolvesShellViewModels()
    {

        AppShellViewModel shell = DesktopCompositionRoot.GetRequiredService<AppShellViewModel>();
        TitleBarViewModel title = DesktopCompositionRoot.GetRequiredService<TitleBarViewModel>();
        ExtraDockViewModel dock = DesktopCompositionRoot.GetRequiredService<ExtraDockViewModel>();
        IMessenger messenger = DesktopCompositionRoot.GetRequiredService<IMessenger>();

        Assert.IsNotNull(shell);
        Assert.IsNotNull(title);
        Assert.IsNotNull(dock);
        Assert.IsNotNull(messenger);
        Assert.AreSame(shell, DesktopCompositionRoot.GetRequiredService<AppShellViewModel>());
    }

    [TestMethod]
    public void CompositionRoot_RegistersLaunchAndInstancesFeatureModules()
    {
        IReadOnlyList<IDesktopFeatureModule> modules =
            DesktopCompositionRoot.GetRequiredService<IReadOnlyList<IDesktopFeatureModule>>();
        Assert.IsTrue(modules.Any(m => m.Id == "launch"));
        Assert.IsTrue(modules.Any(m => m.Id == "instances"));
        Assert.IsTrue(modules.Any(m => m.Id == "download"));
        Assert.IsTrue(modules.Any(m => m.Id == "settings"));
        Assert.IsTrue(modules.Any(m => m.Id == "community"));
        Assert.IsTrue(modules.Any(m => m.Id == "tasks"));

        InstancesSelectSurface select = DesktopCompositionRoot.GetRequiredService<InstancesSelectSurface>();
        LaunchHomeProfileResolver launchProfile =
            DesktopCompositionRoot.GetRequiredService<LaunchHomeProfileResolver>();
        LaunchHomeSurface launchHome = DesktopCompositionRoot.GetRequiredService<LaunchHomeSurface>();
        StartMinecraftUseCase startMinecraft = DesktopCompositionRoot.GetRequiredService<StartMinecraftUseCase>();
        DownloadFeatureSurface download = DesktopCompositionRoot.GetRequiredService<DownloadFeatureSurface>();
        SettingsFeatureSurface settings = DesktopCompositionRoot.GetRequiredService<SettingsFeatureSurface>();
        CommunityFeatureSurface community = DesktopCompositionRoot.GetRequiredService<CommunityFeatureSurface>();
        TaskManagerSurface tasks = DesktopCompositionRoot.GetRequiredService<TaskManagerSurface>();
        InstancesManageSurface manage = DesktopCompositionRoot.GetRequiredService<InstancesManageSurface>();
        LaunchLoginSurface login = DesktopCompositionRoot.GetRequiredService<LaunchLoginSurface>();
        Assert.IsNotNull(select);
        Assert.IsNotNull(launchProfile);
        Assert.IsNotNull(launchHome);
        Assert.IsNotNull(startMinecraft);
        Assert.IsNotNull(download);
        Assert.IsNotNull(settings);
        Assert.IsNotNull(community);
        Assert.IsNotNull(tasks);
        Assert.IsNotNull(manage);
        Assert.IsNotNull(login);
        Assert.AreEqual("instances.select", InstancesSelectSurface.SubPageId);
    }

    [TestMethod]
    public void StartMinecraftUseCase_RequiresBindBeforeExecute()
    {
        // Fresh instance — do not bind the process-wide singleton used by other tests.
        StartMinecraftUseCase useCase = new();
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            useCase.ExecuteAsync(new StartMinecraftRequest(
                Home: null!,
                Instance: null!)).GetAwaiter().GetResult());

        Assert.ThrowsExactly<ArgumentNullException>(() =>
            useCase.ExecuteAsync(null!).GetAwaiter().GetResult());
    }

    [TestMethod]
    public void ExtraDockViewModel_GlassDockRequiresVisibleButton()
    {
        ExtraDockViewModel dock = DesktopCompositionRoot.GetRequiredService<ExtraDockViewModel>();

        dock.UseGlassChrome = true;
        Assert.IsFalse(dock.ShouldShowGlassDock);

        dock.SetBackToTopVisible(true);
        Assert.IsTrue(dock.ShouldShowGlassDock);

        dock.SetBackToTopVisible(false);
        Assert.IsFalse(dock.ShouldShowGlassDock);
    }

    [TestMethod]
    public void Messenger_GameRunningUpdatesExtraDock()
    {
        // Use a private messenger so shared WeakReferenceMessenger.Default cannot
        // wake a live MainWindow handler from another headless test.
        WeakReferenceMessenger localMessenger = new();
        ServiceCollection services = new();
        services.AddSingleton<IMessenger>(localMessenger);
        DesktopCompositionRoot.ConfigureCoreServices(services);
        // Re-bind messenger after ConfigureCoreServices so we own the bus.
        services.AddSingleton<IMessenger>(localMessenger);
        DesktopCompositionRoot.ResetForTests();
        DesktopCompositionRoot.InitializeForTests(services.BuildServiceProvider());

        ExtraDockViewModel dock = DesktopCompositionRoot.GetRequiredService<ExtraDockViewModel>();

        localMessenger.Send(new GameRunningChangedMessage(true));
        Assert.IsTrue(dock.ShowShutdown);
        Assert.IsTrue(dock.ShowGameLog);

        localMessenger.Send(new GameRunningChangedMessage(false));
        Assert.IsFalse(dock.ShowShutdown);
        Assert.IsFalse(dock.ShowGameLog);
    }

    [TestMethod]
    public void MinecraftFolderStore_AddRenameRemoveAndSelection()
    {
        // Isolated store — do not pollute the process-wide composition root used by MainWindow tests.
        MinecraftFolderStore store = new(new WeakReferenceMessenger());
        store.EnsureLoaded();
        Assert.IsTrue(store.Folders.Count >= 1);

        string tempRoot = Path.Combine(Path.GetTempPath(), "pcl-folder-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            MinecraftFolderInfo added = store.AddOrGet(tempRoot, "Temp Custom", isCustom: true);
            Assert.IsTrue(added.IsCustom);
            Assert.IsTrue(store.ContainsRoot(added.RootDirectory));

            Assert.IsTrue(store.TryRename(added, "Renamed"));
            Assert.IsTrue(store.Folders.Any(f => f.Name == "Renamed"));

            Assert.IsTrue(store.TrySetSelectedRoot(added.RootDirectory));
            Assert.AreEqual(
                SessionPath.NormalizeDirectory(tempRoot),
                store.SelectedRoot);

            MinecraftFolderInfo? next = store.Remove(added);
            // Removing selected folder returns a replacement selection.
            Assert.IsNotNull(next);
            Assert.IsFalse(store.ContainsRoot(added.RootDirectory));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [TestMethod]
    public void TaskAndGameSessionStores_PublishDockMessages()
    {
        WeakReferenceMessenger messenger = new();
        TaskSessionStore tasks = new(messenger);
        GameSessionStore games = new(messenger);
        AppShellViewModel shell = new(messenger, new ExperimentalUiProfileSource(messenger));
        ExtraDockViewModel dock = new(messenger, shell);

        tasks.Upsert("t1", new Features.Tasks.Views.TaskManagerEntrySnapshot(
            "t1", "title", "stage", "detail", 0.5d, 1, 2, 0,
            Features.Tasks.Views.TaskManagerTaskState.Running));
        Assert.IsTrue(tasks.HasActiveTask);
        Assert.IsTrue(dock.ShowTaskManager);

        games.SetRunning(null);
        Assert.IsFalse(games.IsRunning);
        Assert.IsFalse(dock.ShowShutdown);
    }

    [TestMethod]
    public void ShellViewModels_DoNotReferenceAvaloniaAssemblies()
    {
        string[] viewModelAssemblies =
        [
            typeof(AppShellViewModel).Assembly.GetName().Name!,
        ];

        // Source-level: Shell + Messaging types under ViewModel namespaces must not import Avalonia.
        string repoRoot = FindRepositoryRoot();
        string[] roots =
        [
            Path.Combine(repoRoot, "PCL.Desktop", "Shell"),
            Path.Combine(repoRoot, "PCL.Desktop", "Messaging"),
            Path.Combine(repoRoot, "PCL.Desktop", "Composition")
        ];

        List<string> violations = [];
        foreach (string root in roots)
        {
            if (!Directory.Exists(root))
                continue;
            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                if (text.Contains("using Avalonia", StringComparison.Ordinal) ||
                    text.Contains("Avalonia.", StringComparison.Ordinal) && text.Contains("using ", StringComparison.Ordinal))
                {
                    // Allow nothing Avalonia in shell VMs / messaging / composition.
                    if (text.Contains("using Avalonia", StringComparison.Ordinal))
                        violations.Add(Path.GetRelativePath(repoRoot, file));
                }
            }
        }

        Assert.AreEqual(
            0,
            violations.Count,
            "Shell/Messaging/Composition must stay Avalonia-free:" + Environment.NewLine +
            string.Join(Environment.NewLine, violations));
        Assert.IsTrue(viewModelAssemblies.Length > 0);
    }

    private static string FindRepositoryRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "PCL-N.slnx")) ||
                File.Exists(Path.Combine(dir, "PCL.Desktop", "PCL.Desktop.csproj")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
