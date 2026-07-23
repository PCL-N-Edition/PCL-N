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
        AddSection(snapshot, "hintPreferences", "Online.CloudSyncHintPreferences", () =>
            BuildOptionSection(settings, HintBooleanKeys, [], HintTextKeys));
        AddSection(snapshot, "downloadPreferences", "Online.CloudSyncDownloadPreferences", () =>
            BuildDownloadSection(settings));
        AddSection(snapshot, "launchPreferences", "Online.CloudSyncLaunchPreferences", () =>
            BuildOptionSection(settings, LaunchBooleanKeys, LaunchIntegerKeys, LaunchTextKeys));
        AddSection(snapshot, "homepagePreferences", "Online.CloudSyncHomepagePreferences", () =>
            BuildOptionSection(settings, [], HomepageIntegerKeys, HomepageTextKeys));
        AddSection(snapshot, "musicPreferences", "Online.CloudSyncMusicPreferences", () =>
            BuildOptionSection(settings, MusicBooleanKeys, MusicIntegerKeys, []));
        AddSection(snapshot, "updatePreferences", "Online.CloudSyncUpdatePreferences", () =>
            BuildOptionSection(settings, UpdateBooleanKeys, UpdateIntegerKeys, []));
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

    private void ApplySections(IReadOnlyDictionary<string, JsonObject?> sections, bool overwriteAccount)
    {
        if (GetBoolean("Online.CloudSyncAccount"))
            ApplyAccount(GetSection(sections, "account"), overwriteAccount);

        LauncherSettingsPageBinder.UpdateSettings(settings =>
        {
            if (GetBoolean("Online.CloudSyncUiPreferences"))
                settings = ApplyUiSection(GetSection(sections, "uiPreferences"), settings);
            if (GetBoolean("Online.CloudSyncHintPreferences"))
                ApplyOptionSection(GetSection(sections, "hintPreferences"), settings, HintBooleanKeys, [], HintTextKeys);
            if (GetBoolean("Online.CloudSyncDownloadPreferences"))
                settings = ApplyDownloadSection(GetSection(sections, "downloadPreferences"), settings);
            if (GetBoolean("Online.CloudSyncLaunchPreferences"))
                ApplyOptionSection(GetSection(sections, "launchPreferences"), settings, LaunchBooleanKeys, LaunchIntegerKeys, LaunchTextKeys);
            if (GetBoolean("Online.CloudSyncHomepagePreferences"))
                ApplyOptionSection(GetSection(sections, "homepagePreferences"), settings, [], HomepageIntegerKeys, HomepageTextKeys);
            if (GetBoolean("Online.CloudSyncMusicPreferences"))
                ApplyOptionSection(GetSection(sections, "musicPreferences"), settings, MusicBooleanKeys, MusicIntegerKeys, []);
            if (GetBoolean("Online.CloudSyncUpdatePreferences"))
                ApplyOptionSection(GetSection(sections, "updatePreferences"), settings, UpdateBooleanKeys, UpdateIntegerKeys, []);
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
        return section;
    }

    private static JsonObject BuildDownloadSection(LauncherSettings settings)
    {
        JsonObject section = BuildOptionSection(settings, DownloadBooleanKeys, DownloadIntegerKeys, []);
        section["download_source"] = settings.DownloadSource.ToString();
        return section;
    }

    private static JsonObject BuildCustomVariablesSection(LauncherSettings settings)
    {
        JsonObject variables = new();
        foreach ((string key, string value) in settings.TextOptions)
        {
            if (key.StartsWith("CustomVariable.", StringComparison.OrdinalIgnoreCase))
                variables[key] = value;
        }

        return new JsonObject { ["custom_variables"] = variables };
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
        return settings;
    }

    private static LauncherSettings ApplyDownloadSection(JsonObject? data, LauncherSettings settings)
    {
        if (data is null)
            return settings;
        ApplyOptionSection(data, settings, DownloadBooleanKeys, DownloadIntegerKeys, []);
        if (TryGetEnum(data, "download_source", out DownloadSourcePreference source))
            settings = settings with { DownloadSource = source };
        return settings;
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
            if (key.StartsWith("CustomVariable.", StringComparison.OrdinalIgnoreCase) &&
                value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out string? text))
                settings.SetTextOption(key, text ?? string.Empty);
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
