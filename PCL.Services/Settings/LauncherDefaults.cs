namespace PCL.Services.Settings;

/// <summary>
/// The concrete launcher settings key universe and default values, migrated from the legacy
/// launcher settings tables. This is the data-compatibility contract: every key the legacy
/// launcher ever persisted has a declared type and default here, and defaults are byte-equal
/// to the legacy tables.
/// </summary>
public static class LauncherDefaults
{
    public static IReadOnlyDictionary<string, bool> BooleanDefaults { get; } = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        ["SystemDisableHardwareAcceleration"] = false,
        // Disable UI motion when compositor/GPU timers misbehave (OOBE / page transitions).
        ["SystemDisableUiAnimations"] = false,
        ["SystemNetEnableDoH"] = true,
        ["SystemDebugMode"] = false,
        ["SystemDebugDelay"] = false,
        ["SystemDebugSkipCopy"] = false,
        ["ToolDownloadAutoSelectVersion"] = true,
        ["ToolFixAuthlib"] = true,
        ["ToolDownloadIgnoreQuilt"] = false,
        ["ToolDownloadAutoInstallDependencies"] = true,
        ["ToolHelpChinese"] = true,
        ["ToolUpdateRelease"] = false,
        ["ToolUpdateSnapshot"] = false,
        ["UiLauncherLogo"] = true,
        ["UiLockWindowSize"] = false,
        ["UiUltraLowPowerMode"] = false,
        ["UiShowLaunchingHint"] = true,
        ["UiHintAlignRight"] = false,
        ["UiLogoLeft"] = false,
        ["UiBackgroundColorful"] = true,
        ["UiAutoPauseVideo"] = true,
        ["UiBlur"] = false,
        ["UiMusicStop"] = false,
        ["UiMusicStart"] = false,
        ["UiMusicAuto"] = true,
        ["UiMusicRandom"] = true,
        ["UiMusicSMTC"] = true,
        ["LaunchAdvanceRunWait"] = true,
        ["LaunchAdvanceDisableJLW"] = true,
        ["LaunchAdvanceDisableRW"] = false,
        ["LaunchAdvanceGraphicCard"] = true,
        ["LaunchAdvanceNoJavaw"] = false,
        ["LaunchAdvanceDisableLwjglUnsafeAgent"] = false,
        ["LaunchUseSystemGlfw"] = false,
        ["LaunchForceX11OnWayland"] = true,
        ["LaunchAutoRepairGame"] = true,
        ["ToolDownloadClipboard"] = false,
        ["UiHideNEditionHint"] = false,
        ["ExperimentalJvmLifecycleHost"] = false,
        ["ExperimentalHomepageUi"] = false,
        ["ExperimentalNextRenderBackend"] = false,
        ["ExperimentalLaunchShortcuts"] = false,
        ["ExperimentalMinecraftAiRepair"] = false,
        ["TelemetryExperienceProgram"] = false,
    };

    public static IReadOnlyDictionary<string, int> IntegerDefaults { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["SystemLogLevel"] = 2,
        ["SystemMaxLog"] = 13,
        ["UiAniFPS"] = 59,
        ["SystemHttpProxyType"] = 1,
        ["SystemDebugAnim"] = 9,
        ["ToolDownloadThread"] = 63,
        ["ToolDownloadSpeed"] = 42,
        ["ToolDownloadSource"] = 1,
        ["ToolDownloadVersion"] = 1,
        ["ToolDownloadTranslateV2"] = 1,
        ["ToolDownloadMod"] = 1,
        ["ToolModLocalNameStyle"] = 0,
        ["SystemUpdateMode"] = 1,
        ["SystemUpdateChannel"] = 0,
        ["SystemSystemActivity"] = 0,
        ["UiDarkMode"] = 2,
        ["UiLauncherTransparent"] = 600,
        ["UiBackgroundOpacity"] = 1000,
        ["UiBackgroundBlur"] = 0,
        ["UiBackgroundSuit"] = 0,
        ["UiBlurValue"] = 16,
        ["UiBlurSamplingRate"] = 70,
        ["UiBlurType"] = 0,
        ["UiCustomType"] = 0,
        ["UiCustomPreset"] = 14,
        ["UiMusicVolume"] = 500,
        ["UiLogoType"] = 1,
        ["LaunchRamType"] = 0,
        ["LaunchRamCustom"] = 15,
        ["LaunchPreferredIpStack"] = 1,
        ["LaunchAdvanceRenderer"] = 0,
        ["LaunchArgumentIndieV2"] = 4,
        ["LaunchArgumentVisible"] = 5,
        ["LaunchArgumentPriority"] = 1,
        ["LaunchArgumentWindowWidth"] = 854,
        ["LaunchArgumentWindowHeight"] = 480,
        ["LaunchArgumentWindowType"] = 1,
        ["ExperimentalMinecraftAiProvider"] = 0,
        ["ExperimentalMinecraftAiLocalModel"] = 0,
        ["ExperimentalMinecraftAiTokenBudget"] = 4096,
        // 0=None, 1=Low, 2=Medium, 3=High — default Medium so OpenAI-compatible thinking is on.
        ["ExperimentalMinecraftAiReasoningEffort"] = 2,
        ["LoginMsAuthType"] = 1,
    };

    public static IReadOnlyDictionary<string, string> TextDefaults { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["SystemHttpProxy"] = string.Empty,
        [Minecraft.MinecraftLibraryService.SettingKey] = string.Empty,
        ["SystemHttpProxyCustomUsername"] = string.Empty,
        ["SystemHttpProxyCustomPassword"] = string.Empty,
        ["UiLogoText"] = string.Empty,
        ["UiLanguage"] = "auto",
        ["UiFormatCulture"] = "auto",
        ["LaunchAdvanceJvm"] = "-XX:+UseG1GC -XX:-UseAdaptiveSizePolicy -XX:-OmitStackTraceInFastThrow -Djdk.lang.Process.allowAmbiguousCommands=true -Dfml.ignoreInvalidMinecraftCertificates=True -Dfml.ignorePatchDiscrepancies=True -Dlog4j2.formatMsgNoLookups=true",
        ["LaunchAdvanceGame"] = string.Empty,
        ["LaunchWrapperCommand"] = string.Empty,
        ["LaunchAdvanceRun"] = string.Empty,
        ["LaunchArgumentTitle"] = string.Empty,
        ["LaunchArgumentInfo"] = "PCLN",
        ["ExperimentalMinecraftAiModelPath"] = string.Empty,
        ["ExperimentalMinecraftAiModelSha256"] = string.Empty,
        ["ExperimentalMinecraftAiRuntimePath"] = string.Empty,
        ["ExperimentalMinecraftAiApiBaseUrl"] = string.Empty,
        ["ExperimentalMinecraftAiApiModel"] = "gemma-4-e2b",
    };

    /// <summary>
    /// Builds the launcher settings schema: every legacy key with its declared type and default.
    /// </summary>
    public static SettingsSchema CreateSchema()
    {
        SettingsSchemaBuilder builder = new();
        foreach ((string key, bool value) in BooleanDefaults)
        {
            builder.AddBool(key, value);
        }

        foreach ((string key, int value) in IntegerDefaults)
        {
            builder.AddInt32(key, value);
        }

        foreach ((string key, string value) in TextDefaults)
        {
            builder.AddString(key, value);
        }

        return builder.Build();
    }
}
