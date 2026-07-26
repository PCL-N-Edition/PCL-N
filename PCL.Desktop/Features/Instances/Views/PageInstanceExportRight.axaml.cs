// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using PCL.Application.Instances;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Features.Launching.Views;
using PCL.Desktop.Features.Shared;

namespace PCL.Desktop.Features.Instances.Views;

public class ExportOption : AvaloniaObject
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<ExportOption, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<ExportOption, string>(nameof(Description), string.Empty);

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string? TitleResourceKey { get; set; }

    public string? DescriptionResourceKey { get; set; }

    public string? Rules { get; set; }

    public string? ShowRules { get; set; }

    public bool DefaultChecked { get; set; }

    public bool RequireModLoader { get; set; }

    public bool RequireOptiFine { get; set; }

    public bool RequireModLoaderOrOptiFine { get; set; }
}

public sealed record InstanceExportPageRequest(
    LaunchInstanceInfo Instance,
    string PackageName,
    string PackageVersion,
    IReadOnlyList<string> Rules,
    bool IncludeLauncherFiles,
    bool IncludeLauncherCustom,
    bool IncludeBundleFiles,
    bool ModrinthUploadMode);

public partial class PageInstanceExportRight : MyPageRight
{
    private static readonly string[] SubOptionBlackList = ["Quark Programmer Art.zip", "+ EuphoriaPatches_"];
    private static readonly string[] DefaultExcludeRules =
        ["!*.log", "!*.dat_old", "!*.BakaCoreInfo", "!hmclversion.cfg", "!log4j2.xml"];

    private LaunchInstanceInfo? _instance;
    private string _gameDirectory = string.Empty;
    private IReadOnlyList<string> _availableEntries = [];
    private bool _hasModLoader;
    private bool _hasOptiFine;
    private List<string>? _rulesOverrides;

    public PageInstanceExportRight()
    {
        AvaloniaXamlLoader.Load(this);
        PanScroll = this.FindControl<MyScrollViewer>("PanBack");
        WireWpfCopiedControls();
        SyncRulesOverrideUi();
    }

    public event EventHandler<InstanceExportPageRequest>? ExportRequested;

    public event EventHandler? ImportConfigRequested;

    public event EventHandler<IReadOnlyList<string>>? ExportConfigRequested;

    public void SetInstance(LaunchInstanceInfo instance)
    {
        bool changed = _instance is null ||
                       !string.Equals(_instance.InstanceDirectory, instance.InstanceDirectory, StringComparison.OrdinalIgnoreCase);
        _instance = instance;
        if (changed)
        {
            _gameDirectory = ResolveGameDirectory(instance);
            RefreshAll();
        }
    }

    public void RefreshAll()
    {
        if (_instance is null)
            return;

        _gameDirectory = ResolveGameDirectory(_instance);
        MinecraftVersionJsonInfo versionInfo = MinecraftVersionJsonInspector.Read(_instance);
        _hasModLoader = versionInfo.Libraries.Any(IsModLoaderLibrary) || HasModLoaderFile(_instance);
        _hasOptiFine = versionInfo.Libraries.Any(static library =>
                            library.Contains("optifine", StringComparison.OrdinalIgnoreCase)) ||
                       HasFileNamePart(_instance.InstanceDirectory, "optifine");
        if (this.FindControl<Control>("HintOptiFine") is { } optiFineHint)
            optiFineHint.IsVisible = _hasOptiFine;

        if (this.FindControl<MyTextBox>("TextExportName") is { } nameBox)
        {
            nameBox.Text = string.Empty;
            nameBox.HintText = _instance.Name;
        }

        if (this.FindControl<MyTextBox>("TextExportVersion") is { } versionBox)
        {
            versionBox.Text = string.Empty;
            versionBox.HintText = "1.0.0";
        }

        if (this.FindControl<MyCheckBox>("CheckAdvancedInclude") is { } include)
            include.Checked = false;
        if (this.FindControl<MyCheckBox>("CheckAdvancedModrinth") is { } modrinth)
            modrinth.Checked = false;

        _rulesOverrides = null;
        SyncRulesOverrideUi();
        ReloadAllSubOptions();
        _availableEntries = BuildAvailableEntries();
        RefreshAllOptionsUI();
        PanScroll?.ScrollToHome();
    }

    public void ApplyRulesOverride(IEnumerable<string> rules)
    {
        _rulesOverrides = rules
            .Select(rule => rule.Trim())
            .Where(rule => !string.IsNullOrWhiteSpace(rule))
            .ToList();
        SyncRulesOverrideUi();
    }

    private void WireWpfCopiedControls()
    {
        if (this.FindControl<MyExtraTextButton>("BtnExport") is { } exportButton)
            exportButton.Click += (_, _) => StartExport();
        if (this.FindControl<MyButton>("BtnAdvancedImport") is { } importButton)
            importButton.Click += (_, _) => ImportConfigRequested?.Invoke(this, EventArgs.Empty);
        if (this.FindControl<MyButton>("BtnAdvancedExport") is { } exportConfigButton)
            exportConfigButton.Click += (_, _) => ExportConfigRequested?.Invoke(this, CollectRules(includeHidden: false));
        if (this.FindControl<MyTextBox>("TextExportName") is { } nameBox)
            nameBox.GotFocus += (_, _) => FillDefaultNameOnFocus();
        if (this.FindControl<MyIconTextButton>("BtnOverrideCancel") is { } overrideCancel)
            overrideCancel.Click += (_, _) => ResetConfigOverrides();
        WireVisibilityToggle("CheckOptionsMod", "PanOptionsMod");
        WireVisibilityToggle("CheckOptionsResourcePacks", "PanOptionsResourcePacks");
        WireVisibilityToggle("CheckOptionsShaderPacks", "PanOptionsShaderPacks");
        WireVisibilityToggle("CheckOptionsSaves", "PanOptionsSaves");
        WireVisibilityToggle("CheckOptionsOtherFolders", "PanOptionsOtherFolders");
        WireVisibilityToggle("CheckOptionsPcl", "PanOptionsPcl");
        WireVisibilityToggle("CheckAdvancedInclude", "HintAdvancedInclude");
        if (this.FindControl<MyCheckBox>("CheckOptionsOtherFolders") is { } otherFolders)
            otherFolders.Change += CheckOptionsOtherFolders_Change;
    }

    private void FillDefaultNameOnFocus()
    {
        if (this.FindControl<MyTextBox>("TextExportName") is not { } nameBox)
            return;

        if (!string.IsNullOrWhiteSpace(nameBox.Text))
            return;

        nameBox.Text = nameBox.HintText;
        nameBox.SelectionStart = nameBox.Text?.Length ?? 0;
    }

    private void StartExport()
    {
        if (_instance is null)
            return;

        string packageName = this.FindControl<MyTextBox>("TextExportName")?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(packageName))
            packageName = _instance.Name;

        string packageVersion = this.FindControl<MyTextBox>("TextExportVersion")?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(packageVersion))
            packageVersion = "1.0.0";
        bool includeLauncher = this.FindControl<MyCheckBox>("CheckOptionsPcl")?.Checked == true;

        ExportRequested?.Invoke(
            this,
            new InstanceExportPageRequest(
                _instance,
                packageName,
                packageVersion,
                CollectRules(includeHidden: false),
                includeLauncher,
                includeLauncher && this.FindControl<MyCheckBox>("CheckOptionsPclCustom")?.Checked == true,
                this.FindControl<MyCheckBox>("CheckAdvancedInclude")?.Checked == true,
                this.FindControl<MyCheckBox>("CheckAdvancedModrinth")?.Checked == true));
    }

    private void CheckAdvancedModrinth_Change(object sender, bool user)
    {
        if (this.FindControl<MyCheckBox>("CheckAdvancedModrinth") is not { } modrinth ||
            this.FindControl<MyCheckBox>("CheckOptionsPcl") is not { } launcher)
        {
            return;
        }

        if (modrinth.Checked == true)
            launcher.Checked = false;
        launcher.IsEnabled = modrinth.Checked != true;
    }

    private void CheckAdvancedInclude_Change(object sender, bool user)
    {
        if (this.FindControl<MyCheckBox>("CheckAdvancedInclude") is not { } include ||
            this.FindControl<MyCheckBox>("CheckAdvancedModrinth") is not { } modrinth)
        {
            return;
        }

        if (include.Checked == true)
            modrinth.Checked = false;
        modrinth.IsEnabled = include.Checked != true;
    }

    private void CheckOptionsOtherFolders_Change(object sender, bool user)
    {
        if (!user ||
            this.FindControl<MyCheckBox>("CheckOptionsOtherFolders") is not { } parent ||
            this.FindControl<StackPanel>("PanOptionsOtherFolders") is not { } panel)
        {
            return;
        }

        foreach (MyCheckBox child in panel.Children.OfType<MyCheckBox>())
            child.Checked = parent.Checked;
    }

    private void ReloadAllSubOptions()
    {
        ReloadSubOptions("PanOptionsResourcePacks", acceptCompressedFile: true, acceptFolder: true,
            "resourcepacks", "texturepacks");
        ReloadSubOptions("PanOptionsSaves", acceptCompressedFile: false, acceptFolder: true, "saves");
        ReloadSubOptions("PanOptionsShaderPacks", acceptCompressedFile: true, acceptFolder: true, "shaderpacks");
        ReloadOtherFolders();
    }

    private void ReloadSubOptions(
        string panelName,
        bool acceptCompressedFile,
        bool acceptFolder,
        params string[] folders)
    {
        if (this.FindControl<StackPanel>(panelName) is not { } panel)
            return;

        panel.Children.Clear();
        foreach (string folderName in folders)
        {
            string targetPath = Path.Combine(_gameDirectory, folderName);
            if (!Directory.Exists(targetPath))
                continue;

            if (acceptCompressedFile)
            {
                IEnumerable<FileInfo> archives = Directory.EnumerateFiles(targetPath, "*", SearchOption.TopDirectoryOnly)
                    .Select(static path => new FileInfo(path))
                    .Where(static file => file.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                                          file.Extension.Equals(".rar", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(static file => file.Name, StringComparer.OrdinalIgnoreCase);
                foreach (FileInfo archive in archives)
                {
                    if (IsBlacklistedSubOption(archive.Name))
                        continue;
                    panel.Children.Add(CreateDynamicOption(
                        archive.Name,
                        EscapeRulePath(folderName, archive.Name),
                        defaultChecked: true));
                    if (folderName.Equals("shaderpacks", StringComparison.OrdinalIgnoreCase))
                        AddShaderConfigOption(panel, folderName, archive.Name, archive.FullName + ".txt");
                }
            }

            if (!acceptFolder)
                continue;

            IEnumerable<DirectoryInfo> subFolders = Directory.EnumerateDirectories(targetPath, "*", SearchOption.TopDirectoryOnly)
                .Select(static path => new DirectoryInfo(path))
                .OrderByDescending(static folder => folder.LastWriteTimeUtc);
            foreach (DirectoryInfo subFolder in subFolders)
            {
                if (IsBlacklistedSubOption(subFolder.Name) || !DirectoryHasEntries(subFolder.FullName))
                    continue;
                string? description = panelName == "PanOptionsSaves"
                    ? subFolder.LastWriteTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture)
                    : null;
                panel.Children.Add(CreateDynamicOption(
                    subFolder.Name,
                    EscapeRulePath(folderName, subFolder.Name) + "/",
                    defaultChecked: true,
                    description));
                if (folderName.Equals("shaderpacks", StringComparison.OrdinalIgnoreCase))
                {
                    AddShaderConfigOption(
                        panel,
                        folderName,
                        subFolder.Name,
                        Path.Combine(targetPath, subFolder.Name + ".txt"));
                }
            }
        }
    }

    private void AddShaderConfigOption(
        StackPanel panel,
        string folderName,
        string itemName,
        string configPath)
    {
        if (!File.Exists(configPath))
            return;
        string configName = itemName + ".txt";
        panel.Children.Add(CreateDynamicOption(
            configName,
            EscapeRulePath(folderName, configName),
            defaultChecked: true,
            ResolveOptionText(this, "Instance.Export.Config.ShaderConfigSuffix", "光影配置文件"),
            new Thickness(30d, 0d, 0d, 0d)));
    }

    private void ReloadOtherFolders()
    {
        if (this.FindControl<StackPanel>("PanOptionsOtherFolders") is not { } panel ||
            this.FindControl<MyCheckBox>("CheckOptionsOtherFolders") is not { } parent)
        {
            return;
        }

        panel.Children.Clear();
        if (!Directory.Exists(_gameDirectory))
        {
            parent.IsVisible = false;
            return;
        }

        HashSet<string> coveredFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            "mods", "coremods", "lib",
            "addons", "multiblocked", "modpack-update-checker", "global_packs",
            "global_resource_packs", "global_data_packs", "optional_data_packs", "maps",
            "mods-resourcepacks", "matmos", "resource_assorts", "patchouli_books", "datapacks",
            "openloader", "worldshape", "resources", "scripts", "structures", "fontfiles",
            "oresources", "packmenu", "craftpresence", "pointblanks",
            "config", "defaultconfigs", "journeymap", "local", "essential", "gg.essential.mod",
            "CustomSkinLoader", "xaero", "XaeroWaypoints", "XaeroWorldMap",
            "resourcepacks", "texturepacks", "shaderpacks", "screenshots", "schematics",
            "replay_recordings", "replay_videos", "saves", "configureddefaults",
            "assets", "versions", "libraries", "structureCacheV1", ".fabric", ".git",
            "avatar-cache", "cosmetic-cache", "PCL"
        };

        foreach (string directoryPath in Directory.EnumerateDirectories(_gameDirectory, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(directoryPath);
            if (coveredFolders.Contains(name) ||
                name.StartsWith("kubejs", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("template", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("-natives", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            panel.Children.Add(CreateDynamicOption(
                name,
                InstanceExportService.EscapeLiteralRulePath(name) + "/",
                defaultChecked: false));
        }

        parent.IsVisible = panel.Children.Count > 0;
    }

    private static MyCheckBox CreateDynamicOption(
        string title,
        string rules,
        bool defaultChecked,
        string? description = null,
        Thickness? margin = null) =>
        new()
        {
            Margin = margin ?? default,
            Tag = new ExportOption
            {
                Title = title,
                Description = description ?? string.Empty,
                Rules = rules,
                DefaultChecked = defaultChecked
            }
        };

    private static string EscapeRulePath(string folderName, string entryName) =>
        InstanceExportService.EscapeLiteralRulePath(folderName) + "/" +
        InstanceExportService.EscapeLiteralRulePath(entryName);

    private static bool IsBlacklistedSubOption(string name) =>
        SubOptionBlackList.Any(value => name.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static bool DirectoryHasEntries(string path)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private List<string> BuildAvailableEntries()
    {
        if (!Directory.Exists(_gameDirectory))
            return [];

        List<string> entries = [];
        try
        {
            foreach (string file in Directory.EnumerateFiles(_gameDirectory, "*", SearchOption.TopDirectoryOnly))
                entries.Add(Path.GetFileName(file));
            foreach (string directoryPath in Directory.EnumerateDirectories(_gameDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                string directoryName = Path.GetFileName(directoryPath);
                entries.Add(directoryName + "/");
                try
                {
                    foreach (string child in Directory.EnumerateFileSystemEntries(directoryPath, "*", SearchOption.TopDirectoryOnly))
                    {
                        entries.Add(directoryName + "/" + Path.GetFileName(child) +
                                    (Directory.Exists(child) ? "/" : string.Empty));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return entries;
    }

    private void RefreshAllOptionsUI()
    {
        foreach (MyCheckBox checkBox in GetAllOptions(includeHidden: true))
        {
            if (checkBox.Tag is not ExportOption option)
                continue;

            checkBox.Height = 26d;
            checkBox.Inlines.Clear();
            string title = ResolveOptionText(checkBox, option.TitleResourceKey, option.Title);
            string description = ResolveOptionText(checkBox, option.DescriptionResourceKey, option.Description);
            checkBox.Inlines.Add(new Run(title));
            if (!string.IsNullOrWhiteSpace(description))
            {
                checkBox.Inlines.Add(new Run("   " + description)
                {
                    Foreground = LegacyResourceResolver.Brush(checkBox, "ColorBrushGray5", "#9aa0a6")
                });
            }

            bool visible = ShouldShowOption(option);
            checkBox.IsVisible = visible;
            checkBox.Checked = option.DefaultChecked && visible;
        }
        SyncDependentVisibility();
    }

    private static string ResolveOptionText(Control owner, string? resourceKey, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(resourceKey) &&
            LegacyResourceResolver.TryResolve(owner, resourceKey, out object? resource) &&
            resource is not null)
        {
            return resource.ToString() ?? fallback;
        }

        return fallback;
    }

    private void WireVisibilityToggle(string checkBoxName, string targetName)
    {
        if (this.FindControl<MyCheckBox>(checkBoxName) is not { } checkBox)
            return;

        checkBox.Change += (_, _) => SyncVisibility(checkBoxName, targetName);
        SyncVisibility(checkBoxName, targetName);
    }

    private void SyncDependentVisibility()
    {
        SyncVisibility("CheckOptionsMod", "PanOptionsMod");
        SyncVisibility("CheckOptionsResourcePacks", "PanOptionsResourcePacks");
        SyncVisibility("CheckOptionsShaderPacks", "PanOptionsShaderPacks");
        SyncVisibility("CheckOptionsSaves", "PanOptionsSaves");
        SyncVisibility("CheckOptionsOtherFolders", "PanOptionsOtherFolders");
        SyncVisibility("CheckOptionsPcl", "PanOptionsPcl");
        SyncVisibility("CheckAdvancedInclude", "HintAdvancedInclude");
    }

    private void SyncVisibility(string checkBoxName, string targetName)
    {
        if (this.FindControl<MyCheckBox>(checkBoxName) is not { } checkBox ||
            this.FindControl<Control>(targetName) is not { } target)
        {
            return;
        }

        target.IsVisible = checkBox.Checked == true;
    }

    private void ResetConfigOverrides()
    {
        _rulesOverrides = null;
        SyncRulesOverrideUi();
        RefreshAllOptionsUI();
    }

    private void SyncRulesOverrideUi()
    {
        bool hasOverride = _rulesOverrides is not null;
        if (this.FindControl<MyIconTextButton>("BtnOverrideCancel") is { } overrideCancel)
        {
            overrideCancel.IsVisible = hasOverride;
            overrideCancel.IsHitTestVisible = hasOverride;
            overrideCancel.Opacity = hasOverride ? 1d : 0d;
        }

        if (this.FindControl<Panel>("PanOptions") is { } options)
            options.IsVisible = !hasOverride;

        if (this.FindControl<MyCard>("CardOptions") is { } card)
        {
            card.Inlines.Clear();
            card.Inlines.Add(new Run(hasOverride ? "导出内容：来自配置文件" : "导出内容"));
        }
    }

    private bool ShouldShowOption(ExportOption option)
    {
        if (_instance is null)
            return false;

        if (option.RequireOptiFine && !_hasOptiFine)
            return false;
        if (option.RequireModLoader && !_hasModLoader)
            return false;
        if (option.RequireModLoaderOrOptiFine && !_hasOptiFine && !_hasModLoader)
            return false;

        string? showRules = option.Rules ?? option.ShowRules;
        if (string.IsNullOrWhiteSpace(showRules))
            return true;

        foreach (string rule in SplitRules(showRules))
        {
            if (rule.StartsWith('!'))
                continue;
            if (_availableEntries.Any(entry => InstanceExportService.RuleMatches(entry, rule)))
                return true;
        }

        return false;
    }

    private List<string> CollectRules(bool includeHidden)
    {
        if (_rulesOverrides is not null)
            return [.. _rulesOverrides];

        List<string> rules = [];
        foreach (MyCheckBox checkBox in GetAllOptions(includeHidden))
        {
            if (checkBox.Checked != true || checkBox.Tag is not ExportOption option || string.IsNullOrWhiteSpace(option.Rules))
                continue;

            rules.AddRange(SplitRules(option.Rules));
        }

        rules.AddRange(DefaultExcludeRules);

        return rules;
    }

    private IEnumerable<MyCheckBox> GetAllOptions(bool includeHidden)
    {
        if (this.FindControl<Panel>("PanOptions") is not { } panel)
            yield break;

        foreach (Control child in EnumerateOptionControls(panel, includeHidden))
        {
            if (child is MyCheckBox checkBox && (includeHidden || checkBox.IsVisible))
                yield return checkBox;
        }
    }

    private static IEnumerable<Control> EnumerateOptionControls(Panel panel, bool includeHidden)
    {
        foreach (Control child in panel.Children)
        {
            if (!includeHidden && !child.IsVisible)
                continue;
            yield return child;
            if (child is Panel childPanel)
            {
                foreach (Control nested in EnumerateOptionControls(childPanel, includeHidden))
                    yield return nested;
            }
        }
    }

    private static IEnumerable<string> SplitRules(string rules)
    {
        foreach (string raw in rules.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(raw))
                yield return raw;
        }
    }

    private static bool HasModLoaderFile(LaunchInstanceInfo instance) =>
        HasFileNamePart(instance.InstanceDirectory, "forge") ||
        HasFileNamePart(instance.InstanceDirectory, "fabric") ||
        HasFileNamePart(instance.InstanceDirectory, "quilt") ||
        HasFileNamePart(instance.InstanceDirectory, "neoforge");

    private static bool HasFileNamePart(string folder, string part)
    {
        if (!Directory.Exists(folder))
            return false;

        return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Any(file => Path.GetFileName(file).Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsModLoaderLibrary(string library) =>
        library.Contains("net.minecraftforge:forge", StringComparison.OrdinalIgnoreCase) ||
        library.Contains("net.neoforged:neoforge", StringComparison.OrdinalIgnoreCase) ||
        library.Contains("net.neoforge:forge", StringComparison.OrdinalIgnoreCase) ||
        library.Contains("fabric-loader", StringComparison.OrdinalIgnoreCase) ||
        library.Contains("quilt-loader", StringComparison.OrdinalIgnoreCase) ||
        library.Contains("liteloader", StringComparison.OrdinalIgnoreCase);

    private static string ResolveGameDirectory(LaunchInstanceInfo instance)
    {
        try
        {
            return InstanceGameDirectory.ResolveAsync(instance).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return instance.InstanceDirectory;
        }
    }
}
