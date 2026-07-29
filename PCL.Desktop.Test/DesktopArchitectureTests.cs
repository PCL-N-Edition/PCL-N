// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Desktop;
using PCL.Desktop.Localization;
using PCL.Desktop.Theme;
using PCL.Core.App;
using PCL.Core.Platform;
using Avalonia.Media;
using PCL.Application.Settings;
using PCL.Desktop.Features.Community;
using System.Globalization;
using System.Xml.Linq;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class DesktopArchitectureTests
{
    [TestMethod]
    public void DesktopShellSuppressionUsesDedicatedTestSwitchOnly()
    {
        Assert.IsFalse(App.ShouldSkipDesktopShell(key =>
            key is "PCL_DISABLE_FIRST_RUN" or "PCL_DISABLE_DEBUG_HINT" ? "1" : null));
        Assert.IsTrue(App.ShouldSkipDesktopShell(key =>
            key == "PCL_DISABLE_DESKTOP_SHELL" ? "1" : null));
    }

    [TestMethod]
    public void HostLocalizationKeepsLegacyFallbacksResolvable()
    {
        string originalLanguage = AvaloniaLocalizationManager.CurrentLanguageCode;
        try
        {
            AvaloniaLocalizationManager.Apply(AvaloniaLocalizationManager.EnglishLanguage, AvaloniaLocalizationManager.FollowLanguage);
            Assert.AreEqual("Account", AvaloniaLocalizationManager.GetTextOrFallback("账户"));
            Assert.AreEqual("Third-party Custom Page", AvaloniaLocalizationManager.GetTextOrFallback("Third-party Custom Page"));
        }
        finally
        {
            AvaloniaLocalizationManager.Apply(originalLanguage, AvaloniaLocalizationManager.FollowLanguage);
        }
    }
    [TestMethod]
    public void SystemAccentAndCustomColorsProduceDistinctPalettes()
    {
        Color accent = Color.Parse("#E04080");
        IReadOnlyDictionary<string, Color> accentPalette =
            ThemeColorPalette.Create(false, ColorTheme.SystemAccent, accent);
        IReadOnlyDictionary<string, Color> fallbackPalette =
            ThemeColorPalette.Create(false, ColorTheme.CatBlue);
        IReadOnlyDictionary<string, Color> customPalette =
            ThemeColorPalette.Create(false, ColorTheme.Custom, customColor: "#2BA84A");

        Assert.AreNotEqual(fallbackPalette["ColorObject2"], accentPalette["ColorObject2"]);
        Assert.AreNotEqual(fallbackPalette["ColorObject2"], customPalette["ColorObject2"]);
        Assert.IsTrue(ThemeColorPalette.TryParseColor("#FF2BA84A", out Color parsed));
        Assert.AreEqual(Color.Parse("#FF2BA84A"), parsed);
    }

    [TestMethod]
    public void AprilThemePolicyControlsVisibilityAndTemporaryDefault()
    {
        try
        {
            ThemeAvailabilityPolicy.Clock = static () => new DateTimeOffset(2026, 3, 31, 12, 0, 0, TimeSpan.Zero);
            Assert.IsFalse(ThemeAvailabilityPolicy.GetAvailableThemes().Contains(ColorTheme.HmclBlue));
            Assert.AreEqual(ColorTheme.CatBlue, ThemeAvailabilityPolicy.ResolveRuntimeTheme(ColorTheme.HmclBlue));

            ThemeAvailabilityPolicy.Clock = static () => new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);
            Assert.IsTrue(ThemeAvailabilityPolicy.GetAvailableThemes().Contains(ColorTheme.HmclBlue));
            Assert.AreEqual(ColorTheme.HmclBlue, ThemeAvailabilityPolicy.ResolveRuntimeTheme(ColorTheme.CatBlue));
            ThemeAvailabilityPolicy.MarkManualThemeSelection();
            Assert.AreEqual(ColorTheme.SkyBlue, ThemeAvailabilityPolicy.ResolveRuntimeTheme(ColorTheme.SkyBlue));

            ThemeAvailabilityPolicy.Clock = static () => new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero);
            Assert.IsFalse(ThemeAvailabilityPolicy.GetAvailableThemes().Contains(ColorTheme.HmclBlue));
            Assert.AreEqual(ColorTheme.CatBlue, ThemeAvailabilityPolicy.ResolveRuntimeTheme(ColorTheme.HmclBlue));
        }
        finally
        {
            ThemeAvailabilityPolicy.ResetSessionForTests();
        }
    }

    [TestMethod]
    public void ThemeAvailabilityHonorsPlatformColorPolicy()
    {
        IReadOnlyList<ColorTheme> themes = ThemeAvailabilityPolicy.GetAvailableThemes();

        Assert.AreEqual(
            PlatformFeaturePolicy.IsSystemAccentThemeSupported,
            themes.Contains(ColorTheme.SystemAccent));
        Assert.AreEqual(
            PlatformFeaturePolicy.IsCustomColorPaletteSupported,
            themes.Contains(ColorTheme.Custom));
    }

    [TestMethod]
    [DataRow("zh-TW", "zh-TW")]
    [DataRow("zh-HK", "zh-TW")]
    [DataRow("zh-MO", "zh-TW")]
    [DataRow("zh-Hant", "zh-TW")]
    [DataRow("zh-CN", "zh-CN")]
    [DataRow("en-GB", "en-US")]
    public void LocalizationResolvesSupportedLanguageFamilies(string cultureName, string expected)
    {
        CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
        Assert.AreEqual(expected, AvaloniaLocalizationManager.ResolveLanguageForCulture("auto", culture));
    }

    [TestMethod]
    public void LocalizationCatalogsHaveMatchingKeysWithoutMigrationHint()
    {
        string localizationRoot = Path.Combine(FindDesktopProjectRoot(), "Localization");
        HashSet<string> simplified = ReadLocalizationKeys(Path.Combine(localizationRoot, "zh-CN.xaml"));
        HashSet<string> traditional = ReadLocalizationKeys(Path.Combine(localizationRoot, "zh-TW.xaml"));
        HashSet<string> english = ReadLocalizationKeys(Path.Combine(localizationRoot, "en-US.xaml"));

        CollectionAssert.AreEquivalent(simplified.ToArray(), traditional.ToArray());
        CollectionAssert.AreEquivalent(simplified.ToArray(), english.ToArray());
        Assert.IsFalse(simplified.Contains("Setup.LauncherLanguage.Hint"));
    }

    private static HashSet<string> ReadLocalizationKeys(string path)
    {
        XDocument document = XDocument.Load(path);
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        return document.Root!.Elements()
            .Select(element => element.Attribute(xaml + "Key")?.Value)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Select(static key => key!)
            .ToHashSet(StringComparer.Ordinal);
    }

    [TestMethod]
    public void DesktopEnablesNativeWaylandBackend()
    {
        string desktopRoot = FindDesktopProjectRoot();
        string repositoryRoot = Directory.GetParent(desktopRoot)?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repository root.");
        string desktopProject = File.ReadAllText(Path.Combine(desktopRoot, "PCL.Desktop.csproj"));
        string testProject = File.ReadAllText(Path.Combine(repositoryRoot, "PCL.Desktop.Test", "PCL.Desktop.Test.csproj"));
        string program = File.ReadAllText(Path.Combine(desktopRoot, "Program.cs"));

        StringAssert.Contains(desktopProject, "Avalonia.Desktop\" Version=\"12.1.0\"");
        StringAssert.Contains(desktopProject, "Avalonia.Wayland\" Version=\"12.1.0\"");
        StringAssert.Contains(testProject, "Avalonia.Headless\" Version=\"12.1.0\"");
        StringAssert.Contains(program, "DesktopDisplayBackendSelector.ShouldUseWaylandForCurrentProcess()");
        StringAssert.Contains(program, "builder = builder.UseWayland()");
    }

    private static readonly string[] ForbiddenAssemblyNames =
    [
        "PresentationCore",
        "PresentationFramework",
        "System.Management",
        "WindowsBase",
        "PCL.Core",
        "PCL.Plugin",
        "PCL.Online",
        "PCL.Online.Windows",
        "Plain Craft Launcher 2"
    ];

    [TestMethod]
    public void DesktopAssembly_DoesNotReferenceWindowsOrLegacyUiAssemblies()
    {
        string[] references = typeof(App)
            .Assembly
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name ?? string.Empty)
            .ToArray();

        foreach (string forbidden in ForbiddenAssemblyNames)
        {
            CollectionAssert.DoesNotContain(
                references,
                forbidden,
                $"PCL.Desktop must not reference {forbidden}.");
        }

    }

    [TestMethod]
    public void AccountAndCloudImplementations_RemainOutsideDesktop()
    {
        string desktopRoot = FindDesktopProjectRoot();
        string repositoryRoot = Directory.GetParent(desktopRoot)?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repository root.");
        string project = File.ReadAllText(Path.Combine(desktopRoot, "PCL.Desktop.csproj"));
        Assert.IsFalse(
            project.Contains("PCL.Online", StringComparison.Ordinal),
            "PCL.Desktop must not reference the removed PCL.Online project.");
        Assert.IsFalse(
            File.Exists(Path.Combine(repositoryRoot, "PCL.Online", "PCL.Online.csproj")),
            "Account and cloud services belong to PCL.Plugin, not a launcher-body project.");

        string[] forbiddenTokens =
        [
            "namespace PCL.Online",
            "NCloudHttpClient",
            "CloudSyncService",
            "OnlineAccountService",
            "OnlineFriendService",
            "XboxSocialService",
            "PluginRemoteContentHandler"
        ];
        List<string> violations = [];
        foreach (string file in Directory.EnumerateFiles(desktopRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(desktopRoot, file);
            if (ShouldSkipSourceScan(relative))
                continue;
            string text = File.ReadAllText(file);
            foreach (string token in forbiddenTokens)
            {
                if (text.Contains(token, StringComparison.Ordinal))
                    violations.Add($"{relative}: {token}");
            }
        }

        Assert.AreEqual(
            0,
            violations.Count,
            "Account and cloud implementations must be supplied by PCL.Plugin:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void DesktopSource_DoesNotUseWpfApis()
    {
        string desktopRoot = FindDesktopProjectRoot();
        string[] forbiddenTokens =
        [
            "using System.Windows;",
            "using System.Windows.Controls",
            "using System.Windows.Documents",
            "using System.Windows.Markup",
            "using System.Windows.Media",
            "using System.Windows.Threading",
            "PresentationFramework",
            "PresentationCore",
            "WindowsBase",
            "PCL.Online.Windows",
            "Plain Craft Launcher 2"
        ];

        List<string> violations = [];
        foreach (string file in Directory.EnumerateFiles(desktopRoot, "*.*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(desktopRoot, file);
            if (ShouldSkipSourceScan(relative) || !IsScannedSourceFile(file))
                continue;

            string text = File.ReadAllText(file);
            foreach (string forbidden in forbiddenTokens)
            {
                if (text.Contains(forbidden, StringComparison.Ordinal))
                    violations.Add($"{relative}: {forbidden}");
            }
        }

        Assert.AreEqual(
            0,
            violations.Count,
            "PCL.Desktop Avalonia sources must not use WPF or legacy UI APIs:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void LocalizationCatalogs_AreSynchronizedUniqueAndReferenced()
    {
        string desktopRoot = FindDesktopProjectRoot();
        IReadOnlyDictionary<string, string> english = ReadLocalizationCatalog(
            Path.Combine(desktopRoot, "Localization", "en-US.xaml"));
        IReadOnlyDictionary<string, string> chinese = ReadLocalizationCatalog(
            Path.Combine(desktopRoot, "Localization", "zh-CN.xaml"));
        IReadOnlyDictionary<string, string> traditionalChinese = ReadLocalizationCatalog(
            Path.Combine(desktopRoot, "Localization", "zh-TW.xaml"));

        CollectionAssert.AreEquivalent(english.Keys.ToArray(), chinese.Keys.ToArray());
        CollectionAssert.AreEquivalent(english.Keys.ToArray(), traditionalChinese.Keys.ToArray());

        string repoRoot = Directory.GetParent(desktopRoot)?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repository root.");
        string[] sourceTexts = new[] { desktopRoot, Path.Combine(repoRoot, "PCL.Plugin") }
            .Where(Directory.Exists)
            .SelectMany(root => Directory
                .EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(IsScannedSourceFile)
                .Where(file => !ShouldSkipSourceScan(Path.GetRelativePath(root, file))))
            .Select(File.ReadAllText)
            .ToArray();
        string[] unreferenced = english.Keys
            .Where(key => !key.StartsWith("Localization.Meta.", StringComparison.Ordinal))
            .Where(key => !key.StartsWith("Plugin.", StringComparison.Ordinal))
            .Where(key => !sourceTexts.Any(source => source.Contains(key, StringComparison.Ordinal)))
            .ToArray();

        Assert.AreEqual(
            0,
            unreferenced.Length,
            "Localization catalogs contain keys that are not referenced by PCL.Desktop:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, unreferenced));

        foreach (string key in english.Keys)
        {
            CollectionAssert.AreEquivalent(
                ExtractPlaceholderNames(english[key]),
                ExtractPlaceholderNames(chinese[key]),
                $"Placeholder mismatch for localization key {key}.");
            CollectionAssert.AreEquivalent(
                ExtractPlaceholderNames(english[key]),
                ExtractPlaceholderNames(traditionalChinese[key]),
                $"Traditional Chinese placeholder mismatch for localization key {key}.");
        }
    }

    [TestMethod]
    public void LocalizationReferences_ArePresentInEveryCatalog()
    {
        string desktopRoot = FindDesktopProjectRoot();
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> catalogs =
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["en-US"] = ReadLocalizationCatalog(
                    Path.Combine(desktopRoot, "Localization", "en-US.xaml")),
                ["zh-CN"] = ReadLocalizationCatalog(
                    Path.Combine(desktopRoot, "Localization", "zh-CN.xaml")),
                ["zh-TW"] = ReadLocalizationCatalog(
                    Path.Combine(desktopRoot, "Localization", "zh-TW.xaml"))
            };
        HashSet<string> knownRoots = catalogs.Values
            .SelectMany(static catalog => catalog.Keys)
            .Select(static key => key.Split('.', 2)[0])
            .ToHashSet(StringComparer.Ordinal);
        string[] references = FindLocalizationReferences(desktopRoot, knownRoots)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] missing = catalogs
            .SelectMany(catalog => references
                .Where(reference => !catalog.Value.ContainsKey(reference))
                .Select(reference => $"{catalog.Key}: {reference}"))
            .ToArray();

        Assert.AreEqual(
            0,
            missing.Length,
            "PCL.Desktop references localization keys missing from one or more catalogs:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, missing));
    }

    [TestMethod]
    public void DesktopNavigation_UsesGeneratedStaticRegistry()
    {
        string desktopRoot = FindDesktopProjectRoot();
        string repoRoot = Directory.GetParent(desktopRoot)?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repository root.");
        string hostSource = File.ReadAllText(Path.Combine(desktopRoot, "Hosting", "DesktopHost.cs"));
        string registrySource = File.ReadAllText(Path.Combine(desktopRoot, "Hosting", "DesktopNavigationRegistry.cs"));
        string generatorSource = File.ReadAllText(Path.Combine(
            repoRoot,
            "PCL.Desktop.SourceGenerators",
            "DesktopNavigationRegistryGenerator.cs"));

        StringAssert.Contains(hostSource, "DesktopNavigationRegistry.RegisterGeneratedHostModules(builder)");
        Assert.IsFalse(hostSource.Contains("BuiltInLaunchModule", StringComparison.Ordinal));
        Assert.IsFalse(hostSource.Contains("BuiltInDownloadModule", StringComparison.Ordinal));
        Assert.AreEqual(4, CountOccurrences(registrySource, "[DesktopNavigationPage("));
        StringAssert.Contains(registrySource, "NavigationRouteId");
        StringAssert.Contains(generatorSource, "new global::PCL.UI.Abstractions.Navigation.NavigationRouteId");
        StringAssert.Contains(generatorSource, "new global::PCL.Application.Hosting.HostModuleId");
        Assert.IsFalse(generatorSource.Contains("StaticHostModule", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DesktopSettings_UsesGeneratedStaticRegistry()
    {
        string desktopRoot = FindDesktopProjectRoot();
        string repoRoot = Directory.GetParent(desktopRoot)?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repository root.");
        string setupLeftSource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Features",
            "Settings",
            "Views",
            "PageSetupLeft.axaml.cs"));
        string registrySource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Features",
            "Settings",
            "Views",
            "SetupPageRegistry.cs"));
        string generatorSource = File.ReadAllText(Path.Combine(
            repoRoot,
            "PCL.Desktop.SourceGenerators",
            "SetupPageRegistryGenerator.cs"));

        StringAssert.Contains(setupLeftSource, "SetupPageRegistry.CreatePage(page)");
        Assert.IsFalse(setupLeftSource.Contains("page switch", StringComparison.Ordinal));
        Assert.AreEqual(11, CountOccurrences(registrySource, "[SetupPage("));
        Assert.IsFalse(registrySource.Contains("SetupPageSubType.Plugin", StringComparison.Ordinal));
        StringAssert.Contains(setupLeftSource, "HostSettingsPageFactory.Create(descriptor)");
        StringAssert.Contains(setupLeftSource, "ItemHostSettings_");
        StringAssert.Contains(generatorSource, "SetupPageRegistry.g.cs");
        StringAssert.Contains(generatorSource, "public static partial global::PCL.Desktop.Controls.Legacy.MyPageRight CreatePage");
    }

    [TestMethod]
    public void DesktopInstancePages_UseGeneratedStaticRegistry()
    {
        string desktopRoot = FindDesktopProjectRoot();
        string repoRoot = Directory.GetParent(desktopRoot)?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repository root.");
        string leftSource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Features",
            "Instances",
            "Views",
            "PageInstanceLeft.axaml.cs"));
        string toolsSource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Features",
            "Instances",
            "Views",
            "PageInstanceToolsRight.axaml.cs"));
        string resourceSource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Features",
            "Instances",
            "Views",
            "PageInstanceResourceRight.axaml.cs"));
        string registrySource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Features",
            "Instances",
            "Views",
            "InstancePageRegistry.cs"));
        string generatorSource = File.ReadAllText(Path.Combine(
            repoRoot,
            "PCL.Desktop.SourceGenerators",
            "InstancePageRegistryGenerator.cs"));

        Assert.IsFalse(leftSource.Contains("Enum.IsDefined(typeof(InstancePageSubType)", StringComparison.Ordinal));
        StringAssert.Contains(leftSource, "InstancePageRegistry.IsDefined");
        StringAssert.Contains(toolsSource, "InstancePageRegistry.UsesGenericFolderPage");
        Assert.IsFalse(toolsSource.Contains("page switch", StringComparison.Ordinal));
        StringAssert.Contains(resourceSource, "InstancePageRegistry.GetResourceKind(page)");
        Assert.IsFalse(resourceSource.Contains("ResourceKindFromPage", StringComparison.Ordinal));
        Assert.AreEqual(12, CountOccurrences(registrySource, "[InstancePage("));
        StringAssert.Contains(generatorSource, "InstancePageRegistry.g.cs");
        StringAssert.Contains(generatorSource, "GetResourceKind");
    }

    [TestMethod]
    public void DesktopDownloadPages_UseGeneratedStaticRegistry()
    {
        string desktopRoot = FindDesktopProjectRoot();
        string repoRoot = Directory.GetParent(desktopRoot)?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repository root.");
        string leftSource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Features",
            "Downloads",
            "Views",
            "PageDownloadLeft.axaml.cs"));
        string registrySource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Features",
            "Downloads",
            "Views",
            "DownloadPageRegistry.cs"));
        string generatorSource = File.ReadAllText(Path.Combine(
            repoRoot,
            "PCL.Desktop.SourceGenerators",
            "DownloadPageRegistryGenerator.cs"));

        StringAssert.Contains(leftSource, "DownloadPageRegistry.CreatePage(page, _pageContext)");
        Assert.IsFalse(leftSource.Contains("new PageDownloadInstall", StringComparison.Ordinal));
        Assert.AreEqual(1, CountOccurrences(registrySource, "[DownloadPage("));
        StringAssert.Contains(generatorSource, "DownloadPageRegistry.g.cs");
        StringAssert.Contains(generatorSource, "DownloadPageFactoryContext context");
    }

    [TestMethod]
    public void DesktopLoaderCards_UseSharedStaticRegistry()
    {
        string desktopRoot = FindDesktopProjectRoot();
        string downloadInstallSource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Features",
            "Downloads",
            "Views",
            "PageDownloadInstall.axaml.cs"));
        string instanceInstallSource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Features",
            "Instances",
            "Views",
            "PageInstanceInstallRight.axaml.cs"));
        string registrySource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Features",
            "Shared",
            "MinecraftLoaderCardRegistry.cs"));
        string idSource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Features",
            "Shared",
            "MinecraftLoaderCardId.cs"));

        StringAssert.Contains(downloadInstallSource, "MinecraftLoaderCardRegistry.AllCards");
        StringAssert.Contains(instanceInstallSource, "MinecraftLoaderCardRegistry.AllCards");
        Assert.IsFalse(downloadInstallSource.Contains("LoaderCardNames", StringComparison.Ordinal));
        Assert.IsFalse(instanceInstallSource.Contains("LoaderCardNames", StringComparison.Ordinal));
        StringAssert.Contains(registrySource, "MinecraftLoaderCardId.LegacyFabricApi");
        StringAssert.Contains(registrySource, "MinecraftLoaderCardId.OptiFabric");
        StringAssert.Contains(registrySource, "ReadOnlySpan<MinecraftLoaderCardDescriptor>");
        StringAssert.Contains(idSource, "readonly record struct MinecraftLoaderCardId");
    }

    [TestMethod]
    public void DesktopVersionSurfacesUseUnifiedMetadata()
    {
        string desktopRoot = FindDesktopProjectRoot();
        string repoRoot = Directory.GetParent(desktopRoot)?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repository root.");
        string csprojSource = File.ReadAllText(Path.Combine(desktopRoot, "PCL.Desktop.csproj"));
        string updatePageSource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Features",
            "Settings",
            "Views",
            "PageSetupUpdate.axaml.cs"));
        string generatorSource = File.ReadAllText(Path.Combine(
            repoRoot,
            "PCL.Desktop.SourceGenerators",
            "BuildInfoGenerator.cs"));

        StringAssert.Contains(csprojSource, "CompilerVisibleProperty Include=\"InformationalVersion\"");
        StringAssert.Contains(csprojSource, "CompilerVisibleProperty Include=\"Version\"");
        StringAssert.Contains(csprojSource, "EmbeddedResource Include=\"metadata.json\"");
        StringAssert.Contains(updatePageSource, "PclMetadata.Current.DisplayVersion");
        Assert.IsFalse(updatePageSource.Contains("Assembly.GetCustomAttribute", StringComparison.Ordinal));
        Assert.IsFalse(updatePageSource.Contains("Assembly.GetName()", StringComparison.Ordinal));
        StringAssert.Contains(generatorSource, "build_property.");
        StringAssert.Contains(generatorSource, "\"InformationalVersion\"");
        StringAssert.Contains(generatorSource, "PclBuildInfo.g.cs");
    }

    [TestMethod]
    public void LauncherAutomaticUpdatesAreOwnedByProcessCoordinator()
    {
        string desktopRoot = FindDesktopProjectRoot();
        string mainWindow = File.ReadAllText(Path.Combine(desktopRoot, "Views", "MainWindow.axaml.cs"));
        string updatePage = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Features",
            "Settings",
            "Views",
            "PageSetupUpdate.axaml.cs"));
        string updatePageXaml = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Features",
            "Settings",
            "Views",
            "PageSetupUpdate.axaml"));
        string mainWindowLogin = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Views",
            "MainWindow.Login.cs"));
        string coordinator = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Hosting",
            "LauncherUpdateCoordinator.cs"));
        string notifications = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Hosting",
            "DesktopHostNotifications.cs"));
        string installer = File.ReadAllText(Path.Combine(
            Directory.GetParent(desktopRoot)!.FullName,
            "PCL.Application",
            "Updates",
            "LauncherUpdateInstaller.cs"));

        StringAssert.Contains(mainWindow, "LauncherUpdateCoordinator.Current.StartAutomaticUpdateOnceAsync()");
        StringAssert.Contains(
            mainWindow,
            "TransparencyLevelHint = [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None]");
        Assert.IsFalse(mainWindow.Contains(
            "TransparencyLevelHint = [WindowTransparencyLevel.None]",
            StringComparison.Ordinal));
        StringAssert.Contains(coordinator, "_automaticTask ??= RunAutomaticUpdateAsync()");
        StringAssert.Contains(coordinator, "PreparedLauncherUpdate? PreparedUpdate");
        StringAssert.Contains(coordinator, "PromptAvailableUpdateAsync");
        StringAssert.Contains(coordinator, "PromptDownloadedUpdateAsync");
        StringAssert.Contains(coordinator, "SystemUpdateSkippedTarget");
        StringAssert.Contains(coordinator, "_installOnExit = prepared");
        StringAssert.Contains(coordinator, "WaitForAutomaticCheckResultAsync");
        StringAssert.Contains(coordinator, "PreparedUpdateChanged?.Invoke(prepared)");
        StringAssert.Contains(coordinator, "IsUpdateTransferActive");
        StringAssert.Contains(installer, "ScheduleInstallOnExit");
        StringAssert.Contains(installer, "restartAfterInstall: false");
        // Markdown prompt lives on the MainWindow partial that owns dialog host controls.
        StringAssert.Contains(mainWindow, "ShowMarkdownDialog");
        StringAssert.Contains(mainWindowLogin, "ShowMarkdownDialog");
        StringAssert.Contains(mainWindowLogin, "MyMsgMarkdown dialog = new()");
        StringAssert.Contains(notifications, "int result = await ChoiceAsync(");
        StringAssert.Contains(notifications, "return result == 1");
        StringAssert.Contains(updatePage, "_updateCoordinator.StartAutomaticUpdateOnceAsync()");
        StringAssert.Contains(updatePage, "HandleAvailableUpdateAsync");
        StringAssert.Contains(updatePage, "SetUpdateActionButtons(_preparedUpdate is not null)");
        StringAssert.Contains(updatePage, "Setup.Update.RestartAndInstall");
        StringAssert.Contains(updatePage, "SetUpdateButtonsVisible(progress.Stage is LauncherUpdateStage.Ready)");
        StringAssert.Contains(updatePageXaml, "<TextBlock x:Name=\"TextChangelog\"");
        Assert.IsFalse(updatePageXaml.Contains("<legacy:MyMarkdownViewer x:Name=\"TextChangelog\"", StringComparison.Ordinal));
        StringAssert.Contains(updatePage, "_updateCoordinator.ProgressChanged -= OnUpdateProgressChanged");
        Assert.IsFalse(updatePage.Contains("new LauncherUpdateService", StringComparison.Ordinal));
        Assert.IsFalse(updatePage.Contains("CancelInFlightCheck", StringComparison.Ordinal));
    }

    [TestMethod]
    public void StartupWindowHandoff_DoesNotBlockFirstFrameOrLeaveExplicitShutdownEnabled()
    {
        string desktopRoot = FindDesktopProjectRoot();
        string app = File.ReadAllText(Path.Combine(desktopRoot, "App.axaml.cs"));
        string mainWindow = File.ReadAllText(Path.Combine(desktopRoot, "Views", "MainWindow.axaml.cs"));

        int showMainStart = app.IndexOf(
            "private void ShowMainWindow",
            StringComparison.Ordinal);
        int splashDismissStart = app.IndexOf(
            "private void DismissSplash",
            showMainStart,
            StringComparison.Ordinal);
        Assert.IsTrue(showMainStart >= 0 && splashDismissStart > showMainStart);
        string showMain = app[showMainStart..splashDismissStart];

        int switchLifetime = showMain.IndexOf(
            "desktop.ShutdownMode = ShutdownMode.OnMainWindowClose",
            StringComparison.Ordinal);
        int showWindow = showMain.IndexOf(
            "mainWindow.Show()",
            StringComparison.Ordinal);
        Assert.IsTrue(
            switchLifetime >= 0 && showWindow > switchLifetime,
            "The real shell must own shutdown before Window.Show can dispatch a re-entrant close.");
        StringAssert.Contains(showMain, "mainWindow.Opened += opened");
        StringAssert.Contains(app, "HandleSplashClosing(desktop)");
        StringAssert.Contains(app, "Volatile.Read(ref _startupShutdownRequested)");

        int openedStart = mainWindow.IndexOf(
            "private void OnMainWindowOpened",
            StringComparison.Ordinal);
        int noticesStart = mainWindow.IndexOf(
            "private void MaybeShowFirstRunDialogs",
            openedStart,
            StringComparison.Ordinal);
        Assert.IsTrue(openedStart >= 0 && noticesStart > openedStart);
        string opened = mainWindow[openedStart..noticesStart];
        int animation = opened.IndexOf("StartShowAnimation()", StringComparison.Ordinal);
        int update = opened.IndexOf(
            "Task.Run(() => LauncherUpdateCoordinator.Current.StartAutomaticUpdateOnceAsync())",
            StringComparison.Ordinal);
        Assert.IsTrue(
            animation >= 0 && update > animation,
            "The first-frame animation must start before update work is moved off the UI thread.");

        StringAssert.Contains(mainWindow, "Task.Run(() =>");
        StringAssert.Contains(mainWindow, "\"MainWindow.CloseCleanup\"");
    }

    [TestMethod]
    public void DesktopHost_InitializesBuiltinModulesOnly()
    {
        string desktopHost = File.ReadAllText(Path.Combine(
            FindDesktopProjectRoot(),
            "Hosting",
            "DesktopHost.cs"));
        string activation = File.ReadAllText(Path.Combine(
            FindDesktopProjectRoot(),
            "Hosting",
            "DesktopHostWindowActivation.cs"));

        StringAssert.Contains(desktopHost, "RegisterGeneratedHostModules");
        Assert.IsFalse(desktopHost.Contains("EmbeddedRuntimeExtensionLoader", StringComparison.Ordinal));
        Assert.IsFalse(desktopHost.Contains("RuntimeExtensionHostAccess.Initialize", StringComparison.Ordinal));
        StringAssert.Contains(activation, "mainWindow.ActivateExistingInstance()");
    }

    [TestMethod]
    public void ReleaseWorkflowsPublishAvaloniaForEveryDesktopRuntime()
    {
        string desktopRoot = FindDesktopProjectRoot();
        string repoRoot = Directory.GetParent(desktopRoot)?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repository root.");
        string workflowRoot = Path.Combine(repoRoot, ".github", "workflows");
        string reusable = File.ReadAllText(Path.Combine(workflowRoot, "reusable-build.yml"));
        string ci = File.ReadAllText(Path.Combine(workflowRoot, "build-test.yml"));
        string stable = File.ReadAllText(Path.Combine(workflowRoot, "release-stable_publish.yml"));
        string beta = File.ReadAllText(Path.Combine(workflowRoot, "release-beta_publish.yml"));
        string patches = File.ReadAllText(Path.Combine(workflowRoot, "generate-launcher-patches.yml"));

        foreach (string runtime in new[] { "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" })
        {
            StringAssert.Contains(stable, runtime);
            StringAssert.Contains(beta, runtime);
        }

        StringAssert.Contains(stable, "include_prerelease_history: true");
        StringAssert.Contains(beta, "include_prerelease_history: true");
        Assert.AreEqual(
            2,
            System.Text.RegularExpressions.Regex.Matches(
                patches,
                "include_prerelease_history:[\\s\\S]*?default: true").Count,
            "Reusable and manual patch generation must include beta/RC predecessors by default.");

        foreach (string workflow in new[] { stable, beta })
        {
            StringAssert.Contains(workflow, "SelfContained");
            StringAssert.Contains(workflow, "NoRuntime");
            // Product binary is always PCL-N-Edition (AssemblyName); project folder remains PCL.Desktop.
            Assert.IsTrue(
                workflow.Contains("PCL-N-Edition", StringComparison.Ordinal),
                "Release matrix must publish the Avalonia desktop binary as PCL-N-Edition.");
        }

        StringAssert.Contains(reusable, "PCL.Desktop/PCL.Desktop.csproj");
        StringAssert.Contains(reusable, "binary_name:");
        StringAssert.Contains(reusable, "PublishAot=true");
        StringAssert.Contains(reusable, "PublishTrimmed=true");
        StringAssert.Contains(reusable, "PublishSingleFile=false");
        StringAssert.Contains(reusable, "pack_native_runtime.py");
        StringAssert.Contains(reusable, "PclNativeRuntimeZipPath");
        // Desktop ProjectReference → external/Jvm.NET; CI must materialize the submodule.
        StringAssert.Contains(reusable, "submodules: recursive");
        // Opaque sidecar zip may be embedded; in-process private plugin IL must not be.
        StringAssert.Contains(reusable, "embed_plugin_sidecar");
        StringAssert.Contains(reusable, "PclPluginSidecarZipPath");
        StringAssert.Contains(reusable, "pack-plugin-sidecar-zip.ps1");
        StringAssert.Contains(reusable, "-SelfContained:$selfContained");
        // Private PCL.Plugin requires PAT-backed checkout. Tag resolution stays on
        // authenticated git data and does not consume the GitHub API quota.
        StringAssert.Contains(reusable, "repository: PCL-N-Edition/PCL.Plugin");
        StringAssert.Contains(reusable, "PCL_PLUGIN_TOKEN");
        StringAssert.Contains(reusable, "git -C $pluginRoot tag -l 'v*'");
        Assert.IsFalse(reusable.Contains("gh api", StringComparison.Ordinal));
        StringAssert.Contains(reusable, "Plan plugin sidecar embed");
        Assert.IsFalse(reusable.Contains("PclPluginAssembly", StringComparison.Ordinal));
        Assert.IsFalse(reusable.Contains("gh release download", StringComparison.Ordinal));
        foreach (string workflow in new[] { ci, stable, beta })
        {
            Assert.IsFalse(workflow.Contains("resolve-plugin-version:", StringComparison.Ordinal));
            // Product packages embed sidecar inside the host; never ship a NoPlugin SKU name.
            Assert.IsFalse(workflow.Contains("NoPlugin", StringComparison.Ordinal));
            Assert.IsFalse(workflow.Contains("WithPlugin", StringComparison.Ordinal));
        }
        StringAssert.Contains(reusable, "app=\"$PUBLISH_DIR/PCL N.app\"");
        StringAssert.Contains(reusable, "contents=\"$app/Contents\"");
        StringAssert.Contains(reusable, "CFBundlePackageType");
        StringAssert.Contains(reusable, "codesign --verify --deep --strict");
        StringAssert.Contains(stable, "binary=\"artifact/PCL N.app/Contents/MacOS/${{ matrix.target.binary_name }}\"");
        StringAssert.Contains(beta, "binary=\"artifact/PCL N.app/Contents/MacOS/${{ matrix.target.binary_name }}\"");
        StringAssert.Contains(stable, "chmod +x \"$binary\"");
        StringAssert.Contains(beta, "chmod +x \"$binary\"");
        StringAssert.Contains(stable, "tar -C artifact -czf \"dist/${base}.tar.gz\" \"PCL N.app\"");
        StringAssert.Contains(beta, "tar -C artifact -czf \"dist/${base}.tar.gz\" \"PCL N.app\"");
        Assert.IsFalse(ci.Contains("generate-launcher-patches.yml", StringComparison.Ordinal));
        StringAssert.Contains(ci, "supportsPatches\": false");
        foreach (string runtime in new[] { "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" })
            StringAssert.Contains(ci, runtime);
        StringAssert.Contains(beta, "github.event.release.tag_name != 'ci-latest'");
        StringAssert.Contains(beta, "inputs.tag_name != 'ci-latest'");
        Assert.IsFalse(reusable.Contains("Plain Craft Launcher 2", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DesktopHost_DoesNotEmbedOrReferencePluginAssemblies()
    {
        string desktopRoot = FindDesktopProjectRoot();
        string repoRoot = Directory.GetParent(desktopRoot)?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repository root.");
        string projectSource = File.ReadAllText(Path.Combine(desktopRoot, "PCL.Desktop.csproj"));
        string solutionSource = File.ReadAllText(Path.Combine(repoRoot, "PCL-N.slnx"));
        string hostSource = File.ReadAllText(Path.Combine(desktopRoot, "Hosting", "DesktopHost.cs"));
        string optionalSource = File.ReadAllText(Path.Combine(desktopRoot, "Hosting", "DesktopHost.Optional.cs"));

        Assert.IsFalse(File.Exists(Path.Combine(desktopRoot, "Hosting", "EmbeddedRuntimeExtensionLoader.cs")));
        Assert.IsFalse(projectSource.Contains("PclPluginAssembly", StringComparison.Ordinal));
        Assert.IsFalse(projectSource.Contains("PCL.Desktop.Embedded.PCL.Plugin", StringComparison.Ordinal));
        // Harmony must never appear in host project body (sidecar owns it).
        Assert.IsFalse(projectSource.Contains("Lib.Harmony", StringComparison.Ordinal));
        Assert.IsFalse(hostSource.Contains("LoadFromStream", StringComparison.Ordinal));
        Assert.IsFalse(hostSource.Contains("EmbeddedRuntimeExtensionLoader", StringComparison.Ordinal));
        Assert.IsFalse(solutionSource.Contains("PCL.Plugin", StringComparison.Ordinal));
        Assert.IsFalse(projectSource.Contains("ProjectReference Include=\"../PCL.Plugin", StringComparison.Ordinal));
        StringAssert.Contains(hostSource, "RegisterGeneratedHostModules");
        StringAssert.Contains(hostSource, "RegisterOptionalModules");
        // In-process bootstrap must not live in host; sidecar process hosts PluginPlatformBootstrap.
        Assert.IsFalse(optionalSource.Contains("PclPluginHostModule", StringComparison.Ordinal));
        Assert.IsFalse(optionalSource.Contains("PluginPlatformBootstrap", StringComparison.Ordinal));
        // Out-of-process IPC client is the supported integration.
        StringAssert.Contains(optionalSource, "PluginSidecarSupervisor");
        // InternalsVisibleTo PCL.Plugin is allowed so the *sidecar process* can share Desktop UI types;
        // host still must not ProjectReference or embed plugin assemblies.
        StringAssert.Contains(projectSource, "InternalsVisibleTo Include=\"PCL.Plugin\"");
    }

    [TestMethod]
    public void PclN_DoesNotImplementThePluginPlatform()
    {
        string desktopRoot = FindDesktopProjectRoot();
        string repoRoot = Directory.GetParent(desktopRoot)?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repository root.");
        string applicationRoot = Path.Combine(repoRoot, "PCL.Application");
        string legacyPlatformRoot = Path.Combine(applicationRoot, "Hosting", "PluginPlatform");

        Assert.IsFalse(Directory.Exists(legacyPlatformRoot) &&
                       Directory.EnumerateFiles(legacyPlatformRoot, "*.cs", SearchOption.AllDirectories).Any(),
            "PCL-N must not contain a plugin platform implementation directory.");

        string[] projectFiles = Directory.EnumerateFiles(repoRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}PCL.Plugin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (string projectFile in projectFiles)
        {
            string project = File.ReadAllText(projectFile);
            Assert.IsFalse(project.Contains("ProjectReference Include=\"../PCL.Plugin", StringComparison.OrdinalIgnoreCase), projectFile);
            Assert.IsFalse(project.Contains("PCL.N.Plugin.Abstractions.csproj", StringComparison.OrdinalIgnoreCase), projectFile);
            Assert.IsFalse(project.Contains("PCL.N.Plugin.Sdk.csproj", StringComparison.OrdinalIgnoreCase), projectFile);
        }

        string[] forbiddenSourceTokens =
        [
            "using PCL.N.Plugin",
            "namespace PCL.Application.Hosting.PluginPlatform",
            "interface IPluginHost",
            "class PclPluginPlatformHost",
            "record PluginSafetySettings",
            "interface IPluginCatalogService",
            "class PluginPlatformBootstrap"
        ];
        foreach (string sourceFile in Directory.EnumerateFiles(repoRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (string.Equals(sourceFile, Path.Combine(repoRoot, "PCL.Desktop.Test", "DesktopArchitectureTests.cs"), StringComparison.OrdinalIgnoreCase) ||
                sourceFile.Contains($"{Path.DirectorySeparatorChar}PCL.Plugin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                sourceFile.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                sourceFile.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string source = File.ReadAllText(sourceFile);
            foreach (string token in forbiddenSourceTokens)
                Assert.IsFalse(source.Contains(token, StringComparison.Ordinal), $"{sourceFile}: {token}");
        }
    }

    [TestMethod]
    public void PortableWorkflow_BuildsTheCurrentSolution()
    {
        string desktopRoot = FindDesktopProjectRoot();
        string repoRoot = Directory.GetParent(desktopRoot)?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repository root.");
        string workflow = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "portable-core.yml"));

        StringAssert.Contains(workflow, "dotnet restore PCL-N.slnx");
        StringAssert.Contains(workflow, "dotnet build PCL-N.slnx");
        Assert.IsFalse(workflow.Contains("PCL.Portable.slnx", StringComparison.Ordinal));
    }

    private static string FindDesktopProjectRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "PCL.Desktop", "PCL.Desktop.csproj");
            if (File.Exists(candidate))
                return Path.Combine(directory.FullName, "PCL.Desktop");

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate PCL.Desktop project root.");
    }

    private static bool IsScannedSourceFile(string file) =>
        file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
        file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) ||
        file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldSkipSourceScan(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        return normalized.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("obj/", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string> ReadLocalizationCatalog(string path)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        (string Key, string Value)[] entries = XDocument
            .Load(path)
            .Root?
            .Elements()
            .Select(element => (
                Key: element.Attribute(xaml + "Key")?.Value ?? string.Empty,
                Value: element.Value))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
            .ToArray() ?? [];
        string[] duplicateKeys = entries
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        string[] emptyKeys = entries
            .Where(entry => string.IsNullOrEmpty(entry.Value))
            .Select(entry => entry.Key)
            .ToArray();

        Assert.AreEqual(
            0,
            duplicateKeys.Length,
            $"Duplicate localization keys in {path}: {string.Join(", ", duplicateKeys)}");
        Assert.AreEqual(
            0,
            emptyKeys.Length,
            $"Empty localization values in {path}: {string.Join(", ", emptyKeys)}");
        return entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
    }

    private static HashSet<string> FindLocalizationReferences(
        string desktopRoot,
        IReadOnlySet<string> knownRoots)
    {
        System.Text.RegularExpressions.Regex directCall = new(
            @"(?:AvaloniaLocalizationManager\.GetText|(?:Get|BuiltIn)?ResourceText|FormatResource)" +
            @"\s*\(\s*""(?<key>[^""]+)""",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        System.Text.RegularExpressions.Regex dottedString = new(
            @"""(?<key>[A-Za-z][A-Za-z0-9_-]*(?:\.[A-Za-z0-9_-]+)+)""",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        System.Text.RegularExpressions.Regex textResource = new(
            @"\b(?:Text|Title|Header|Content|ToolTip|HintText|Watermark)\s*=\s*""" +
            @"\{(?:Dynamic|Static)Resource\s+(?<key>[A-Za-z][A-Za-z0-9_.-]+)\}""",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        HashSet<string> references = new(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(desktopRoot, "*.*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(desktopRoot, file);
            if (ShouldSkipSourceScan(relative) || !IsScannedSourceFile(file))
                continue;

            string source = File.ReadAllText(file);
            if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                foreach (System.Text.RegularExpressions.Match match in directCall.Matches(source))
                    references.Add(match.Groups["key"].Value);
                foreach (System.Text.RegularExpressions.Match match in dottedString.Matches(source))
                {
                    string key = match.Groups["key"].Value;
                    if (knownRoots.Contains(key.Split('.', 2)[0]))
                        references.Add(key);
                }
            }
            else if (file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            {
                foreach (System.Text.RegularExpressions.Match match in textResource.Matches(source))
                {
                    string key = match.Groups["key"].Value;
                    if (knownRoots.Contains(key.Split('.', 2)[0]))
                        references.Add(key);
                }
            }
        }

        return references;
    }

    private static string[] ExtractPlaceholderNames(string value) =>
        System.Text.RegularExpressions.Regex
            .Matches(value, @"(?<!\{)\{([A-Za-z0-9_]+)(?:[^}]*)\}(?!\})")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
