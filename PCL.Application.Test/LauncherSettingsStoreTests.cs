// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Settings;
using PCL.Core.App;

namespace PCL.Application.Test;

[TestClass]
public sealed class LauncherSettingsStoreTests
{
    [TestMethod]
    public async Task SaveAndLoadAsync_RoundTripsStronglyTypedSettings()
    {
        using TestDirectory directory = new();
        using LauncherSettingsStore store = new(
            Path.Combine(directory.Path, "settings.json"));
        LauncherSettings expected = new()
        {
            AutomaticallyRepairGameIssues = false,
            ColorMode = ColorMode.Dark,
            LightColor = ColorTheme.SkyBlue,
            DarkColor = ColorTheme.CatBlue,
            DownloadSource = DownloadSourcePreference.OfficialOnly
        };

        await store.SaveAsync(expected);
        LauncherSettingsLoadResult result = await store.LoadAsync();

        AssertSettingsEqual(expected, result.Settings);
        Assert.IsFalse(result.RecoveredFromInvalidFile);
        Assert.IsNull(result.InvalidFileBackupPath);
    }

    [TestMethod]
    public async Task LoadAsync_InvalidJsonCreatesBackupAndReturnsDefaults()
    {
        using TestDirectory directory = new();
        string settingsPath = Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, "{invalid");
        using LauncherSettingsStore store = new(settingsPath);

        LauncherSettingsLoadResult result = await store.LoadAsync();

        Assert.IsTrue(result.RecoveredFromInvalidFile);
        AssertSettingsEqual(new LauncherSettings(), result.Settings);
        Assert.IsNotNull(result.InvalidFileBackupPath);
        Assert.IsTrue(File.Exists(result.InvalidFileBackupPath));
    }

    [TestMethod]
    public async Task SeparateStoreInstances_SerializeConcurrentReadsAndWrites()
    {
        using TestDirectory directory = new();
        string settingsPath = Path.Combine(directory.Path, "settings.json");
        using LauncherSettingsStore initialStore = new(settingsPath);
        await initialStore.SaveAsync(new LauncherSettings());

        Task[] workers = Enumerable.Range(0, 12)
            .Select(async worker =>
            {
                for (int iteration = 0; iteration < 20; iteration++)
                {
                    using LauncherSettingsStore store = new(settingsPath);
                    LauncherSettings settings = new();
                    settings.SetIntegerOption("Worker", worker);
                    settings.SetIntegerOption("Iteration", iteration);
                    await store.SaveAsync(settings);
                    LauncherSettingsLoadResult loaded = await store.LoadAsync();
                    Assert.AreEqual(LauncherSettings.CurrentSchemaVersion, loaded.Settings.SchemaVersion);
                }
            })
            .ToArray();

        await Task.WhenAll(workers);

        using LauncherSettingsStore finalStore = new(settingsPath);
        LauncherSettingsLoadResult result = await finalStore.LoadAsync();
        Assert.IsFalse(result.RecoveredFromInvalidFile);
        Assert.AreEqual(LauncherSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
        Assert.AreEqual(0, Directory.EnumerateFiles(directory.Path, "*.tmp").Count());
    }

    [TestMethod]
    public void Normalize_DisablesUnsupportedAccentAndMirrorChoices()
    {
        LauncherSettings settings = new()
        {
            LightColor = ColorTheme.SystemAccent,
            DarkColor = ColorTheme.SystemAccent,
            DownloadSource = DownloadSourcePreference.MirrorOnly
        };

        LauncherSettings normalized = LauncherSettingsPolicy.Normalize(
            settings,
            supportsSystemAccentTheme: false,
            allowsDomesticMirror: false);

        Assert.AreEqual(ColorTheme.CatBlue, normalized.LightColor);
        Assert.AreEqual(ColorTheme.CatBlue, normalized.DarkColor);
        Assert.AreEqual(
            DownloadSourcePreference.OfficialOnly,
            normalized.DownloadSource);
        Assert.AreEqual(ColorMode.System, normalized.ColorMode);
    }

    [TestMethod]
    public void Normalize_DisablesUnsupportedCustomPalette()
    {
        LauncherSettings settings = new()
        {
            LightColor = ColorTheme.Custom,
            DarkColor = ColorTheme.Custom
        };

        LauncherSettings normalized = LauncherSettingsPolicy.Normalize(
            settings,
            supportsSystemAccentTheme: false,
            allowsDomesticMirror: true,
            supportsCustomColorPalette: false);

        Assert.AreEqual(ColorTheme.CatBlue, normalized.LightColor);
        Assert.AreEqual(ColorTheme.CatBlue, normalized.DarkColor);
    }

    [TestMethod]
    public void OptionAccessors_UseStrongCaseInsensitiveSettingKeys()
    {
        LauncherSettings settings = new()
        {
            BooleanOptions = new Dictionary<string, bool> { ["LaunchAutoRepairGame"] = true },
            IntegerOptions = new Dictionary<string, int> { ["LaunchRamType"] = 1 },
            TextOptions = new Dictionary<string, string> { ["LaunchAdvanceJvm"] = "-Ddemo=true" }
        };

        Assert.IsTrue(settings.GetBooleanOption("launchautorepairgame"));
        Assert.AreEqual(1, settings.GetIntegerOption("launchramtype"));
        Assert.AreEqual("-Ddemo=true", settings.GetTextOption("launchadvancejvm"));

        settings.SetTextOption("LAUNCHADVANCEJVM", "-Ddemo=false");

        Assert.AreEqual("-Ddemo=false", settings.GetTextOption("LaunchAdvanceJvm"));
        Assert.AreEqual(1, settings.TextOptions.Count);
        Assert.ThrowsExactly<ArgumentException>(() => settings.SetTextOption(default, ""));
    }

    [TestMethod]
    public void LauncherSettingKeys_KeepPersistedNamesStable()
    {
        Assert.AreEqual("LaunchAdvanceJvm", LauncherSettingKeys.LaunchAdvanceJvm.Value);
        Assert.AreEqual("LaunchAdvanceGame", LauncherSettingKeys.LaunchAdvanceGame.Value);
        Assert.AreEqual("LaunchArgumentWindowHeight", LauncherSettingKeys.LaunchArgumentWindowHeight.Value);
        Assert.AreEqual("LaunchArgumentWindowType", LauncherSettingKeys.LaunchArgumentWindowType.Value);
        Assert.AreEqual("LaunchArgumentWindowWidth", LauncherSettingKeys.LaunchArgumentWindowWidth.Value);
        Assert.AreEqual("LaunchPreferredIpStack", LauncherSettingKeys.LaunchPreferredIpStack.Value);
        Assert.AreEqual("LaunchRamCustom", LauncherSettingKeys.LaunchRamCustom.Value);
        Assert.AreEqual("LaunchRamType", LauncherSettingKeys.LaunchRamType.Value);
        Assert.AreEqual("LaunchSelectedJava", LauncherSettingKeys.LaunchSelectedJava.Value);
        Assert.AreEqual("JavaCustomRoots", LauncherSettingKeys.JavaCustomRoots.Value);
        Assert.AreEqual("HintDownloadThread", LauncherSettingKeys.HintDownloadThread.Value);
        Assert.AreEqual("ToolDownloadThread", LauncherSettingKeys.ToolDownloadThread.Value);
        Assert.AreEqual("UiCustomLogoPath", LauncherSettingKeys.UiCustomLogoPath.Value);
        Assert.AreEqual("JavaDisabled|/opt/java/bin/java", LauncherSettingKeys.JavaDisabled("/opt/java/bin/java").Value);
        Assert.ThrowsExactly<ArgumentException>(() => LauncherSettingKeys.JavaDisabled(""));
    }

    [TestMethod]
    public void NormalizeOptionDictionaries_RemovesBlankAndDuplicateKeys()
    {
        LauncherSettings settings = new()
        {
            BooleanOptions = new Dictionary<string, bool>
            {
                ["HintDownloadThread"] = false,
                ["hintdownloadthread"] = true,
                [""] = true
            }
        };

        LauncherSettings normalized = settings.NormalizeOptionDictionaries();

        Assert.IsTrue(normalized.GetBooleanOption("HINTDOWNLOADTHREAD"));
        Assert.AreEqual(1, normalized.BooleanOptions.Count);
    }

    private static void AssertSettingsEqual(
        LauncherSettings expected,
        LauncherSettings actual)
    {
        Assert.AreEqual(expected.SchemaVersion, actual.SchemaVersion);
        Assert.AreEqual(
            expected.AutomaticallyRepairGameIssues,
            actual.AutomaticallyRepairGameIssues);
        Assert.AreEqual(expected.ColorMode, actual.ColorMode);
        Assert.AreEqual(expected.LightColor, actual.LightColor);
        Assert.AreEqual(expected.DarkColor, actual.DarkColor);
        Assert.AreEqual(expected.DownloadSource, actual.DownloadSource);
        CollectionAssert.AreEquivalent(
            expected.BooleanOptions.ToArray(),
            actual.BooleanOptions.ToArray());
        CollectionAssert.AreEquivalent(
            expected.IntegerOptions.ToArray(),
            actual.IntegerOptions.ToArray());
        CollectionAssert.AreEquivalent(
            expected.TextOptions.ToArray(),
            actual.TextOptions.ToArray());
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pcl-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
