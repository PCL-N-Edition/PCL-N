// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using CommunityToolkit.Mvvm.Messaging;
using PCL.Desktop.Composition;
using PCL.Desktop.Messaging;
using PCL.Desktop.Shell;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class DesktopCompositionTests
{
    [TestCleanup]
    public void Cleanup() => DesktopCompositionRoot.ResetForTests();

    [TestMethod]
    public void CompositionRoot_ResolvesShellViewModels()
    {
        DesktopCompositionRoot.ResetForTests();
        DesktopCompositionRoot.Initialize();

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
    public void ExtraDockViewModel_GlassDockRequiresVisibleButton()
    {
        DesktopCompositionRoot.ResetForTests();
        DesktopCompositionRoot.Initialize();
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
        DesktopCompositionRoot.ResetForTests();
        DesktopCompositionRoot.Initialize();
        ExtraDockViewModel dock = DesktopCompositionRoot.GetRequiredService<ExtraDockViewModel>();
        IMessenger messenger = DesktopCompositionRoot.GetRequiredService<IMessenger>();

        messenger.Send(new GameRunningChangedMessage(true));
        Assert.IsTrue(dock.ShowShutdown);
        Assert.IsTrue(dock.ShowGameLog);

        messenger.Send(new GameRunningChangedMessage(false));
        Assert.IsFalse(dock.ShowShutdown);
        Assert.IsFalse(dock.ShowGameLog);
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
