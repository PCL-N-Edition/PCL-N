// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using PCL.Application.Accounts;
using PCL.Application.Settings;
using PCL.Core.App;
using PCL.Core.Logging;
using PCL.Desktop.Features.Community;
using PCL.Desktop.Features.Launching.Views;
using PCL.Desktop.Features.Settings.Views;
using PCL.Desktop.Localization;
using PCL.Online;
using PCL.Platform.Paths;

namespace PCL.Desktop.Features.Online;

internal sealed class DesktopOnlineRuntimeHost :
    IOnlineRuntimeHost,
    ICloudSyncDataProvider,
    IRegionalDownloadPolicySink
{
    private static readonly string[] UiBooleanKeys =
    [
        "UiLauncherLogo", "UiLockWindowSize", "UiShowLaunchingHint", "UiHintAlignRight",
        "UiLogoLeft", "UiBackgroundColorful", "UiAutoPauseVideo", "UiBlur"
    ];

    private static readonly string[] UiIntegerKeys =
    [
        "UiAniFPS", "UiDarkMode", "UiLauncherTransparent", "UiBackgroundOpacity",
        "UiBackgroundBlur", "UiBackgroundSuit", "UiBlurValue", "UiBlurSamplingRate",
        "UiBlurType", "UiLogoType"
    ];

    private static readonly string[] UiTextKeys = ["UiLogoText", "UiLanguage", "UiFormatCulture"];
    private static readonly string[] HintBooleanKeys = ["UiHideNEditionHint"];
    private static readonly string[] HintTextKeys =
    [
        "UiCommunityNoticeVersion", "UiSpecialVersionNoticeVersion", "UiLauncherAnnouncementIds"
    ];

    private static readonly string[] DownloadBooleanKeys =
    [
        "ToolDownloadAutoSelectVersion", "ToolFixAuthlib", "ToolDownloadIgnoreQuilt",
        "ToolDownloadAutoInstallDependencies", "ToolDownloadClipboard"
    ];

    private static readonly string[] DownloadIntegerKeys =
    [
        "ToolDownloadThread", "ToolDownloadSpeed", "ToolDownloadSource", "ToolDownloadVersion",
        "ToolDownloadTranslateV2", "ToolDownloadMod", "ToolModLocalNameStyle"
    ];

    private static readonly string[] LaunchBooleanKeys =
    [
        "LaunchAdvanceDisableJLW", "LaunchAdvanceDisableRW", "LaunchAdvanceGraphicCard",
        "LaunchAdvanceNoJavaw", "LaunchAdvanceDisableLwjglUnsafeAgent", "LaunchAutoRepairGame"
    ];

    private static readonly string[] LaunchIntegerKeys =
    [
        "LaunchRamType", "LaunchRamCustom", "LaunchPreferredIpStack", "LaunchAdvanceRenderer",
        "LaunchArgumentIndieV2", "LaunchArgumentVisible", "LaunchArgumentPriority",
        "LaunchArgumentWindowWidth", "LaunchArgumentWindowHeight", "LaunchArgumentWindowType",
        "LoginMsAuthType"
    ];

    private static readonly string[] LaunchTextKeys = ["LaunchArgumentTitle", "LaunchArgumentInfo"];
    private static readonly string[] HomepageIntegerKeys = ["UiCustomType", "UiCustomPreset"];
    private static readonly string[] HomepageTextKeys = ["UiCustomNet"];
    private static readonly string[] MusicBooleanKeys =
        ["UiMusicStop", "UiMusicStart", "UiMusicAuto", "UiMusicRandom", "UiMusicSMTC"];
    private static readonly string[] MusicIntegerKeys = ["UiMusicVolume"];
    private static readonly string[] UpdateBooleanKeys = ["ToolHelpChinese", "ToolUpdateRelease", "ToolUpdateSnapshot"];
    private static readonly string[] UpdateIntegerKeys = ["SystemUpdateMode", "SystemUpdateChannel"];

    private static readonly LegacySettingMap[] UiLegacyBooleans =
    [
        new("ui_launcher_logo", "UiLauncherLogo"), new("ui_show_launching_hint", "UiShowLaunchingHint"),
        new("ui_hint_align_right", "UiHintAlignRight"), new("ui_logo_left", "UiLogoLeft"),
        new("detailed_instance_classification", "UiDetailedInstanceClassification"),
        new("ui_background_colorful", "UiBackgroundColorful"), new("ui_auto_pause_video", "UiAutoPauseVideo"),
        new("ui_blur", "UiBlur")
    ];
    private static readonly LegacySettingMap[] UiLegacyIntegers =
    [
        new("ui_launcher_theme", "UiLauncherTheme"), new("ui_launcher_hue", "UiLauncherHue"),
        new("ui_launcher_sat", "UiLauncherSat"), new("ui_launcher_light", "UiLauncherLight"),
        new("ui_launcher_delta", "UiLauncherDelta"), new("ui_logo_type", "UiLogoType"),
        new("ui_background_opacity", "UiBackgroundOpacity"), new("ui_background_carousel", "UiBackgroundCarousel"),
        new("ui_background_blur", "UiBackgroundBlur"), new("ui_background_suit", "UiBackgroundSuit"),
        new("ui_blur_value", "UiBlurValue"), new("ui_blur_sampling_rate", "UiBlurSamplingRate"),
        new("ui_blur_type", "UiBlurType")
    ];
    private static readonly LegacySettingMap[] UiLegacyTexts =
    [
        new("ui_language", "UiLanguage"), new("ui_format_culture", "UiFormatCulture"),
        new("ui_region", "UiRegion"), new("ui_logo_text", "UiLogoText"),
        new("ui_font", "UiFont"), new("ui_motd_font", "UiMotdFont")
    ];
    private static readonly LegacySettingMap[] HintLegacyBooleans =
    [
        new("hint_download_thread", "HintDownloadThread"), new("hint_renderer", "HintRenderer"),
        new("hint_debug_log4j2_config", "HintDebugLog4j2Config"), new("hint_install_back", "HintInstallBack"),
        new("hint_hide", "HintHide"), new("hint_hand_install", "HintHandInstall"),
        new("hint_update_mod", "HintUpdateMod"), new("hint_custom_command", "HintCustomCommand"),
        new("hint_custom_warn", "HintCustomWarn"), new("hint_more_advanced_setup", "HintMoreAdvancedSetup"),
        new("hint_indie_setup", "HintIndieSetup"), new("hint_profile_select", "HintProfileSelect"),
        new("hint_export_config", "HintExportConfig"), new("hint_max_log", "HintMaxLog"),
        new("hint_non_ascii_game_path", "HintNonAsciiGamePath"), new("ui_launcher_ce_hint", "UiLauncherCEHint"),
        new("ui_schematic_first_time", "UiSchematicFirstTime"), new("hint_datapack_update", "HintDatapackUpdate")
    ];
    private static readonly LegacySettingMap[] HintLegacyIntegers = [new("hint_clear_rubbish", "HintClearRubbish")];
    private static readonly LegacySettingMap[] HintLegacyTexts = [new("showed_announcements", "SystemAnnouncementSeen")];
    private static readonly LegacySettingMap[] DownloadLegacyBooleans =
    [
        new("download_auto_select_instance", "ToolDownloadAutoSelectVersion"), new("download_fix_authlib", "ToolFixAuthlib"),
        new("comp_ignore_quilt", "ToolDownloadIgnoreQuilt"),
        new("comp_auto_install_dependencies", "ToolDownloadAutoInstallDependencies"),
        new("comp_read_clipboard", "ToolDownloadClipboard")
    ];
    private static readonly LegacySettingMap[] DownloadLegacyIntegers =
    [
        new("download_thread_limit", "ToolDownloadThread"), new("download_speed_limit", "ToolDownloadSpeed"),
        new("download_file_source", "ToolDownloadSource"), new("download_version_source", "ToolDownloadVersion"),
        new("comp_name_format_v1", "ToolDownloadTranslate"), new("comp_name_format_v2", "ToolDownloadTranslateV2"),
        new("comp_source_solution", "ToolDownloadMod"), new("comp_local_name_style", "ToolModLocalNameStyle")
    ];
    private static readonly LegacySettingMap[] LaunchLegacyBooleans =
    [
        new("launch_disable_jlw", "LaunchAdvanceDisableJLW"), new("launch_disable_rw", "LaunchAdvanceDisableRW"),
        new("launch_set_gpu_preference", "LaunchAdvanceGraphicCard"), new("launch_no_javaw", "LaunchAdvanceNoJavaw"),
        new("launch_disable_lwjgl_unsafe_agent", "LaunchAdvanceDisableLwjglUnsafeAgent")
    ];
    private static readonly LegacySettingMap[] LaunchLegacyIntegers =
    [
        new("launch_preferred_ip_stack", "LaunchPreferredIpStack"), new("launch_indie_solution_v1", "LaunchArgumentIndie"),
        new("launch_indie_solution_v2", "LaunchArgumentIndieV2"), new("launch_launcher_visibility", "LaunchArgumentVisible"),
        new("launch_process_priority", "LaunchArgumentPriority"), new("launch_login_ms_auth_type", "LoginMsAuthType")
    ];
    private static readonly LegacySettingMap[] LaunchLegacyTexts =
        [new("launch_title", "LaunchArgumentTitle"), new("launch_type_info", "LaunchArgumentInfo")];
    private static readonly LegacySettingMap[] HomepageLegacyIntegers =
        [new("ui_custom_type", "UiCustomType"), new("ui_custom_preset", "UiCustomPreset")];
    private static readonly LegacySettingMap[] HomepageLegacyTexts =
        [new("ui_custom_net", "UiCustomNet"), new("cache_saved_page_url", "CacheSavedPageUrl"), new("cache_saved_page_version", "CacheSavedPageVersion")];
    private static readonly LegacySettingMap[] MusicLegacyBooleans =
        [new("ui_music_stop", "UiMusicStop"), new("ui_music_start", "UiMusicStart"), new("ui_music_auto", "UiMusicAuto"), new("ui_music_random", "UiMusicRandom"), new("ui_music_smtc", "UiMusicSMTC")];
    private static readonly LegacySettingMap[] MusicLegacyIntegers = [new("ui_music_volume", "UiMusicVolume")];
    private static readonly LegacySettingMap[] UpdateLegacyBooleans =
        [new("tool_help_chinese", "ToolHelpChinese"), new("tool_update_release", "ToolUpdateRelease"), new("tool_update_snapshot", "ToolUpdateSnapshot")];
    private static readonly LegacySettingMap[] UpdateLegacyIntegers =
        [new("system_system_update", "SystemUpdateMode"), new("system_update_channel", "SystemUpdateChannel")];

    private readonly DesktopOnlineStateStore _state;
    private readonly CommunityFavoritesStore _favorites;

    public DesktopOnlineRuntimeHost(CommunityFavoritesStore favorites)
    {
        _favorites = favorites ?? throw new ArgumentNullException(nameof(favorites));
        DefaultPlatformPathProvider paths = new();
        SharedDataDirectory = LauncherSettingsPageBinder.CreateDataDirectory();
        _state = new DesktopOnlineStateStore(SharedDataDirectory, paths.ApplicationDataDirectory);
    }

    public string SharedDataDirectory { get; }

    public ICloudSyncDataProvider CloudSync => this;

    public IRegionalDownloadPolicySink RegionalDownloadPolicy => this;

    public bool IsEnabled => GetBoolean("Online.CloudSyncEnabled");

    public bool HasAnySectionEnabled =>
        GetBoolean("Online.CloudSyncAccount") ||
        GetBoolean("Online.CloudSyncFavorites") ||
        GetBoolean("Online.CloudSyncUiPreferences") ||
        GetBoolean("Online.CloudSyncHintPreferences") ||
        GetBoolean("Online.CloudSyncDownloadPreferences") ||
        GetBoolean("Online.CloudSyncLaunchPreferences") ||
        GetBoolean("Online.CloudSyncHomepagePreferences") ||
        GetBoolean("Online.CloudSyncMusicPreferences") ||
        GetBoolean("Online.CloudSyncUpdatePreferences") ||
        GetBoolean("Online.CloudSyncCustomVariables");

    public string? GetSecret(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (string.Equals(key, "MS_CLIENT_ID", StringComparison.Ordinal))
            return MicrosoftMinecraftAuthService.ResolveClientId();

        return Environment.GetEnvironmentVariable("PCL_" + key)
               ?? Environment.GetEnvironmentVariable(key);
    }

    public string Text(string key, params object?[] args)
    {
        string text = AvaloniaLocalizationManager.GetText(key, key);
        return args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
    }

    public string GetString(string key) => _state.GetString(key);

    public void SetString(string key, string value) => _state.SetString(key, value);

    public bool GetBoolean(string key)
    {
        bool fallback = key.StartsWith("Online.CloudSync", StringComparison.Ordinal) &&
                        !string.Equals(key, "Online.CloudSyncDisconnected", StringComparison.Ordinal);
        return _state.GetBoolean(key, fallback);
    }

    public void SetBoolean(string key, bool value) => _state.SetBoolean(key, value);

    public void Flush() => _state.Flush();

    public Dictionary<string, JsonObject> BuildSnapshot()
    {
        LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
        Dictionary<string, JsonObject> snapshot = new(StringComparer.Ordinal);
        AddSection(snapshot, "account", "Online.CloudSyncAccount", BuildAccountSection);
        AddSection(snapshot, "favorites", "Online.CloudSyncFavorites", BuildFavoritesSection);
        AddSection(snapshot, "uiPreferences", "Online.CloudSyncUiPreferences", () => BuildUiSection(settings));
        AddSection(snapshot, "hintPreferences", "Online.CloudSyncHintPreferences", () => BuildHintSection(settings));
        AddSection(snapshot, "downloadPreferences", "Online.CloudSyncDownloadPreferences", () =>
            BuildDownloadSection(settings));
        AddSection(snapshot, "launchPreferences", "Online.CloudSyncLaunchPreferences", () => BuildLaunchSection(settings));
        AddSection(snapshot, "homepagePreferences", "Online.CloudSyncHomepagePreferences", () => BuildHomepageSection(settings));
        AddSection(snapshot, "musicPreferences", "Online.CloudSyncMusicPreferences", () => BuildMusicSection(settings));
        AddSection(snapshot, "updatePreferences", "Online.CloudSyncUpdatePreferences", () => BuildUpdateSection(settings));
        AddSection(snapshot, "customVariables", "Online.CloudSyncCustomVariables", () =>
            BuildCustomVariablesSection(settings));
        return snapshot;
    }

    public async Task ApplySectionsAsync(
        IReadOnlyDictionary<string, JsonObject?> sections,
        bool overwriteAccount)
    {
        ArgumentNullException.ThrowIfNull(sections);
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() => ApplySections(sections, overwriteAccount));
            return;
        }

        ApplySections(sections, overwriteAccount);
    }

    public bool Apply(ClientRegionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.AllowDomesticMirrorSwitch)
            return false;

        DownloadSourcePreference target = policy.UseDomesticMirror
            ? DownloadSourcePreference.MirrorOnly
            : DownloadSourcePreference.OfficialOnly;
        bool changed = false;
        LauncherSettingsPageBinder.UpdateSettings(settings =>
        {
            if (settings.DownloadSource == target)
                return settings;
            changed = true;
            return settings with { DownloadSource = target };
        });
        return changed;
    }

    public void HydrateMicrosoftProfile(LoginProfileInfo profile, bool explicitLogin)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Kind != LaunchLoginProfileKind.Microsoft || string.IsNullOrWhiteSpace(profile.Uuid))
            return;
        if (!explicitLogin && GetBoolean("Online.CloudSyncDisconnected"))
            return;

        if (explicitLogin)
            SetBoolean("Online.CloudSyncDisconnected", false);
        string existingUuid = GetString("Online.MsUuid");
        bool accountChanged = explicitLogin &&
                              !string.IsNullOrWhiteSpace(existingUuid) &&
                              !string.Equals(existingUuid, profile.Uuid, StringComparison.OrdinalIgnoreCase);
        if (accountChanged || string.IsNullOrWhiteSpace(GetString("Online.MsId")))
            SetString("Online.MsId", profile.Uuid.Replace("-", string.Empty, StringComparison.Ordinal));
        SetString("Online.MsUserName", profile.Username);
        SetString("Online.MsMinecraftProfileName", profile.Username);
        SetString("Online.MsUuid", profile.Uuid);
        SetString("Online.MsAvatarUrl", profile.SkinAddress ?? string.Empty);
        SetBoolean("Online.MsOwnsMinecraft", true);
        Flush();
    }

    public void DisconnectAccount()
    {
        OnlineAccountService.Logout();
        SetBoolean("Online.CloudSyncDisconnected", true);
        Flush();
    }

    public bool UsesFavoritesStore(CommunityFavoritesStore favorites) => ReferenceEquals(_favorites, favorites);

    internal void ApplySections(IReadOnlyDictionary<string, JsonObject?> sections, bool overwriteAccount)
    {
        if (GetBoolean("Online.CloudSyncAccount"))
            ApplyAccount(GetSection(sections, "account"), overwriteAccount);

        LauncherSettingsPageBinder.UpdateSettings(settings =>
        {
            if (GetBoolean("Online.CloudSyncUiPreferences"))
                settings = ApplyUiSection(GetSection(sections, "uiPreferences"), settings);
            if (GetBoolean("Online.CloudSyncHintPreferences"))
                ApplyHintSection(GetSection(sections, "hintPreferences"), settings);
            if (GetBoolean("Online.CloudSyncDownloadPreferences"))
                settings = ApplyDownloadSection(GetSection(sections, "downloadPreferences"), settings);
            if (GetBoolean("Online.CloudSyncLaunchPreferences"))
                ApplyLaunchSection(GetSection(sections, "launchPreferences"), settings);
            if (GetBoolean("Online.CloudSyncHomepagePreferences"))
                ApplyHomepageSection(GetSection(sections, "homepagePreferences"), settings);
            if (GetBoolean("Online.CloudSyncMusicPreferences"))
                ApplyMusicSection(GetSection(sections, "musicPreferences"), settings);
            if (GetBoolean("Online.CloudSyncUpdatePreferences"))
                ApplyUpdateSection(GetSection(sections, "updatePreferences"), settings);
            if (GetBoolean("Online.CloudSyncCustomVariables"))
                ApplyCustomVariablesSection(GetSection(sections, "customVariables"), settings);
            return settings;
        });
        if (GetBoolean("Online.CloudSyncFavorites"))
            ApplyFavorites(GetSection(sections, "favorites"));
        Flush();
    }

    private JsonObject BuildAccountSection() => new()
    {
        ["msid"] = GetString("Online.MsId"),
        ["ms_user_name"] = GetString("Online.MsUserName"),
        ["ms_uuid"] = GetString("Online.MsUuid"),
        ["ms_avatar_url"] = GetString("Online.MsAvatarUrl"),
        ["ms_owns_minecraft"] = GetBoolean("Online.MsOwnsMinecraft"),
        ["minecraft_profile_name"] = GetString("Online.MsMinecraftProfileName"),
        ["legal_accepted_version"] = GetString("Online.LegalAcceptedVersion")
    };

    private JsonObject BuildFavoritesSection()
    {
        JsonNode items = JsonNode.Parse(_favorites.ExportJson()) ?? new JsonArray();
        JsonArray projectIds = new(_favorites.Items
            .Select(static item => JsonValue.Create(item.Entry.ProjectId))
            .ToArray<JsonNode?>());
        return new JsonObject
        {
            ["items"] = items,
            ["comp_favorites"] = new JsonArray(
                new JsonObject
                {
                    ["Name"] = "默认收藏夹",
                    ["Id"] = "pcln-migrated",
                    ["Favs"] = projectIds,
                    ["Notes"] = new JsonObject()
                })
        };
    }

    private static JsonObject BuildUiSection(LauncherSettings settings)
    {
        JsonObject section = BuildOptionSection(settings, UiBooleanKeys, UiIntegerKeys, UiTextKeys);
        section["color_mode"] = settings.ColorMode.ToString();
        section["light_color"] = settings.LightColor.ToString();
        section["dark_color"] = settings.DarkColor.ToString();
        section["ui_dark_mode"] = (int)settings.ColorMode;
        section["ui_light_color"] = (int)settings.LightColor;
        section["ui_dark_color"] = (int)settings.DarkColor;
        AddLegacyOptionValues(section, settings, UiLegacyBooleans, UiLegacyIntegers, UiLegacyTexts);
        section["ui_hidden_pages"] = BuildLegacyBooleanObject(settings,
            new LegacySettingMap("page_download", "UiHiddenPageDownload"),
            new LegacySettingMap("page_setup", "UiHiddenPageSetup"),
            new LegacySettingMap("page_tools", "UiHiddenPageTools"));
        section["ui_hidden_tools"] = BuildLegacyBooleanObject(settings,
            new LegacySettingMap("tools_help", "UiHiddenToolsHelp"),
            new LegacySettingMap("tools_test", "UiHiddenToolsTest"));
        section["ui_hidden_instance_tabs"] = BuildLegacyBooleanObject(settings,
            new LegacySettingMap("instance_edit", "UiHiddenInstanceEdit"),
            new LegacySettingMap("instance_export", "UiHiddenInstanceExport"),
            new LegacySettingMap("instance_save", "UiHiddenInstanceSave"),
            new LegacySettingMap("instance_screenshot", "UiHiddenInstanceScreenshot"),
            new LegacySettingMap("instance_mod", "UiHiddenInstanceMod"),
            new LegacySettingMap("instance_resource_pack", "UiHiddenInstanceResourcePack"),
            new LegacySettingMap("instance_shader", "UiHiddenInstanceShader"),
            new LegacySettingMap("instance_schematic", "UiHiddenInstanceSchematic"),
            new LegacySettingMap("instance_server", "UiHiddenInstanceServer"));
        section["ui_hidden_functions"] = BuildLegacyBooleanObject(settings,
            new LegacySettingMap("function_select", "UiHiddenFunctionSelect"),
            new LegacySettingMap("function_mod_update", "UiHiddenFunctionModUpdate"),
            new LegacySettingMap("function_hidden", "UiHiddenFunctionHidden"));
        return section;
    }

    private static JsonObject BuildHintSection(LauncherSettings settings)
    {
        JsonObject section = BuildOptionSection(settings, HintBooleanKeys, [], HintTextKeys);
        AddLegacyOptionValues(section, settings, HintLegacyBooleans, HintLegacyIntegers, HintLegacyTexts);
        return section;
    }

    private static JsonObject BuildDownloadSection(LauncherSettings settings)
    {
        JsonObject section = BuildOptionSection(settings, DownloadBooleanKeys, DownloadIntegerKeys, []);
        section["download_source"] = settings.DownloadSource.ToString();
        AddLegacyOptionValues(section, settings, DownloadLegacyBooleans, DownloadLegacyIntegers, []);
        return section;
    }

    private static JsonObject BuildLaunchSection(LauncherSettings settings)
    {
        JsonObject section = BuildOptionSection(settings, LaunchBooleanKeys, LaunchIntegerKeys, LaunchTextKeys);
        AddLegacyOptionValues(section, settings, LaunchLegacyBooleans, LaunchLegacyIntegers, LaunchLegacyTexts);
        return section;
    }

    private static JsonObject BuildHomepageSection(LauncherSettings settings)
    {
        JsonObject section = BuildOptionSection(settings, [], HomepageIntegerKeys, HomepageTextKeys);
        AddLegacyOptionValues(section, settings, [], HomepageLegacyIntegers, HomepageLegacyTexts);
        return section;
    }

    private static JsonObject BuildMusicSection(LauncherSettings settings)
    {
        JsonObject section = BuildOptionSection(settings, MusicBooleanKeys, MusicIntegerKeys, []);
        AddLegacyOptionValues(section, settings, MusicLegacyBooleans, MusicLegacyIntegers, []);
        return section;
    }

    private static JsonObject BuildUpdateSection(LauncherSettings settings)
    {
        JsonObject section = BuildOptionSection(settings, UpdateBooleanKeys, UpdateIntegerKeys, []);
        AddLegacyOptionValues(section, settings, UpdateLegacyBooleans, UpdateLegacyIntegers, []);
        return section;
    }

    private static JsonObject BuildCustomVariablesSection(LauncherSettings settings)
    {
        JsonObject variables = new();
        foreach ((string key, string value) in settings.TextOptions)
        {
            if (key.StartsWith("CustomVariable.", StringComparison.OrdinalIgnoreCase))
                variables[key["CustomVariable.".Length..]] = value;
        }

        return new JsonObject { ["custom_variables"] = variables };
    }

    private static void AddLegacyOptionValues(
        JsonObject section,
        LauncherSettings settings,
        IReadOnlyList<LegacySettingMap> booleans,
        IReadOnlyList<LegacySettingMap> integers,
        IReadOnlyList<LegacySettingMap> texts)
    {
        foreach (LegacySettingMap map in booleans)
            section[map.WireKey] = settings.GetBooleanOption(map.SettingKey, LauncherSettingDefaults.GetBoolean(map.SettingKey));
        foreach (LegacySettingMap map in integers)
            section[map.WireKey] = settings.GetIntegerOption(map.SettingKey, LauncherSettingDefaults.GetInteger(map.SettingKey));
        foreach (LegacySettingMap map in texts)
            section[map.WireKey] = settings.GetTextOption(map.SettingKey, LauncherSettingDefaults.GetText(map.SettingKey));
    }

    private static JsonObject BuildLegacyBooleanObject(
        LauncherSettings settings,
        params LegacySettingMap[] mappings)
    {
        JsonObject result = new();
        foreach (LegacySettingMap map in mappings)
            result[map.WireKey] = settings.GetBooleanOption(map.SettingKey, LauncherSettingDefaults.GetBoolean(map.SettingKey));
        return result;
    }

    private static JsonObject BuildOptionSection(
        LauncherSettings settings,
        IReadOnlyList<string> booleanKeys,
        IReadOnlyList<string> integerKeys,
        IReadOnlyList<string> textKeys)
    {
        JsonObject booleans = new();
        JsonObject integers = new();
        JsonObject texts = new();
        foreach (string key in booleanKeys)
        {
            if (settings.TryGetBooleanOption(key, out bool value))
                booleans[key] = value;
        }
        foreach (string key in integerKeys)
        {
            if (settings.TryGetIntegerOption(key, out int value))
                integers[key] = value;
        }
        foreach (string key in textKeys)
        {
            if (settings.TryGetTextOption(key, out string? value))
                texts[key] = value ?? string.Empty;
        }

        return new JsonObject
        {
            ["booleans"] = booleans,
            ["integers"] = integers,
            ["texts"] = texts
        };
    }

    private void ApplyAccount(JsonObject? data, bool overwrite)
    {
        if (data is null)
            return;

        ApplyString(data, "legal_accepted_version", "Online.LegalAcceptedVersion", overwrite: true);
        ApplyString(data, "msid", "Online.MsId", overwrite);
        ApplyString(data, "ms_user_name", "Online.MsUserName", overwrite);
        ApplyString(data, "minecraft_profile_name", "Online.MsMinecraftProfileName", overwrite);
        ApplyString(data, "ms_uuid", "Online.MsUuid", overwrite);
        ApplyString(data, "ms_avatar_url", "Online.MsAvatarUrl", overwrite);
        if ((overwrite || !GetBoolean("Online.MsOwnsMinecraft")) &&
            TryGetBoolean(data, "ms_owns_minecraft", out bool ownsMinecraft))
            SetBoolean("Online.MsOwnsMinecraft", ownsMinecraft);
    }

    private void ApplyString(JsonObject data, string sourceKey, string targetKey, bool overwrite)
    {
        if (TryGetString(data, sourceKey, out string value) &&
            (overwrite || string.IsNullOrWhiteSpace(GetString(targetKey))))
            SetString(targetKey, value);
    }

    private void ApplyFavorites(JsonObject? data)
    {
        if (data?["items"] is JsonArray items)
        {
            _favorites.ReplaceFromJson(items.ToJsonString());
            return;
        }

        if (data?["comp_favorites"] is not JsonArray legacyFolders)
            return;

        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? folder in legacyFolders)
        {
            if (folder?["Favs"] is not JsonArray favorites)
                continue;
            foreach (JsonNode? id in favorites)
            {
                string? value = id?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(value))
                    ids.Add(value);
            }
        }

        JsonArray migrated = new(ids.Select(CreateMigratedFavorite).ToArray<JsonNode?>());
        _favorites.ReplaceFromJson(migrated.ToJsonString());
    }

    private static JsonObject CreateMigratedFavorite(string projectId)
    {
        bool curseForge = long.TryParse(projectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        return new JsonObject
        {
            ["entry"] = new JsonObject
            {
                ["projectId"] = projectId,
                ["slug"] = projectId,
                ["title"] = projectId,
                ["summary"] = "从旧版收藏夹迁移",
                ["projectType"] = "mod",
                ["downloads"] = 0,
                ["source"] = curseForge ? 2 : 1
            },
            ["category"] = 0,
            ["addedAt"] = DateTimeOffset.UtcNow
        };
    }

    private static LauncherSettings ApplyUiSection(JsonObject? data, LauncherSettings settings)
    {
        if (data is null)
            return settings;
        ApplyOptionSection(data, settings, UiBooleanKeys, UiIntegerKeys, UiTextKeys);
        if (TryGetEnum(data, "color_mode", out ColorMode colorMode))
            settings = settings with { ColorMode = colorMode };
        if (TryGetEnum(data, "light_color", out ColorTheme lightColor))
            settings = settings with { LightColor = lightColor };
        if (TryGetEnum(data, "dark_color", out ColorTheme darkColor))
            settings = settings with { DarkColor = darkColor };
        if (TryGetEnum(data, "ui_dark_mode", out ColorMode legacyColorMode))
            settings = settings with { ColorMode = legacyColorMode };
        if (TryGetEnum(data, "ui_light_color", out ColorTheme legacyLightColor))
            settings = settings with { LightColor = legacyLightColor };
        if (TryGetEnum(data, "ui_dark_color", out ColorTheme legacyDarkColor))
            settings = settings with { DarkColor = legacyDarkColor };
        ApplyLegacyOptionValues(data, settings, UiLegacyBooleans, UiLegacyIntegers, UiLegacyTexts);
        ApplyLegacyBooleanObject(data["ui_hidden_pages"] as JsonObject, settings,
            new LegacySettingMap("page_download", "UiHiddenPageDownload"),
            new LegacySettingMap("page_setup", "UiHiddenPageSetup"),
            new LegacySettingMap("page_tools", "UiHiddenPageTools"));
        ApplyLegacyBooleanObject(data["ui_hidden_tools"] as JsonObject, settings,
            new LegacySettingMap("tools_help", "UiHiddenToolsHelp"),
            new LegacySettingMap("tools_test", "UiHiddenToolsTest"));
        ApplyLegacyBooleanObject(data["ui_hidden_instance_tabs"] as JsonObject, settings,
            new LegacySettingMap("instance_edit", "UiHiddenInstanceEdit"),
            new LegacySettingMap("instance_export", "UiHiddenInstanceExport"),
            new LegacySettingMap("instance_save", "UiHiddenInstanceSave"),
            new LegacySettingMap("instance_screenshot", "UiHiddenInstanceScreenshot"),
            new LegacySettingMap("instance_mod", "UiHiddenInstanceMod"),
            new LegacySettingMap("instance_resource_pack", "UiHiddenInstanceResourcePack"),
            new LegacySettingMap("instance_shader", "UiHiddenInstanceShader"),
            new LegacySettingMap("instance_schematic", "UiHiddenInstanceSchematic"),
            new LegacySettingMap("instance_server", "UiHiddenInstanceServer"));
        ApplyLegacyBooleanObject(data["ui_hidden_functions"] as JsonObject, settings,
            new LegacySettingMap("function_select", "UiHiddenFunctionSelect"),
            new LegacySettingMap("function_mod_update", "UiHiddenFunctionModUpdate"),
            new LegacySettingMap("function_hidden", "UiHiddenFunctionHidden"));
        return settings;
    }

    private static void ApplyHintSection(JsonObject? data, LauncherSettings settings)
    {
        ApplyOptionSection(data, settings, HintBooleanKeys, [], HintTextKeys);
        ApplyLegacyOptionValues(data, settings, HintLegacyBooleans, HintLegacyIntegers, HintLegacyTexts);
    }

    private static LauncherSettings ApplyDownloadSection(JsonObject? data, LauncherSettings settings)
    {
        if (data is null)
            return settings;
        ApplyOptionSection(data, settings, DownloadBooleanKeys, DownloadIntegerKeys, []);
        if (TryGetEnum(data, "download_source", out DownloadSourcePreference source))
            settings = settings with { DownloadSource = source };
        ApplyLegacyOptionValues(data, settings, DownloadLegacyBooleans, DownloadLegacyIntegers, []);
        if (TryGetInteger(data, "download_file_source", out int legacySource) &&
            Enum.IsDefined((DownloadSourcePreference)legacySource))
        {
            settings = settings with { DownloadSource = (DownloadSourcePreference)legacySource };
        }
        return settings;
    }

    private static void ApplyLaunchSection(JsonObject? data, LauncherSettings settings)
    {
        ApplyOptionSection(data, settings, LaunchBooleanKeys, LaunchIntegerKeys, LaunchTextKeys);
        ApplyLegacyOptionValues(data, settings, LaunchLegacyBooleans, LaunchLegacyIntegers, LaunchLegacyTexts);
    }

    private static void ApplyHomepageSection(JsonObject? data, LauncherSettings settings)
    {
        ApplyOptionSection(data, settings, [], HomepageIntegerKeys, HomepageTextKeys);
        ApplyLegacyOptionValues(data, settings, [], HomepageLegacyIntegers, HomepageLegacyTexts);
    }

    private static void ApplyMusicSection(JsonObject? data, LauncherSettings settings)
    {
        ApplyOptionSection(data, settings, MusicBooleanKeys, MusicIntegerKeys, []);
        ApplyLegacyOptionValues(data, settings, MusicLegacyBooleans, MusicLegacyIntegers, []);
    }

    private static void ApplyUpdateSection(JsonObject? data, LauncherSettings settings)
    {
        ApplyOptionSection(data, settings, UpdateBooleanKeys, UpdateIntegerKeys, []);
        ApplyLegacyOptionValues(data, settings, UpdateLegacyBooleans, UpdateLegacyIntegers, []);
    }

    private static void ApplyCustomVariablesSection(JsonObject? data, LauncherSettings settings)
    {
        if (data?["custom_variables"] is not JsonObject variables)
            return;

        foreach (string key in settings.TextOptions.Keys
                     .Where(static key => key.StartsWith("CustomVariable.", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            settings.TextOptions.Remove(key);
        foreach ((string key, JsonNode? value) in variables)
        {
            if (value is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out string? text))
                continue;
            string settingKey = key.StartsWith("CustomVariable.", StringComparison.OrdinalIgnoreCase)
                ? key
                : "CustomVariable." + key;
            settings.SetTextOption(settingKey, text ?? string.Empty);
        }
    }

    private static void ApplyLegacyOptionValues(
        JsonObject? data,
        LauncherSettings settings,
        IReadOnlyList<LegacySettingMap> booleans,
        IReadOnlyList<LegacySettingMap> integers,
        IReadOnlyList<LegacySettingMap> texts)
    {
        if (data is null)
            return;
        foreach (LegacySettingMap map in booleans)
        {
            if (TryGetBoolean(data, map.WireKey, out bool value))
                settings.SetBooleanOption(map.SettingKey, value);
        }
        foreach (LegacySettingMap map in integers)
        {
            if (TryGetInteger(data, map.WireKey, out int value))
                settings.SetIntegerOption(map.SettingKey, value);
        }
        foreach (LegacySettingMap map in texts)
        {
            if (TryGetString(data, map.WireKey, out string value))
                settings.SetTextOption(map.SettingKey, value);
        }
    }

    private static void ApplyLegacyBooleanObject(
        JsonObject? data,
        LauncherSettings settings,
        params LegacySettingMap[] mappings)
    {
        if (data is null)
            return;
        foreach (LegacySettingMap map in mappings)
        {
            if (TryGetBoolean(data, map.WireKey, out bool value))
                settings.SetBooleanOption(map.SettingKey, value);
        }
    }

    private static void ApplyOptionSection(
        JsonObject? data,
        LauncherSettings settings,
        IReadOnlyList<string> booleanKeys,
        IReadOnlyList<string> integerKeys,
        IReadOnlyList<string> textKeys)
    {
        if (data is null)
            return;
        HashSet<string> allowedBooleans = booleanKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> allowedIntegers = integerKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> allowedTexts = textKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (data["booleans"] is JsonObject booleans)
        {
            foreach ((string key, JsonNode? node) in booleans)
            {
                if (allowedBooleans.Contains(key) && TryGetBoolean(node, out bool value))
                    settings.SetBooleanOption(key, value);
            }
        }
        if (data["integers"] is JsonObject integers)
        {
            foreach ((string key, JsonNode? node) in integers)
            {
                if (allowedIntegers.Contains(key) && TryGetInteger(node, out int value))
                    settings.SetIntegerOption(key, value);
            }
        }
        if (data["texts"] is JsonObject texts)
        {
            foreach ((string key, JsonNode? node) in texts)
            {
                if (allowedTexts.Contains(key) && node is JsonValue value && value.TryGetValue<string>(out string? text))
                    settings.SetTextOption(key, text ?? string.Empty);
            }
        }
    }

    private void AddSection(
        Dictionary<string, JsonObject> snapshot,
        string sectionKey,
        string optionKey,
        Func<JsonObject> factory)
    {
        if (GetBoolean(optionKey))
            snapshot[sectionKey] = factory();
    }

    private static JsonObject? GetSection(IReadOnlyDictionary<string, JsonObject?> sections, string key) =>
        sections.TryGetValue(key, out JsonObject? section) ? section : null;

    private static bool TryGetString(JsonObject source, string key, out string value)
    {
        value = string.Empty;
        return source[key] is JsonValue jsonValue &&
               jsonValue.TryGetValue<string>(out value!);
    }

    private static bool TryGetBoolean(JsonObject source, string key, out bool value) =>
        TryGetBoolean(source[key], out value);

    private static bool TryGetBoolean(JsonNode? node, out bool value)
    {
        value = false;
        return node is JsonValue jsonValue &&
               (jsonValue.TryGetValue<bool>(out value) ||
                jsonValue.TryGetValue<int>(out int number) && (value = number != 0));
    }

    private static bool TryGetInteger(JsonNode? node, out int value)
    {
        value = 0;
        return node is JsonValue jsonValue && jsonValue.TryGetValue<int>(out value);
    }

    private static bool TryGetInteger(JsonObject source, string key, out int value) =>
        TryGetInteger(source[key], out value);

    private static bool TryGetEnum<TEnum>(JsonObject source, string key, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        if (source[key] is not JsonValue jsonValue)
            return false;
        if (jsonValue.TryGetValue<string>(out string? text) &&
            Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value))
            return true;
        return jsonValue.TryGetValue<int>(out int number) &&
               Enum.IsDefined(value = (TEnum)Enum.ToObject(typeof(TEnum), number));
    }

    private readonly record struct LegacySettingMap(string WireKey, string SettingKey);
}

internal static class DesktopOnlineRuntime
{
    private static readonly object Gate = new();
    private static DesktopOnlineRuntimeHost? _host;

    public static DesktopOnlineRuntimeHost Host =>
        _host ?? throw new InvalidOperationException("Desktop online runtime 尚未初始化。");

    public static void Initialize(CommunityFavoritesStore favorites)
    {
        ArgumentNullException.ThrowIfNull(favorites);
        lock (Gate)
        {
            if (_host?.UsesFavoritesStore(favorites) == true)
                return;

            _host = new DesktopOnlineRuntimeHost(favorites);
            OnlineRuntime.Configure(_host);
            OnlineUiScheduler.Configure(action =>
                Dispatcher.UIThread.CheckAccess()
                    ? RunInline(action)
                    : Dispatcher.UIThread.InvokeAsync(action).GetTask());
            PortableLog.Info("Online", "Avalonia 在线运行时与 N Cloud 同步适配器已初始化。");
        }
    }

    private static Task RunInline(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
