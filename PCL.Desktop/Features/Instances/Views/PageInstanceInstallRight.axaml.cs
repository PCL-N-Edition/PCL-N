// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using System.Globalization;
using PCL.Application.Downloads;
using PCL.Application.Instances;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Diagnostics;
using PCL.Desktop.Features.Launching.Views;
using PCL.Desktop.Features.Shared;

namespace PCL.Desktop.Features.Instances.Views;

public sealed record InstanceInstallModifyRequest(
    LaunchInstanceInfo Instance,
    string MinecraftVersionId,
    MinecraftLoaderKind? LoaderKind = null,
    string? LoaderVersion = null,
    MinecraftInstallAddonKind? AddonKind = null,
    MinecraftLoaderKind? CurrentLoaderKind = null,
    string? CurrentLoaderVersion = null,
    string? CurrentOptiFineVersion = null,
    bool ApplySelection = false);

public partial class PageInstanceInstallRight : MyPageRight
{
    private static readonly MinecraftVersionCategory[] VersionCategoryOrder =
    [
        MinecraftVersionCategory.Release,
        MinecraftVersionCategory.Snapshot,
        MinecraftVersionCategory.BeforeRelease,
        MinecraftVersionCategory.AprilFools
    ];

    private readonly MinecraftVanillaInstallService _installService;
    private readonly IMinecraftLoaderMetadataService _loaderMetadataService;
    private readonly Dictionary<(MinecraftLoaderKind Kind, string GameVersion), IReadOnlyList<MinecraftLoaderVersionEntry>> _loaderVersionCache = [];
    private readonly Dictionary<(MinecraftLoaderKind Kind, string GameVersion), Task<IReadOnlyList<MinecraftLoaderVersionEntry>>> _loaderVersionLoads = [];
    private IReadOnlyList<MinecraftVersionManifestEntry> _versions = [];
    private LaunchInstanceInfo? _instance;
    private string _selectedMinecraftVersionId = string.Empty;
    private string _selectedMinecraftLogo = BlockAssetRoot + "Grass.png";
    private MinecraftLoaderKind? _selectedLoaderKind;
    private MinecraftLoaderVersionEntry? _selectedLoaderVersion;
    /// <summary>
    /// OptiFine selected as a companion addon (Forge / LiteLoader), matching
    /// <c>PageDownloadInstall._selectedOptiFineAddon</c>.
    /// </summary>
    private MinecraftLoaderVersionEntry? _selectedOptiFineAddon;
    private bool _isLoadingMinecraftVersions;

    public PageInstanceInstallRight()
        : this(new MinecraftVanillaInstallService(), new MinecraftLoaderMetadataService())
    {
    }

    public PageInstanceInstallRight(
        MinecraftVanillaInstallService installService,
        IMinecraftLoaderMetadataService? loaderMetadataService = null)
    {
        _installService = installService;
        _loaderMetadataService = loaderMetadataService ?? new MinecraftLoaderMetadataService();
        AvaloniaXamlLoader.Load(this);
        PanScroll = this.FindControl<MyScrollViewer>("PanBack");
        WireWpfCopiedControls();
        HideLoading();
        HideAllHints();
    }

    public event EventHandler<InstanceInstallModifyRequest>? ModifyRequested;

    public void SetInstance(LaunchInstanceInfo instance)
    {
        _instance = instance;
        RefreshAll();
    }

    public void RefreshAll()
    {
        if (_instance is null)
            return;

        MinecraftVersionJsonInfo versionInfo = MinecraftVersionJsonInspector.Read(_instance);
        _selectedMinecraftVersionId = versionInfo.MinecraftVersionId;
        _selectedMinecraftLogo = BlockAssetRoot + GetVersionLogoImageName("release");
        InitializeSelectedLoader(_instance);
        PanScroll?.ScrollToHome();
        ApplySelectPageState();
        PopulateSelectedInstance(_instance);
        InitializeLoaderCards(_instance);
        HideAllHints();
        HideLoading();
    }

    private bool _startButtonWired;

    private void WireWpfCopiedControls()
    {
        if (this.FindControl<MyExtraTextButton>("BtnSelectStart") is not { } startButton || _startButtonWired)
            return;

        _startButtonWired = true;
        startButton.Show = true;
        startButton.IsEnabled = true;
        startButton.Click += (_, _) =>
        {
            if (_instance is null)
                return;

            // Reinstall / re-apply keeps the existing instance directory name via the
            // download-install bridge (preserveInstallNameOnLoaderSelect + replaceExisting).
            StartSelectedInstall();
        };
    }

    /// <summary>
    /// Pushes the current selection into PageDownloadInstall through
    /// <see cref="InstanceInstallModifyRequest"/> — same apply path as install-page
    /// StartSelectedInstall, with replace-existing naming owned by the bridge.
    /// </summary>
    private void StartSelectedInstall()
    {
        if (_instance is null)
            return;

        ModifyRequested?.Invoke(
            this,
            new InstanceInstallModifyRequest(
                _instance,
                _selectedMinecraftVersionId,
                _selectedLoaderKind,
                _selectedLoaderVersion?.Version,
                CurrentOptiFineVersion: _selectedOptiFineAddon?.Version,
                ApplySelection: true));
    }

    private void ApplySelectPageState()
    {
        if (this.FindControl<Control>("PanMinecraft") is { } minecraft)
        {
            minecraft.IsVisible = false;
            minecraft.IsHitTestVisible = false;
            minecraft.Opacity = 0d;
            ResetTranslateX(minecraft);
        }

        if (this.FindControl<Control>("PanSelect") is { } select)
        {
            select.IsVisible = true;
            select.IsHitTestVisible = true;
            select.Opacity = 1d;
            ResetTranslateX(select);
        }

        if (this.FindControl<MyScrollViewer>("PanBack") is { } scroll)
        {
            scroll.IsHitTestVisible = true;
            scroll.ScrollToHome();
        }

        if (this.FindControl<MyExtraTextButton>("BtnSelectStart") is { } startButton)
        {
            // Prefer non-animated re-show so re-entering the page cannot accumulate scale.
            if (!startButton.Show)
                startButton.Show = true;
            startButton.IsEnabled = true;
            startButton.Opacity = 1d;
            startButton.IsVisible = true;
        }
    }

    private void ApplyMinecraftPageState()
    {
        if (this.FindControl<Control>("PanSelect") is { } select)
        {
            select.IsVisible = false;
            select.IsHitTestVisible = false;
            select.Opacity = 0d;
            ResetTranslateX(select);
        }

        if (this.FindControl<Control>("PanMinecraft") is { } minecraft)
        {
            minecraft.IsVisible = true;
            minecraft.IsHitTestVisible = true;
            minecraft.Opacity = 1d;
            ResetTranslateX(minecraft);
        }

        if (this.FindControl<MyExtraTextButton>("BtnSelectStart") is { } startButton)
            startButton.Show = false;

        if (this.FindControl<MyScrollViewer>("PanBack") is { } scroll)
        {
            scroll.IsHitTestVisible = true;
            scroll.ScrollToHome();
        }
    }

    private async Task EnsureMinecraftVersionsAsync()
    {
        if (_versions.Count > 0)
        {
            RenderMinecraftVersions();
            return;
        }

        if (_isLoadingMinecraftVersions)
            return;

        _isLoadingMinecraftVersions = true;
        SetMinecraftVersionListLoading();
        try
        {
            IReadOnlyList<MinecraftVersionManifestEntry> versions = await _installService
                .GetVersionManifestAsync(preferOfficialSource: true)
                .ConfigureAwait(false);
            await RunOnUiThreadAsync(() =>
            {
                _versions = versions;
                RenderMinecraftVersions();
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or FormatException)
        {
            await RunOnUiThreadAsync(() =>
                SetMinecraftVersionListMessage("Minecraft", "获取版本列表失败：" + ex.Message)).ConfigureAwait(false);
        }
        finally
        {
            _isLoadingMinecraftVersions = false;
        }
    }

    private void RenderMinecraftVersions()
    {
        if (this.FindControl<StackPanel>("PanMinecraft") is not { } panel)
            return;

        panel.Children.Clear();
        if (_versions.Count == 0)
        {
            SetMinecraftVersionListMessage("Minecraft", "暂时没有可选择的 Minecraft 版本。");
            return;
        }

        MinecraftVersionView[] views = BuildMinecraftVersionViews(_versions);
        Dictionary<MinecraftVersionCategory, List<MinecraftVersionView>> categories = CreateVersionDictionary(views);
        AddLatestMinecraftVersionCard(panel, categories);
        foreach (MinecraftVersionCategory category in VersionCategoryOrder)
        {
            IReadOnlyList<MinecraftVersionView> versions = categories[category];
            if (versions.Count == 0)
                continue;

            panel.Children.Add(CreateMinecraftVersionCard(
                GetVersionCategoryTitle(category) + " (" + versions.Count.ToString(CultureInfo.CurrentCulture) + ")",
                versions,
                isSwapped: true,
                margin: new Thickness(0d, 0d, 0d, 15d)));
        }
    }

    private void AddLatestMinecraftVersionCard(
        StackPanel panel,
        IReadOnlyDictionary<MinecraftVersionCategory, List<MinecraftVersionView>> categories)
    {
        MinecraftVersionView? latestRelease = categories[MinecraftVersionCategory.Release].FirstOrDefault();
        MinecraftVersionView? latestSnapshot = categories[MinecraftVersionCategory.Snapshot].FirstOrDefault();
        List<MinecraftVersionView> latest = [];

        if (latestRelease is not null)
        {
            latest.Add(latestRelease with
            {
                Info = ResourceText(
                    "Download.Version.Latest.Release",
                    "最新正式版，发布于 {0}",
                    FormatReleaseTime(latestRelease.ReleaseTime))
            });
        }

        if (latestSnapshot is not null &&
            (latestRelease is null ||
             (latestSnapshot.ReleaseTime ?? DateTimeOffset.MinValue) > (latestRelease.ReleaseTime ?? DateTimeOffset.MinValue)))
        {
            latest.Add(latestSnapshot with
            {
                Info = ResourceText(
                    "Download.Version.Latest.Development",
                    "最新预览版，发布于 {0}",
                    FormatReleaseTime(latestSnapshot.ReleaseTime))
            });
        }

        if (latest.Count == 0)
            return;

        panel.Children.Add(CreateMinecraftVersionCard(
            ResourceText("Download.Version.Latest.Title", "最新版本"),
            latest,
            isSwapped: false,
            margin: new Thickness(0d, 15d, 0d, 15d)));
    }

    private MyCard CreateMinecraftVersionCard(
        string title,
        IReadOnlyList<MinecraftVersionView> versions,
        bool isSwapped,
        Thickness margin)
    {
        StackPanel stack = new()
        {
            Margin = new Thickness(20d, MyCard.SwapedHeight, 18d, 0d),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            RenderTransform = new TranslateTransform(),
            Tag = versions
        };
        MyCard card = new()
        {
            Title = title,
            Margin = margin,
            SwapControl = stack,
            InstallMethod = InstallMinecraftVersionItems,
            IsSwapped = isSwapped
        };
        card.Children.Add(stack);
        MyCard.StackInstall(ref stack, InstallMinecraftVersionItems);
        return card;
    }

    private void InstallMinecraftVersionItems(StackPanel stack)
    {
        if (stack.Tag is not IReadOnlyList<MinecraftVersionView> versions)
            return;

        foreach (MinecraftVersionView version in versions)
            stack.Children.Add(CreateMinecraftVersionItem(version));
    }

    private MyListItem CreateMinecraftVersionItem(MinecraftVersionView version)
    {
        MyListItem item = new()
        {
            Title = version.Title,
            Info = version.Info,
            Type = MyListItem.CheckType.Clickable,
            Logo = version.Logo,
            LogoScale = 1d,
            Height = 42d,
            Margin = new Thickness(0, 0, 0, 2),
            Tag = version.Manifest
        };
        item.Click += (_, _) => SelectMinecraftVersion(version);
        return item;
    }

    private void SelectMinecraftVersion(MinecraftVersionView version)
    {
        _selectedMinecraftVersionId = version.Manifest.Id;
        _selectedMinecraftLogo = version.Logo;
        if (this.FindControl<TextBlock>("LabMinecraft") is { } label)
            label.Text = version.Manifest.Id;
        if (this.FindControl<Image>("ImgMinecraft") is { } image)
        {
            image.Source = LoadImage(version.Logo) ?? LoadBlockImage("Grass.png");
            image.Tag = version.Logo;
        }

        // Match PageDownloadInstall.SelectVersion: changing MC clears loader/addon selection.
        ResetSelectedLoader();
        CollapseLoaderCards();
        ApplyBlankLoaderCards(version.Manifest.Id);
        SetLoaderCardVisible("FabricApi", false);
        SetLoaderCardVisible("LegacyFabricApi", false);
        SetLoaderCardVisible("QSL", false);
        SetLoaderCardVisible("OptiFabric", false);
        ApplySelectedInstanceSummary(version.Manifest.Id, null, null, null, null, null, null, null, null, null);
        ApplySelectPageState();
    }

    private void ApplyBlankLoaderCards(string minecraftVersionId)
    {
        SetLoaderInfo("Forge", null, "Anvil.png");
        SetLoaderInfo("Cleanroom", null, "Cleanroom.png");
        SetLoaderInfo("NeoForge", null, "NeoForge.png");
        SetLoaderInfo("Fabric", null, "Fabric.png");
        SetLoaderInfo("LegacyFabric", null, "Fabric.png");
        SetLoaderInfo("FabricApi", null, "Fabric.png");
        SetLoaderInfo("LegacyFabricApi", null, "Fabric.png");
        SetLoaderInfo("Quilt", null, "Quilt.png");
        SetLoaderInfo("QSL", null, "Quilt.png");
        SetLoaderInfo("LabyMod", null, "LabyMod.png");
        SetLoaderInfo("OptiFine", null, "GrassPath.png");
        SetLoaderInfo("OptiFabric", null, "OptiFabric.png");
        SetLoaderInfo("LiteLoader", null, "Egg.png");
        ApplyLoaderCardVisibility(minecraftVersionId);
    }

    private void SetMinecraftVersionListMessage(string title, string message)
    {
        if (this.FindControl<StackPanel>("PanMinecraft") is not { } panel)
            return;

        panel.Children.Clear();
        MyCard card = new()
        {
            Title = title,
            Margin = new Thickness(0d, 15d, 0d, 15d),
            UseAnimation = false
        };
        card.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 13.5d,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(25d, 38d, 23d, 16d)
        });
        panel.Children.Add(card);
    }

    private void SetMinecraftVersionListLoading()
    {
        if (this.FindControl<StackPanel>("PanMinecraft") is not { } panel)
            return;

        panel.Children.Clear();
        panel.Children.Add(new MyLoading
        {
            Text = "正在获取 Minecraft 版本列表",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Thickness(0d, 28d, 0d, 18d)
        });
    }

    private static MinecraftVersionView[] BuildMinecraftVersionViews(IReadOnlyList<MinecraftVersionManifestEntry> versions)
    {
        MinecraftVersionView[] views = new MinecraftVersionView[versions.Count];
        for (int i = 0; i < versions.Count; i++)
            views[i] = CreateMinecraftVersionView(versions[i]);
        return views;
    }

    private static MinecraftVersionView CreateMinecraftVersionView(MinecraftVersionManifestEntry version)
    {
        MinecraftVersionClassification classification = MinecraftVersionCatalogClassifier.Classify(version);
        string id = classification.Id;
        string type = classification.Type;
        string title = MinecraftVersionCatalogClassifier.FormatVersion(id).Replace("_", " ", StringComparison.Ordinal);
        string info = string.Equals(title, id, StringComparison.Ordinal)
            ? FormatReleaseTime(version.ReleaseTime)
            : $"{FormatReleaseTime(version.ReleaseTime)} | {id}";
        MinecraftVersionManifestEntry manifest = version with
        {
            Id = id,
            Type = type
        };

        return new MinecraftVersionView(
            manifest,
            title,
            info,
            version.ReleaseTime,
            classification.Category,
            BlockAssetRoot + GetVersionLogoImageName(type));
    }

    private static Dictionary<MinecraftVersionCategory, List<MinecraftVersionView>> CreateVersionDictionary(
        IReadOnlyList<MinecraftVersionView> versions)
    {
        Dictionary<MinecraftVersionCategory, List<MinecraftVersionView>> categories = VersionCategoryOrder.ToDictionary(
            category => category,
            _ => new List<MinecraftVersionView>());
        foreach (MinecraftVersionView version in versions)
            categories[version.Category].Add(version);

        foreach (MinecraftVersionCategory category in VersionCategoryOrder)
            categories[category] = categories[category]
                .OrderByDescending(version => version.ReleaseTime ?? DateTimeOffset.MinValue)
                .ToList();

        return categories;
    }

    private string GetVersionCategoryTitle(MinecraftVersionCategory category) =>
        category switch
        {
            MinecraftVersionCategory.Release => ResourceText("Download.Version.Type.Release", "正式版"),
            MinecraftVersionCategory.Snapshot => ResourceText("Download.Version.Type.Development", "预览版"),
            MinecraftVersionCategory.BeforeRelease => ResourceText("Download.Version.Type.BeforeRelease", "远古版"),
            MinecraftVersionCategory.AprilFools => ResourceText("Download.Version.Type.AprilFools", "愚人节版"),
            _ => category.ToString()
        };

    private static string FormatReleaseTime(DateTimeOffset? releaseTime) =>
        releaseTime?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "未知日期";

    private static string GetVersionLogoImageName(string type)
    {
        return type.ToLowerInvariant() switch
        {
            "release" => "Grass.png",
            "snapshot" or "pending" => "CommandBlock.png",
            "special" => "GoldBlock.png",
            _ => "CobbleStone.png"
        };
    }

    private void PopulateSelectedInstance(LaunchInstanceInfo instance)
    {
        InstanceMetadata metadata = InstanceMetadataStore.LoadAsync(instance.InstanceDirectory).GetAwaiter().GetResult();
        string logo = InstanceDisplayHelper.ResolveLogo(instance, metadata);
        if (this.FindControl<MyListItem>("ItemSelect") is { } item)
        {
            item.Title = instance.Name;
            item.Logo = logo;
        }

        if (this.FindControl<TextBlock>("LabMinecraft") is { } label)
            label.Text = _selectedMinecraftVersionId;

        if (this.FindControl<Image>("ImgMinecraft") is { } image)
        {
            image.Source = LoadImage(_selectedMinecraftLogo) ?? LoadImage(logo) ?? LoadBlockImage("Grass.png");
            image.Tag = _selectedMinecraftLogo;
        }
    }

    private void InitializeSelectedLoader(LaunchInstanceInfo instance)
    {
        (MinecraftLoaderKind? kind, string? version, string? optiFineVersion) =
            DetectCurrentLoaderSelection(instance);
        _selectedOptiFineAddon = null;
        if (kind is not null && !string.IsNullOrWhiteSpace(version))
        {
            _selectedLoaderKind = kind;
            _selectedLoaderVersion = new MinecraftLoaderVersionEntry(kind.Value, version, true);
            if (!string.IsNullOrWhiteSpace(optiFineVersion) && CanCombineWithOptiFine(kind.Value))
            {
                _selectedOptiFineAddon = new MinecraftLoaderVersionEntry(
                    MinecraftLoaderKind.OptiFine,
                    optiFineVersion,
                    true);
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(optiFineVersion))
        {
            _selectedLoaderKind = MinecraftLoaderKind.OptiFine;
            _selectedLoaderVersion = new MinecraftLoaderVersionEntry(
                MinecraftLoaderKind.OptiFine,
                optiFineVersion,
                true);
            return;
        }

        _selectedLoaderKind = null;
        _selectedLoaderVersion = null;
    }

    private void InitializeLoaderCards(LaunchInstanceInfo instance)
    {
        CollapseLoaderCards();
        MinecraftVersionJsonInfo versionInfo = MinecraftVersionJsonInspector.Read(instance);
        string minecraftVersionId = versionInfo.MinecraftVersionId;
        IReadOnlyList<string> libraries = versionInfo.LoaderEntries;
        // NeoForge first — DetectLoader("forge") would also match "neoforge" filenames.
        string? neoForge = MinecraftLoaderLibraryDetector.DetectVersion(
                               libraries,
                               "net.neoforged:neoforge:",
                               "net.neoforge:forge:")
                           ?? DetectLoader(instance, "neoforge");
        string? forge = string.IsNullOrWhiteSpace(neoForge)
            ? MinecraftLoaderLibraryDetector.DetectVersion(libraries, "net.minecraftforge:forge:")
              ?? DetectLoaderExcluding(instance, include: "forge", exclude: "neoforge")
            : null;
        string? cleanroom = MinecraftLoaderLibraryDetector.DetectVersion(libraries, "com.cleanroommc:cleanroom:", "cleanroom") ?? DetectLoader(instance, "cleanroom");
        bool hasLegacyFabricLibraries = libraries.Any(library => library.Contains("net.legacyfabric:", StringComparison.OrdinalIgnoreCase));
        string? fabricLoaderVersion = MinecraftLoaderLibraryDetector.DetectVersion(libraries, "net.fabricmc:fabric-loader:");
        string? fabric = hasLegacyFabricLibraries ? null : fabricLoaderVersion ?? DetectLoader(instance, "fabric-loader", "fabric");
        string? legacyFabric = hasLegacyFabricLibraries ? fabricLoaderVersion ?? DetectLoader(instance, "legacyfabric") : null;
        string? fabricApi = DetectModFile(instance, "fabric-api");
        string? legacyFabricApi = DetectModFile(instance, "legacy-fabric-api");
        string? quilt = MinecraftLoaderLibraryDetector.DetectVersion(libraries, "org.quiltmc:quilt-loader:") ?? DetectLoader(instance, "quilt");
        string? qsl = DetectModFile(instance, "qsl", "quilted-fabric-api");
        string? labyMod = MinecraftLoaderLibraryDetector.DetectVersion(libraries, "labymod") ?? DetectLoader(instance, "labymod");
        string? optiFine = MinecraftLoaderLibraryDetector.DetectVersion(libraries, "optifine") ?? DetectLoader(instance, "optifine");
        string? optiFabric = DetectModFile(instance, "optifabric");
        string? liteLoader = MinecraftLoaderLibraryDetector.DetectVersion(libraries, "liteloader") ?? DetectLoader(instance, "liteloader");

        SetLoaderInfo("Forge", forge, "Anvil.png");
        SetLoaderInfo("Cleanroom", cleanroom, "Cleanroom.png");
        SetLoaderInfo("NeoForge", neoForge, "NeoForge.png");
        SetLoaderInfo("Fabric", fabric, "Fabric.png");
        SetLoaderInfo("LegacyFabric", legacyFabric, "Fabric.png");
        SetLoaderInfo("FabricApi", fabricApi, "Fabric.png");
        SetLoaderInfo("LegacyFabricApi", legacyFabricApi, "Fabric.png");
        SetLoaderInfo("Quilt", quilt, "Quilt.png");
        SetLoaderInfo("QSL", qsl, "Quilt.png");
        SetLoaderInfo("LabyMod", labyMod, "LabyMod.png");
        SetLoaderInfo("OptiFine", optiFine, "GrassPath.png");
        SetLoaderInfo("OptiFabric", optiFabric, "OptiFabric.png");
        SetLoaderInfo("LiteLoader", liteLoader, "Egg.png");
        ApplyLoaderCardVisibility(minecraftVersionId);
        SetLoaderCardVisible("FabricApi", fabric is not null || quilt is not null);
        SetLoaderCardVisible("LegacyFabricApi", legacyFabric is not null);
        SetLoaderCardVisible("QSL", quilt is not null);
        SetLoaderCardVisible("OptiFabric", (fabric is not null || legacyFabric is not null) && optiFine is not null);
        ApplySelectedInstanceSummary(
            minecraftVersionId,
            fabric,
            legacyFabric,
            quilt,
            forge,
            neoForge,
            cleanroom,
            labyMod,
            optiFine,
            liteLoader);
    }

    private void SetLoaderInfo(string name, string? detectedVersion, string imageName)
    {
        bool installed = !string.IsNullOrWhiteSpace(detectedVersion);
        if (this.FindControl<TextBlock>("Lab" + name) is { } label)
        {
            label.Text = installed ? detectedVersion : "可添加";
            label.Foreground = LegacyResourceResolver.Brush(label, "ColorBrushGray4", "#8c8c8c");
        }

        if (this.FindControl<Image>("Img" + name) is { } image)
        {
            image.Source = LoadBlockImage(imageName);
            image.IsVisible = installed;
        }

        if (this.FindControl<Control>("Btn" + name + "Clear") is { } clearButton)
            clearButton.IsVisible = installed;
    }

    private void CollapseLoaderCards()
    {
        foreach (MinecraftLoaderCardDescriptor loaderCard in MinecraftLoaderCardRegistry.AllCards)
        {
            string name = loaderCard.ControlSuffix;
            if (this.FindControl<MyCard>("Card" + name) is { } card)
                card.IsSwapped = true;
        }
    }

    private void ApplyLoaderCardVisibility(string minecraftVersionId)
    {
        int vanillaDrop = MinecraftVersionRuleHelper.VersionToDrop(minecraftVersionId, allowSnapshot: true);
        SetLoaderCardVisible("LiteLoader", vanillaDrop < 130);
        SetLoaderCardVisible("Forge", MinecraftVersionRuleHelper.IsFormatFit(minecraftVersionId));
        SetLoaderCardVisible("Cleanroom", string.Equals(minecraftVersionId, "1.12.2", StringComparison.OrdinalIgnoreCase));
        SetLoaderCardVisible("NeoForge", !(vanillaDrop is > 0 and < 200));
        SetLoaderCardVisible("Fabric", vanillaDrop > 130);
        SetLoaderCardVisible("LegacyFabric", vanillaDrop <= 130);
        SetLoaderCardVisible("Quilt", vanillaDrop >= 144);
        SetLoaderCardVisible("LabyMod", vanillaDrop >= 80);
    }

    private void SetLoaderCardVisible(string name, bool visible)
    {
        if (this.FindControl<MyCard>("Card" + name) is not { } card)
            return;

        card.IsVisible = visible;
        if (!visible)
            card.IsSwapped = true;
    }

    private void ApplySelectedInstanceSummary(
        string minecraftVersionId,
        string? fabric,
        string? legacyFabric,
        string? quilt,
        string? forge,
        string? neoForge,
        string? cleanroom,
        string? labyMod,
        string? optiFine,
        string? liteLoader)
    {
        if (this.FindControl<MyListItem>("ItemSelect") is not { } item)
            return;

        List<string> parts = [minecraftVersionId];
        AddInstallPart(parts, "Common.Installation.Fabric", "Fabric", fabric?.Replace("+build", string.Empty, StringComparison.Ordinal));
        AddInstallPart(parts, "Common.Installation.LegacyFabric", "Legacy Fabric", legacyFabric);
        AddInstallPart(parts, "Common.Installation.Quilt", "Quilt", quilt);
        AddInstallPart(parts, "Common.Installation.Forge", "Forge", forge);
        AddInstallPart(parts, "Common.Installation.NeoForge", "NeoForge", neoForge);
        AddInstallPart(parts, "Common.Installation.Cleanroom", "Cleanroom", cleanroom);
        AddInstallPart(parts, "Common.Installation.LabyMod", "LabyMod", labyMod);
        AddInstallPart(parts, "Common.Installation.OptiFine", "OptiFine", optiFine);

        if (!string.IsNullOrWhiteSpace(liteLoader))
            parts.Add(ResourceText("Common.Installation.LiteLoader", "LiteLoader"));
        if (parts.Count == 1)
            parts.Add(ResourceText("Instance.Install.NoExtraInstall", "无额外安装"));

        item.Info = string.Join("  |  ", parts);
    }

    private void AddInstallPart(List<string> parts, string nameKey, string fallbackName, string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return;

        parts.Add(ResourceText(nameKey, fallbackName) + " " + version);
    }

    private static void ResetTranslateX(Control control)
    {
        if (control.RenderTransform is TranslateTransform transform)
        {
            transform.X = 0d;
            return;
        }

        control.RenderTransform = new TranslateTransform();
    }

    private static string? DetectLoader(LaunchInstanceInfo instance, params string[] needles)
    {
        if (!Directory.Exists(instance.InstanceDirectory))
            return null;

        foreach (string file in Directory.EnumerateFiles(instance.InstanceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(file);
            if (needles.Any(needle => name.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                return SimplifyVersionName(name);
        }

        return null;
    }

    /// <summary>
    /// Like <see cref="DetectLoader"/> but skips names containing <paramref name="exclude"/>
    /// (used so "forge" does not match "neoforge-…").
    /// </summary>
    private static string? DetectLoaderExcluding(
        LaunchInstanceInfo instance,
        string include,
        string exclude)
    {
        if (!Directory.Exists(instance.InstanceDirectory))
            return null;

        foreach (string file in Directory.EnumerateFiles(instance.InstanceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(file);
            if (name.Contains(exclude, StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.Contains(include, StringComparison.OrdinalIgnoreCase))
                return SimplifyVersionName(name);
        }

        return null;
    }

    private static string? DetectModFile(LaunchInstanceInfo instance, params string[] needles)
    {
        string[] modDirectories =
        [
            Path.Combine(instance.InstanceDirectory, "mods"),
            Path.Combine(GetMinecraftRootFromInstance(instance), "mods")
        ];
        foreach (string mods in modDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(mods))
                continue;
            foreach (string file in Directory.EnumerateFiles(mods, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                string artifactName = Path.GetFileNameWithoutExtension(name);
                if (needles.Any(needle => IsModArtifactName(artifactName, needle)))
                    return SimplifyVersionName(name);
            }
        }

        return null;
    }

    private static bool IsModArtifactName(string artifactName, string expectedName) =>
        artifactName.Equals(expectedName, StringComparison.OrdinalIgnoreCase) ||
        artifactName.StartsWith(expectedName + "-", StringComparison.OrdinalIgnoreCase) ||
        artifactName.StartsWith(expectedName + "_", StringComparison.OrdinalIgnoreCase);

    private static string SimplifyVersionName(string fileName)
    {
        string withoutExtension = Path.GetFileNameWithoutExtension(fileName);
        return string.IsNullOrWhiteSpace(withoutExtension) ? fileName : withoutExtension;
    }

    private static string ResolveMinecraftVersionId(LaunchInstanceInfo instance)
    {
        return MinecraftVersionJsonInspector.Read(instance).MinecraftVersionId;
    }

    private void HideAllHints()
    {
        string[] names =
        [
            "HintFabricAPI",
            "HintLegacyFabricAPI",
            "HintOptiFabric",
            "HintOptiFabricOld",
            "HintLegacyOptiFabric",
            "HintModOptiFine",
            "HintQSL",
            "HintQuiltFabricAPI"
        ];

        foreach (string name in names)
        {
            if (this.FindControl<Control>(name) is { } hint)
                hint.IsVisible = false;
        }
    }

    private void HideLoading()
    {
        if (this.FindControl<Control>("PanLoad") is { } load)
        {
            load.IsVisible = false;
            load.IsHitTestVisible = false;
            load.Opacity = 0d;
        }

        if (this.FindControl<MyLoading>("LoadMinecraft") is { } loading)
            loading.Text = "正在准备安装器";
    }

    private static Bitmap? LoadBlockImage(string imageName)
    {
        return LoadImage(BlockAssetRoot + imageName);
    }

    private static Bitmap? LoadImage(string address)
    {
        try
        {
            using Stream stream = OpenImageStream(address);
            return new Bitmap(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static Stream OpenImageStream(string address)
    {
        if (Uri.TryCreate(address, UriKind.Absolute, out Uri? uri))
        {
            if (uri.IsFile)
                return File.OpenRead(uri.LocalPath);
            if (uri.Scheme.Equals("avares", StringComparison.OrdinalIgnoreCase))
                return AssetLoader.Open(uri);
        }

        return File.OpenRead(address);
    }

    private static Task RunOnUiThreadAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    private string ResourceText(string key, string fallback, params object[] args)
    {
        string text = fallback;
        if (this.TryFindResource(key, ActualThemeVariant, out object? value) && value is string resourceText)
            text = resourceText;

        return args.Length == 0
            ? text
            : string.Format(CultureInfo.CurrentCulture, text, args);
    }

    private const string BlockAssetRoot = InstanceDisplayHelper.BlockAssetRoot;

    private void CardMinecraft_PreviewSwap(object sender, RouteEventArgs e)
    {
        e.Handled = true;
        ApplyMinecraftPageState();
        _ = EnsureMinecraftVersionsAsync();
    }

    private void CardForge_PreviewSwap(object sender, RouteEventArgs e) =>
        RequestLoaderInstall(MinecraftLoaderKind.Forge, e);

    private void CardCleanroom_PreviewSwap(object sender, RouteEventArgs e) =>
        RequestLoaderInstall(MinecraftLoaderKind.Cleanroom, e);

    private void CardNeoForge_PreviewSwap(object sender, RouteEventArgs e) =>
        RequestLoaderInstall(MinecraftLoaderKind.NeoForge, e);

    private void CardFabric_PreviewSwap(object sender, RouteEventArgs e) =>
        RequestLoaderInstall(MinecraftLoaderKind.Fabric, e);

    private void CardLegacyFabric_PreviewSwap(object sender, RouteEventArgs e) =>
        RequestLoaderInstall(MinecraftLoaderKind.LegacyFabric, e);

    private void CardFabricApi_PreviewSwap(object sender, RouteEventArgs e) =>
        RequestAddonInstall(MinecraftInstallAddonKind.FabricApi, e);

    private void CardLegacyFabricApi_PreviewSwap(object sender, RouteEventArgs e) =>
        RequestAddonInstall(MinecraftInstallAddonKind.LegacyFabricApi, e);

    private void CardQuilt_PreviewSwap(object sender, RouteEventArgs e) =>
        RequestLoaderInstall(MinecraftLoaderKind.Quilt, e);

    private void CardQSL_PreviewSwap(object sender, RouteEventArgs e) =>
        RequestAddonInstall(MinecraftInstallAddonKind.Qsl, e);

    private void CardLabyMod_PreviewSwap(object sender, RouteEventArgs e) =>
        RequestLoaderInstall(MinecraftLoaderKind.LabyMod, e);

    private void CardOptiFine_PreviewSwap(object sender, RouteEventArgs e) =>
        RequestLoaderInstall(MinecraftLoaderKind.OptiFine, e);

    private void CardOptiFabric_PreviewSwap(object sender, RouteEventArgs e) =>
        RequestAddonInstall(MinecraftInstallAddonKind.OptiFabric, e);

    private void CardLiteLoader_PreviewSwap(object sender, RouteEventArgs e) =>
        RequestLoaderInstall(MinecraftLoaderKind.LiteLoader, e);

    private void RequestLoaderInstall(MinecraftLoaderKind kind, RouteEventArgs e)
    {
        if (_instance is null)
        {
            e.Handled = true;
            return;
        }

        // Match PageDownloadInstall PreviewSwap gating (LoaderSupportState.CanOpen).
        if (!CanOpenLoaderCard(kind))
        {
            e.Handled = true;
            return;
        }

        string cardName = GetLoaderCardName(kind);
        if (this.FindControl<MyCard>("Card" + cardName) is not { IsSwapped: true })
            return;

        SetLoaderVersionPanelLoading(cardName);
        UnhandledExceptionGuard.Observe(
            EnsureLoaderVersions(kind, _instance, _selectedMinecraftVersionId),
            $"PageInstanceInstallRight.Load{kind}");
    }

    private bool CanOpenLoaderCard(MinecraftLoaderKind kind)
    {
        if (_selectedLoaderKind is null || _selectedLoaderKind == kind)
            return true;

        if (_selectedLoaderKind == MinecraftLoaderKind.OptiFine)
            return CanCombineWithOptiFine(kind);

        if (kind == MinecraftLoaderKind.OptiFine)
            return CanCombineWithOptiFine(_selectedLoaderKind.Value);

        return false;
    }

    /// <summary>
    /// Same combine rules as <c>PageDownloadInstall.CanCombineWithOptiFine</c>.
    /// </summary>
    private bool CanCombineWithOptiFine(MinecraftLoaderKind loaderKind)
    {
        if (loaderKind == MinecraftLoaderKind.LiteLoader)
            return true;
        if (loaderKind != MinecraftLoaderKind.Forge)
            return false;

        string? gameVersion = _selectedMinecraftVersionId.Split('-', 2)[0];
        return !Version.TryParse(gameVersion, out Version? parsed) ||
               parsed < new Version(1, 13) ||
               parsed > new Version(1, 14, 3);
    }

    private async Task EnsureLoaderVersions(
        MinecraftLoaderKind kind,
        LaunchInstanceInfo expectedInstance,
        string expectedGameVersion)
    {
        string cardName = GetLoaderCardName(kind);
        (MinecraftLoaderKind Kind, string GameVersion) key = (kind, expectedGameVersion);
        if (_loaderVersionCache.TryGetValue(key, out IReadOnlyList<MinecraftLoaderVersionEntry>? cached))
        {
            PopulateLoaderVersionList(cardName, kind, cached);
            return;
        }

        try
        {
            if (!_loaderVersionLoads.TryGetValue(key, out Task<IReadOnlyList<MinecraftLoaderVersionEntry>>? load))
            {
                load = _loaderMetadataService.GetLoaderVersionsAsync(kind, expectedGameVersion);
                _loaderVersionLoads[key] = load;
            }

            IReadOnlyList<MinecraftLoaderVersionEntry> versions = await load.ConfigureAwait(false);
            await RunOnUiThreadAsync(() =>
            {
                if (!ReferenceEquals(_instance, expectedInstance) ||
                    !string.Equals(_selectedMinecraftVersionId, expectedGameVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _loaderVersionCache[key] = versions;
                PopulateLoaderVersionList(cardName, kind, versions);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or FormatException or InvalidOperationException or NotSupportedException)
        {
            await RunOnUiThreadAsync(() =>
            {
                _loaderVersionLoads.Remove(key);
                if (ReferenceEquals(_instance, expectedInstance))
                    SetLoaderVersionPanelMessage(cardName, "获取版本列表失败", ex.Message);
            }).ConfigureAwait(false);
        }
    }

    private void PopulateLoaderVersionList(
        string cardName,
        MinecraftLoaderKind kind,
        IReadOnlyList<MinecraftLoaderVersionEntry> versions)
    {
        if (this.FindControl<StackPanel>("Pan" + cardName) is not { } panel)
            return;

        panel.Children.Clear();
        if (versions.Count == 0)
        {
            SetLoaderVersionPanelMessage(cardName, "没有可用版本", "当前 Minecraft 版本暂时没有兼容版本。");
            return;
        }

        foreach (MinecraftLoaderVersionEntry version in versions)
        {
            MyListItem item = new()
            {
                Title = version.DisplayVersion,
                Info = version.Stable ? "稳定版" : "测试版",
                Type = MyListItem.CheckType.Clickable,
                Logo = GetLoaderLogo(kind),
                LogoScale = 0.82d,
                Height = 42d,
                Margin = new Thickness(0d, 0d, 0d, 2d),
                Tag = version
            };
            item.Click += (_, _) => SelectLoaderVersion(kind, version);
            panel.Children.Add(item);
        }

        ControlVisualHelpers.AnimateListEntrance(panel, "Instance Loader List " + cardName);
    }

    private void SelectLoaderVersion(MinecraftLoaderKind kind, MinecraftLoaderVersionEntry version)
    {
        // Mirror PageDownloadInstall.SelectLoaderVersion (OptiFine-as-addon + promote).
        bool installsAsAddon = kind == MinecraftLoaderKind.OptiFine &&
                               _selectedLoaderKind is not null and not MinecraftLoaderKind.OptiFine;
        if (installsAsAddon)
        {
            _selectedOptiFineAddon = version;
        }
        else
        {
            if (_selectedLoaderKind == MinecraftLoaderKind.OptiFine && kind != MinecraftLoaderKind.OptiFine &&
                _selectedLoaderVersion is { } selectedOptiFine)
            {
                _selectedOptiFineAddon = selectedOptiFine;
            }

            _selectedLoaderKind = kind;
            _selectedLoaderVersion = version;
            if (kind is MinecraftLoaderKind.Fabric or MinecraftLoaderKind.LegacyFabric)
                _selectedOptiFineAddon = null;
        }

        if (this.FindControl<MyCard>("Card" + GetLoaderCardName(kind)) is { } card)
            card.IsSwapped = true;
        HideAllHints();
        RefreshSelectedLoaderCards();
    }

    private void RefreshSelectedLoaderCards()
    {
        string canAdd = "可添加";
        string? incompatibleLoader = _selectedLoaderKind is null
            ? null
            : ResourceText(
                "Download.Install.Compat.IncompatibleWithLoader",
                "与 {0} 不兼容",
                GetLoaderDisplayName(_selectedLoaderKind.Value));

        foreach (MinecraftLoaderKind kind in CoreLoaderKinds)
        {
            if (kind == MinecraftLoaderKind.OptiFine)
                continue;

            string cardName = GetLoaderCardName(kind);
            if (_selectedLoaderKind == kind && _selectedLoaderVersion is { } selectedLoader)
            {
                SetLoaderInfo(cardName, selectedLoader.Version, GetLoaderImageName(kind));
            }
            else if (_selectedLoaderKind is not null && _selectedLoaderKind != MinecraftLoaderKind.OptiFine)
            {
                SetLoaderInfoClosed(cardName, incompatibleLoader ?? canAdd, GetLoaderImageName(kind));
            }
            else if (_selectedLoaderKind == MinecraftLoaderKind.OptiFine && !CanCombineWithOptiFine(kind))
            {
                SetLoaderInfoClosed(
                    cardName,
                    ResourceText(
                        "Download.Install.Compat.IncompatibleWithLoader",
                        "与 {0} 不兼容",
                        GetLoaderDisplayName(MinecraftLoaderKind.OptiFine)),
                    GetLoaderImageName(kind));
            }
            else
            {
                SetLoaderInfo(cardName, null, GetLoaderImageName(kind));
            }
        }

        if (_selectedOptiFineAddon is { } optiFineAddon)
            SetLoaderInfo("OptiFine", optiFineAddon.Version, GetLoaderImageName(MinecraftLoaderKind.OptiFine));
        else if (_selectedLoaderKind == MinecraftLoaderKind.OptiFine && _selectedLoaderVersion is { } selectedOptiFine)
            SetLoaderInfo("OptiFine", selectedOptiFine.Version, GetLoaderImageName(MinecraftLoaderKind.OptiFine));
        else if (_selectedLoaderKind is { } selectedLoader && !CanCombineWithOptiFine(selectedLoader))
            SetLoaderInfoClosed(
                "OptiFine",
                ResourceText(
                    "Download.Install.Compat.IncompatibleWithLoader",
                    "与 {0} 不兼容",
                    GetLoaderDisplayName(selectedLoader)),
                GetLoaderImageName(MinecraftLoaderKind.OptiFine));
        else
            SetLoaderInfo("OptiFine", null, GetLoaderImageName(MinecraftLoaderKind.OptiFine));

        SetLoaderCardVisible(
            "FabricApi",
            _selectedLoaderKind is MinecraftLoaderKind.Fabric or MinecraftLoaderKind.Quilt);
        SetLoaderCardVisible("LegacyFabricApi", _selectedLoaderKind == MinecraftLoaderKind.LegacyFabric);
        SetLoaderCardVisible("QSL", _selectedLoaderKind == MinecraftLoaderKind.Quilt);
        SetLoaderCardVisible("OptiFabric", false);

        if (_selectedLoaderKind == MinecraftLoaderKind.Fabric &&
            this.FindControl<Control>("HintFabricAPI") is { } fabricHint)
        {
            fabricHint.IsVisible = true;
        }

        if (_selectedLoaderKind == MinecraftLoaderKind.Quilt &&
            this.FindControl<Control>("HintQSL") is { } qslHint)
        {
            qslHint.IsVisible = true;
        }

        if (this.FindControl<MyListItem>("ItemSelect") is { } item)
        {
            List<string> parts = [_selectedMinecraftVersionId];
            if (_selectedLoaderKind is { } kind && _selectedLoaderVersion is { } version)
                parts.Add(GetLoaderDisplayName(kind) + " " + version.DisplayVersion);
            if (_selectedOptiFineAddon is { } addon && _selectedLoaderKind != MinecraftLoaderKind.OptiFine)
                parts.Add(GetLoaderDisplayName(MinecraftLoaderKind.OptiFine) + " " + addon.DisplayVersion);
            if (parts.Count == 1)
                parts.Add(ResourceText("Instance.Install.NoExtraInstall", "无额外安装"));
            item.Info = string.Join("  |  ", parts);
        }

        if (this.FindControl<MyExtraTextButton>("BtnSelectStart") is { } startButton)
            startButton.IsEnabled = true;
    }

    private void SetLoaderInfoClosed(string name, string text, string imageName)
    {
        if (this.FindControl<TextBlock>("Lab" + name) is { } label)
        {
            label.Text = text;
            label.Foreground = LegacyResourceResolver.Brush(label, "ColorBrushGray4", "#8c8c8c");
        }

        if (this.FindControl<Image>("Img" + name) is { } image)
        {
            image.Source = LoadBlockImage(imageName);
            image.IsVisible = false;
        }

        if (this.FindControl<Control>("Btn" + name + "Clear") is { } clearButton)
            clearButton.IsVisible = false;

        if (this.FindControl<MyCard>("Card" + name) is { } card)
            card.IsSwapped = true;
    }

    private void ResetSelectedLoader()
    {
        _selectedLoaderKind = null;
        _selectedLoaderVersion = null;
        _selectedOptiFineAddon = null;
    }

    private void SetLoaderVersionPanelLoading(string cardName)
    {
        if (this.FindControl<StackPanel>("Pan" + cardName) is not { } panel)
            return;

        panel.Children.Clear();
        panel.Children.Add(new MyLoading
        {
            Text = "正在获取版本列表",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Thickness(0d, 8d, 0d, 2d)
        });
    }

    private void SetLoaderVersionPanelMessage(string cardName, string title, string info)
    {
        if (this.FindControl<StackPanel>("Pan" + cardName) is not { } panel)
            return;

        panel.Children.Clear();
        panel.Children.Add(new MyListItem
        {
            Title = title,
            Info = info,
            Type = MyListItem.CheckType.None,
            Logo = GetLoaderLogoByCardName(cardName),
            LogoScale = 0.82d,
            Height = 42d
        });
    }

    private void RequestAddonInstall(MinecraftInstallAddonKind kind, RouteEventArgs e)
    {
        e.Handled = true;
        if (_instance is null)
            return;

        // Prefer in-page selection (install-page source of truth); fall back to disk detect.
        MinecraftLoaderKind? loaderKind = _selectedLoaderKind;
        string? loaderVersion = _selectedLoaderVersion?.Version;
        string? optiFineVersion = _selectedOptiFineAddon?.Version;
        if (loaderKind is null || string.IsNullOrWhiteSpace(loaderVersion))
        {
            (loaderKind, loaderVersion, string? detectedOptiFine) = DetectCurrentLoaderSelection(_instance);
            optiFineVersion ??= detectedOptiFine;
        }

        if (loaderKind is null || string.IsNullOrWhiteSpace(loaderVersion))
            return;

        ModifyRequested?.Invoke(this, new InstanceInstallModifyRequest(
            _instance,
            _selectedMinecraftVersionId,
            AddonKind: kind,
            CurrentLoaderKind: loaderKind,
            CurrentLoaderVersion: loaderVersion,
            CurrentOptiFineVersion: optiFineVersion));
    }

    internal static (MinecraftLoaderKind? Kind, string? Version, string? OptiFineVersion) DetectCurrentLoaderSelection(
        LaunchInstanceInfo instance)
    {
        IReadOnlyList<string> libraries = MinecraftVersionJsonInspector.Read(instance).LoaderEntries;
        string? optiFine = MinecraftLoaderLibraryDetector.DetectVersion(libraries, "optifine") ?? DetectLoader(instance, "optifine");
        bool hasLegacyFabricLibraries = libraries.Any(library => library.Contains("net.legacyfabric:", StringComparison.OrdinalIgnoreCase));
        string? fabricLoaderVersion = MinecraftLoaderLibraryDetector.DetectVersion(libraries, "net.fabricmc:fabric-loader:");
        (MinecraftLoaderKind Kind, string? Version)[] candidates =
        [
            (MinecraftLoaderKind.LegacyFabric, hasLegacyFabricLibraries ? fabricLoaderVersion : null),
            (MinecraftLoaderKind.Fabric, hasLegacyFabricLibraries ? null : fabricLoaderVersion),
            (MinecraftLoaderKind.Quilt, MinecraftLoaderLibraryDetector.DetectVersion(libraries, "org.quiltmc:quilt-loader:")),
            (MinecraftLoaderKind.NeoForge, MinecraftLoaderLibraryDetector.DetectVersion(libraries, "net.neoforged:neoforge:", "net.neoforge:forge:")),
            (MinecraftLoaderKind.Forge, MinecraftLoaderLibraryDetector.DetectVersion(libraries, "net.minecraftforge:forge:")),
            (MinecraftLoaderKind.Cleanroom, MinecraftLoaderLibraryDetector.DetectVersion(libraries, "com.cleanroommc:cleanroom:")),
            (MinecraftLoaderKind.LabyMod, MinecraftLoaderLibraryDetector.DetectVersion(libraries, "labymod")),
            (MinecraftLoaderKind.LiteLoader, MinecraftLoaderLibraryDetector.DetectVersion(libraries, "liteloader"))
        ];
        foreach ((MinecraftLoaderKind kind, string? version) in candidates)
        {
            if (!string.IsNullOrWhiteSpace(version))
                return (kind, version, NormalizeOptiFineVersion(optiFine));
        }

        return (null, null, NormalizeOptiFineVersion(optiFine));
    }

    private static string? NormalizeOptiFineVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;
        return version.StartsWith("OptiFine_", StringComparison.OrdinalIgnoreCase) ? version[9..] : version;
    }

    private void Forge_Clear(object sender, PointerReleasedEventArgs e) => ClearLoader("Forge", e);

    private void Cleanroom_Clear(object sender, PointerReleasedEventArgs e) => ClearLoader("Cleanroom", e);

    private void NeoForge_Clear(object sender, PointerReleasedEventArgs e) => ClearLoader("NeoForge", e);

    private void Fabric_Clear(object sender, PointerReleasedEventArgs e) => ClearLoader("Fabric", e);

    private void LegacyFabric_Clear(object sender, PointerReleasedEventArgs e) => ClearLoader("LegacyFabric", e);

    private void FabricApi_Clear(object sender, PointerReleasedEventArgs e) => ClearLoader("FabricApi", e);

    private void LegacyFabricApi_Clear(object sender, PointerReleasedEventArgs e) => ClearLoader("LegacyFabricApi", e);

    private void Quilt_Clear(object sender, PointerReleasedEventArgs e) => ClearLoader("Quilt", e);

    private void QSL_Clear(object sender, PointerReleasedEventArgs e) => ClearLoader("QSL", e);

    private void LabyMod_Clear(object sender, PointerReleasedEventArgs e) => ClearLoader("LabyMod", e);

    private void OptiFine_Clear(object sender, PointerReleasedEventArgs e) => ClearLoader("OptiFine", e);

    private void OptiFabric_Clear(object sender, PointerReleasedEventArgs e) => ClearLoader("OptiFabric", e);

    private void LiteLoader_Clear(object sender, PointerReleasedEventArgs e) => ClearLoader("LiteLoader", e);

    private void ClearLoader(string name, PointerReleasedEventArgs e)
    {
        // Match PageDownloadInstall.ClearSelectedLoader: OptiFine addon clears independently.
        if (string.Equals(name, "OptiFine", StringComparison.Ordinal) &&
            _selectedOptiFineAddon is not null &&
            _selectedLoaderKind != MinecraftLoaderKind.OptiFine)
        {
            _selectedOptiFineAddon = null;
            RefreshSelectedLoaderCards();
            e.Handled = true;
            return;
        }

        if (TryGetLoaderKind(name, out MinecraftLoaderKind kind) && _selectedLoaderKind == kind)
        {
            ResetSelectedLoader();
            RefreshSelectedLoaderCards();
        }
        else
        {
            string imageName = TryGetLoaderKind(name, out MinecraftLoaderKind clearedKind)
                ? GetLoaderImageName(clearedKind)
                : "Grass.png";
            SetLoaderInfo(name, null, imageName);
        }

        e.Handled = true;
    }

    private static readonly MinecraftLoaderKind[] CoreLoaderKinds =
    [
        MinecraftLoaderKind.Forge,
        MinecraftLoaderKind.Cleanroom,
        MinecraftLoaderKind.NeoForge,
        MinecraftLoaderKind.Fabric,
        MinecraftLoaderKind.LegacyFabric,
        MinecraftLoaderKind.Quilt,
        MinecraftLoaderKind.LabyMod,
        MinecraftLoaderKind.OptiFine,
        MinecraftLoaderKind.LiteLoader
    ];

    private static string GetLoaderCardName(MinecraftLoaderKind kind) =>
        kind == MinecraftLoaderKind.LegacyFabric ? "LegacyFabric" : kind.ToString();

    private static string GetLoaderDisplayName(MinecraftLoaderKind kind) =>
        kind == MinecraftLoaderKind.LegacyFabric ? "Legacy Fabric" : kind.ToString();

    private static string GetLoaderImageName(MinecraftLoaderKind kind) =>
        kind switch
        {
            MinecraftLoaderKind.Forge => "Anvil.png",
            MinecraftLoaderKind.Cleanroom => "Cleanroom.png",
            MinecraftLoaderKind.NeoForge => "NeoForge.png",
            MinecraftLoaderKind.Fabric or MinecraftLoaderKind.LegacyFabric => "Fabric.png",
            MinecraftLoaderKind.Quilt => "Quilt.png",
            MinecraftLoaderKind.LabyMod => "LabyMod.png",
            MinecraftLoaderKind.OptiFine => "GrassPath.png",
            MinecraftLoaderKind.LiteLoader => "Egg.png",
            _ => "Grass.png"
        };

    private static string GetLoaderLogo(MinecraftLoaderKind kind) =>
        BlockAssetRoot + GetLoaderImageName(kind);

    private static string GetLoaderLogoByCardName(string cardName) =>
        TryGetLoaderKind(cardName, out MinecraftLoaderKind kind)
            ? GetLoaderLogo(kind)
            : string.Empty;

    private static bool TryGetLoaderKind(string cardName, out MinecraftLoaderKind kind)
    {
        if (string.Equals(cardName, "LegacyFabric", StringComparison.Ordinal))
        {
            kind = MinecraftLoaderKind.LegacyFabric;
            return true;
        }

        return Enum.TryParse(cardName, ignoreCase: false, out kind);
    }

    private static string GetMinecraftRootFromInstance(LaunchInstanceInfo instance)
    {
        DirectoryInfo versionDirectory = new(instance.InstanceDirectory);
        DirectoryInfo? versionsDirectory = versionDirectory.Parent;
        if (versionsDirectory?.Parent is not null &&
            string.Equals(versionsDirectory.Name, "versions", StringComparison.OrdinalIgnoreCase))
        {
            return versionsDirectory.Parent.FullName;
        }

        return instance.InstanceDirectory;
    }

    private sealed record MinecraftVersionView(
        MinecraftVersionManifestEntry Manifest,
        string Title,
        string Info,
        DateTimeOffset? ReleaseTime,
        MinecraftVersionCategory Category,
        string Logo);
}
