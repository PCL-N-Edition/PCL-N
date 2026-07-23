// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using System.Globalization;
using System.Runtime.CompilerServices;
using PCL.Application.Settings;
using PCL.Core.App;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Localization;
using PCL.Desktop.Theme;
using PCL.Platform.Paths;
using PCL.Core.Logging;
using PCL.Core.Platform;

namespace PCL.Desktop.Features.Settings.Views;

internal static class LauncherSettingsPageBinder
{
    private const string SettingsPathOverrideEnvironmentVariable = "PCLN_LAUNCHER_SETTINGS_PATH";
    private static readonly ConditionalWeakTable<MyPageRight, BindingState> BindingStates = new();

    internal static IReadOnlyList<ColorTheme> ThemeOrder => ThemeAvailabilityPolicy.GetAvailableThemes();

    internal static string GetThemeName(ColorTheme theme) => theme switch
    {
        ColorTheme.SystemAccent => AvaloniaLocalizationManager.GetText("Setup.Ui.ThemeColor.SystemAccent", "跟随系统"),
        ColorTheme.SkyBlue => AvaloniaLocalizationManager.GetText("Setup.Ui.ThemeColor.SkyBlue", "天空蓝"),
        ColorTheme.CatBlue => AvaloniaLocalizationManager.GetText("Setup.Ui.ThemeColor.CatBlue", "龙猫蓝"),
        ColorTheme.DeathBlue => AvaloniaLocalizationManager.GetText("Setup.Ui.ThemeColor.CrashBlue", "死亡蓝"),
        ColorTheme.Custom => AvaloniaLocalizationManager.GetText("Setup.Ui.ThemeColor.Custom", "调色盘"),
        ColorTheme.HmclBlue => AvaloniaLocalizationManager.GetText("Setup.Ui.ThemeColor.Hmcl", "HMCL 蓝"),
        _ => theme.ToString()
    };

    internal static event Action<LauncherSettings>? SettingsChanged;

    public static void Attach(MyPageRight page, Action<LauncherSettings>? settingsApplied = null)
    {
        ArgumentNullException.ThrowIfNull(page);

        BindingState state = new(page, settingsApplied);
        BindingStates.Remove(page);
        BindingStates.Add(page, state);
        LauncherSettings settings = LoadSettings();
        ApplySettings(page, settings);
        settingsApplied?.Invoke(settings);
        state.IsApplying = false;
        Window? ownerWindow = null;
        page.AttachedToVisualTree += (_, _) =>
        {
            // Re-apply after attach so DynamicResource item text resolves and defaults stick.
            state.IsApplying = true;
            try
            {
                LauncherSettings attachedSettings = LoadSettings();
                ApplySettings(page, attachedSettings);
                state.SettingsApplied?.Invoke(attachedSettings);
            }
            finally
            {
                state.IsApplying = false;
            }

            if (TopLevel.GetTopLevel(page) is not Window window || ReferenceEquals(ownerWindow, window))
                return;

            if (ownerWindow is not null)
                UnwireOwnerWindow(ownerWindow);

            ownerWindow = window;
            ownerWindow.Closing += OwnerWindow_Closing;
        };
        // While detached, ignore control change storms; re-enable on attach (above).
        page.DetachedFromVisualTree += (_, _) => state.IsApplying = true;
        page.DetachedFromLogicalTree += (_, _) => state.IsApplying = true;

        void OwnerWindow_Closing(object? sender, WindowClosingEventArgs e)
        {
            state.IsApplying = true;
            if (ownerWindow is not null)
            {
                UnwireOwnerWindow(ownerWindow);
            }
            ownerWindow = null;
        }

        void UnwireOwnerWindow(Window window)
        {
            window.Closing -= OwnerWindow_Closing;
        }

        foreach (MyCheckBox checkBox in GetControlDescendants(page).OfType<MyCheckBox>())
        {
            checkBox.Change += (_, _) =>
            {
                if (state.IsApplying || !IsInteractive(page))
                    return;

                string? tag = GetTag(checkBox);
                if (string.IsNullOrWhiteSpace(tag))
                    return;

                bool value = checkBox.Checked == true;
                UpdateSettings(current =>
                {
                    current.SetBooleanOption(tag, value);
                    return tag == "LaunchAutoRepairGame"
                        ? current with { AutomaticallyRepairGameIssues = value }
                        : current;
                });
            };
        }

        foreach (MyComboBox comboBox in GetControlDescendants(page).OfType<MyComboBox>())
        {
            void PersistComboBox()
            {
                // Do not gate on window activation: ComboBox popups / dialogs can deactivate
                // the owner and would otherwise drop user selections (e.g. update channel/mode).
                if (state.IsApplying || comboBox.SelectedIndex < 0)
                    return;
                if (!page.IsAttachedToVisualTree())
                    return;

                string? tag = GetTag(comboBox);
                if (string.IsNullOrWhiteSpace(tag))
                    return;
                if (page is IDeferredSettingsPersistence deferred && deferred.IsPersistenceDeferred(tag))
                    return;

                settings = LoadSettings();
                ColorTheme? selectedTheme = tag is "UiLightColor" or "UiDarkColor"
                    ? GetTheme(comboBox.SelectedIndex)
                    : null;
                if (selectedTheme == ColorTheme.SystemAccent && !PlatformFeaturePolicy.IsSystemAccentThemeSupported)
                {
                    state.IsApplying = true;
                    try
                    {
                        selectedTheme = ColorTheme.CatBlue;
                        SetComboIndex(comboBox, GetThemeIndex(selectedTheme.Value));
                    }
                    finally
                    {
                        state.IsApplying = false;
                    }
                    if (page is PageSetupUI uiPage)
                    {
                        uiPage.RequestThemeMessage(
                            AvaloniaLocalizationManager.GetText("Setup.Ui.ThemeColor.SystemAccent.Unsupported.Title", "此功能在 Windows 上不可用"),
                            AvaloniaLocalizationManager.GetText(
                                "Setup.Ui.ThemeColor.SystemAccent.Unsupported",
                                "“跟随系统主题色”仅在 Linux 和 macOS 上受支持。根据 PCL 上游使用指南第 5 条，Windows 版本不能提供与赞助解锁主题类似的表现，因此配色将切回“龙猫蓝”。"));
                    }
                }
                else if (selectedTheme == ColorTheme.Custom && !PlatformFeaturePolicy.IsCustomColorPaletteSupported)
                {
                    state.IsApplying = true;
                    try
                    {
                        selectedTheme = ColorTheme.CatBlue;
                        SetComboIndex(comboBox, GetThemeIndex(selectedTheme.Value));
                    }
                    finally
                    {
                        state.IsApplying = false;
                    }
                }
                else if (selectedTheme == ColorTheme.Custom && page is PageSetupUI colorPage)
                {
                    LauncherSettings original = settings;
                    bool dark = tag == "UiDarkColor";
                    string colorKey = dark ? "UiCustomDarkColor" : "UiCustomLightColor";
                    string initialText = settings.GetTextOption(colorKey, dark ? "#6F8CFF" : "#3D7DFF");
                    Color initial = ThemeColorPalette.TryParseColor(initialText, out Color parsed) ? parsed : Color.Parse("#3D7DFF");
                    colorPage.RequestThemeColor(
                        AvaloniaLocalizationManager.GetText("Setup.Ui.ThemeColor.Custom", "调色盘"),
                        initial,
                        preview =>
                        {
                            LauncherSettings previewSettings = dark
                                ? settings with { DarkColor = ColorTheme.Custom }
                                : settings with { LightColor = ColorTheme.Custom };
                            previewSettings.SetTextOption(colorKey, preview.ToString());
                            AvaloniaThemeManager.Apply(previewSettings);
                        },
                        chosen =>
                        {
                            if (chosen is null)
                            {
                                AvaloniaThemeManager.Apply(original);
                                state.IsApplying = true;
                                try
                                {
                                    SetComboIndex(comboBox, GetThemeIndex(dark ? original.DarkColor : original.LightColor));
                                }
                                finally
                                {
                                    state.IsApplying = false;
                                }
                                return;
                            }

                            int selectedIndex = comboBox.SelectedIndex;
                            LauncherSettings confirmed = UpdateSettings(current =>
                            {
                                LauncherSettings updated = dark
                                    ? current with { DarkColor = ColorTheme.Custom }
                                    : current with { LightColor = ColorTheme.Custom };
                                updated.SetTextOption(colorKey, chosen.Value.ToString());
                                updated.SetIntegerOption(tag, selectedIndex);
                                return updated;
                            }, notify: false);
                            ThemeAvailabilityPolicy.MarkManualThemeSelection();
                            AvaloniaThemeManager.Apply(confirmed);
                            SettingsChanged?.Invoke(confirmed);
                        });
                    return;
                }

                int comboValue = GetComboValue(comboBox);
                bool shouldApplyTheme = false;
                int selectedIndex = comboBox.SelectedIndex;
                string editableText = comboBox.Text ?? string.Empty;
                bool isEditable = comboBox.IsEditable;
                settings = UpdateSettings(current =>
                {
                    current.SetIntegerOption(tag, comboValue);
                    LauncherSettings updated = tag switch
                    {
                        "UiDarkMode" => current with
                        {
                            ColorMode = (ColorMode)Math.Clamp(selectedIndex, 0, 2)
                        },
                        "UiLightColor" => current with { LightColor = GetTheme(selectedIndex) },
                        "UiDarkColor" => current with { DarkColor = GetTheme(selectedIndex) },
                        "ToolDownloadSource" or "ToolDownloadVersion" or "ToolDownloadMod" => current with
                        {
                            DownloadSource = (DownloadSourcePreference)Math.Clamp(selectedIndex, 0, 2)
                        },
                        _ => current
                    };
                    if (isEditable)
                        updated.SetTextOption(tag, editableText);
                    return updated;
                }, notify: false);
                shouldApplyTheme = tag is "UiDarkMode" or "UiLightColor" or "UiDarkColor";
                if (tag is "UiLightColor" or "UiDarkColor")
                    ThemeAvailabilityPolicy.MarkManualThemeSelection();

                // Apply theme palette first so SettingsChanged / form chrome see new colors.
                if (shouldApplyTheme)
                    AvaloniaThemeManager.Apply(settings);
                SettingsChanged?.Invoke(settings);
            }

            comboBox.SelectionChanged += (_, _) => PersistComboBox();
            comboBox.GetObservable(ComboBox.SelectedIndexProperty).Subscribe(_ => PersistComboBox());

            if (comboBox.IsEditable)
            {
                comboBox.TextChanged += (_, _) =>
                {
                    if (state.IsApplying || !IsInteractive(page))
                        return;

                    string? tag = GetTag(comboBox);
                    if (string.IsNullOrWhiteSpace(tag))
                        return;

                    string text = comboBox.Text ?? string.Empty;
                    UpdateSettings(current =>
                    {
                        current.SetTextOption(tag, text);
                        return current;
                    });
                };
            }
        }

        foreach (MySlider slider in GetControlDescendants(page).OfType<MySlider>())
        {
            slider.Change += (_, _) =>
            {
                if (state.IsApplying || !IsInteractive(page))
                    return;

                string? tag = GetTag(slider);
                if (string.IsNullOrWhiteSpace(tag))
                    return;

                int value = slider.Value;
                UpdateSettings(current =>
                {
                    current.SetIntegerOption(tag, value);
                    return current;
                });
            };
        }

        foreach (MyTextBox textBox in GetControlDescendants(page).OfType<MyTextBox>())
        {
            textBox.TextChanged += (_, _) =>
            {
                if (state.IsApplying || !IsInteractive(page))
                    return;

                string? tag = GetTag(textBox);
                if (string.IsNullOrWhiteSpace(tag))
                    return;

                int? integerValue = null;
                string text = textBox.Text ?? string.Empty;
                if (tag == LauncherSettingKeys.ExperimentalMinecraftAiTokenBudget.Value)
                {
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedValue))
                        return;
                    integerValue = parsedValue;
                }
                UpdateSettings(current =>
                {
                    if (integerValue is int parsedValue)
                        current.SetIntegerOption(tag, parsedValue);
                    else
                        current.SetTextOption(tag, text);
                    return current;
                });
            };
        }

        foreach (MyRadioBox radioBox in GetControlDescendants(page).OfType<MyRadioBox>())
        {
            radioBox.Check += (_, _) =>
            {
                if (state.IsApplying || !IsInteractive(page) || !radioBox.Checked)
                    return;

                if (!TryParseRadioTag(GetTag(radioBox), out string? key, out int value))
                    return;

                UpdateSettings(current =>
                {
                    current.SetIntegerOption(key, value);
                    return current;
                });
            };
        }
    }

    internal static bool ResetPage(MyPageRight page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!BindingStates.TryGetValue(page, out BindingState? state))
            return false;

        state.IsApplying = true;
        try
        {
            LauncherSettings defaults = new();
            bool resetAutoRepair = page is PageSetupLaunch;
            bool resetColorMode = page is PageSetupUI;
            bool resetLightColor = page is PageSetupUI;
            bool resetDarkColor = page is PageSetupUI;
            bool resetDownloadSource = page is PageSetupGameManage;
            List<string> settingKeys = [];

            foreach (Control control in state.TaggedControls)
            {
                string? tag = GetTag(control);
                if (string.IsNullOrWhiteSpace(tag))
                    continue;

                string settingKey = control is MyRadioBox && TryParseRadioTag(tag, out string key, out _)
                    ? key
                    : tag;
                settingKeys.Add(settingKey);
                resetAutoRepair |= tag == "LaunchAutoRepairGame";
                resetColorMode |= tag == "UiDarkMode";
                resetLightColor |= tag == "UiLightColor";
                resetDarkColor |= tag == "UiDarkColor";
                resetDownloadSource |= tag is "ToolDownloadSource" or "ToolDownloadVersion" or "ToolDownloadMod";
            }

            LauncherSettings settings = UpdateSettings(current =>
            {
                foreach (string settingKey in settingKeys)
                {
                    current.BooleanOptions.Remove(settingKey);
                    current.IntegerOptions.Remove(settingKey);
                    current.TextOptions.Remove(settingKey);
                }

                return current with
                {
                    AutomaticallyRepairGameIssues = resetAutoRepair
                        ? defaults.AutomaticallyRepairGameIssues
                        : current.AutomaticallyRepairGameIssues,
                    ColorMode = resetColorMode ? defaults.ColorMode : current.ColorMode,
                    LightColor = resetLightColor ? defaults.LightColor : current.LightColor,
                    DarkColor = resetDarkColor ? defaults.DarkColor : current.DarkColor,
                    DownloadSource = resetDownloadSource ? defaults.DownloadSource : current.DownloadSource
                };
            });
            state.RestoreControlDefaults();
            ApplySettings(page, settings);
            state.SettingsApplied?.Invoke(settings);
            if (page is IRefreshableSettingsPage refreshable)
                refreshable.RefreshPage();
            return true;
        }
        finally
        {
            state.IsApplying = !page.IsAttachedToVisualTree();
        }
    }

    internal static bool ReloadPage(MyPageRight page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!BindingStates.TryGetValue(page, out BindingState? state))
            return false;

        state.IsApplying = true;
        try
        {
            LauncherSettings settings = LoadSettings();
            state.RestoreControlDefaults();
            ApplySettings(page, settings);
            state.SettingsApplied?.Invoke(settings);
            if (page is IRefreshableSettingsPage refreshable)
                refreshable.RefreshPage();
            return true;
        }
        finally
        {
            state.IsApplying = !page.IsAttachedToVisualTree();
        }
    }

    private static void ApplySettings(MyPageRight page, LauncherSettings settings)
    {
        foreach (MyComboBox comboBox in GetControlDescendants(page).OfType<MyComboBox>())
        {
            string? tag = GetTag(comboBox);
            if (string.IsNullOrWhiteSpace(tag))
                continue;

            if (tag is "UiLightColor" or "UiDarkColor")
                EnsureThemeColorItems(comboBox);

            if (tag == "UiDarkMode")
                SetComboIndex(comboBox, (int)settings.ColorMode);
            else if (tag == "UiLightColor")
                SetComboIndex(comboBox, GetThemeIndex(settings.LightColor));
            else if (tag == "UiDarkColor")
                SetComboIndex(comboBox, GetThemeIndex(settings.DarkColor));
            else if (tag is "ToolDownloadSource" or "ToolDownloadVersion" or "ToolDownloadMod")
                SetComboIndex(comboBox, (int)settings.DownloadSource);
            else if (settings.TryGetIntegerOption(tag, out int index))
                SetComboValue(comboBox, index);
            else
            {
                int defaultIndex = LauncherSettingDefaults.GetInteger(
                    tag,
                    comboBox.SelectedIndex >= 0 ? comboBox.SelectedIndex : 0);
                SetComboValue(comboBox, defaultIndex);
            }

            // Guarantee a visible selection even if ItemCount was briefly 0 or value was out of range.
            if (comboBox.ItemCount > 0 && comboBox.SelectedIndex < 0)
            {
                int fallback = LauncherSettingDefaults.GetInteger(tag, 0);
                SetComboIndex(comboBox, fallback);
            }

            if (comboBox.IsEditable && settings.TryGetTextOption(tag, out string? text))
                comboBox.Text = text ?? string.Empty;
            else if (comboBox.IsEditable)
                comboBox.Text = LauncherSettingDefaults.GetText(tag, comboBox.Text ?? string.Empty);

            // Force visible selection text (string ItemsSource / DynamicResource could leave blank).
            comboBox.RefreshSelectionDisplay();
        }

        foreach (MyCheckBox checkBox in GetControlDescendants(page).OfType<MyCheckBox>())
        {
            string? tag = GetTag(checkBox);
            if (string.IsNullOrWhiteSpace(tag))
                continue;

            if (tag == "LaunchAutoRepairGame")
                checkBox.Checked = settings.AutomaticallyRepairGameIssues;
            else if (settings.TryGetBooleanOption(tag, out bool value))
                checkBox.Checked = value;
            else
            {
                // Use LauncherSettingDefaults when present; otherwise keep XAML Checked.
                bool xamlFallback = checkBox.Checked == true;
                checkBox.Checked = LauncherSettingDefaults.GetBoolean(tag, xamlFallback);
            }
        }

        foreach (MySlider slider in GetControlDescendants(page).OfType<MySlider>())
        {
            string? tag = GetTag(slider);
            if (!string.IsNullOrWhiteSpace(tag))
            {
                int value = settings.TryGetIntegerOption(tag, out int configured)
                    ? configured
                    : LauncherSettingDefaults.GetInteger(tag, slider.Value);
                slider.Value = Math.Clamp(value, 0, slider.MaxValue);
            }
        }

        foreach (MyTextBox textBox in GetControlDescendants(page).OfType<MyTextBox>())
        {
            string? tag = GetTag(textBox);
            if (!string.IsNullOrWhiteSpace(tag))
            {
                if (tag == LauncherSettingKeys.ExperimentalMinecraftAiTokenBudget.Value)
                {
                    int integerValue = settings.TryGetIntegerOption(tag, out int configuredInteger)
                        ? configuredInteger
                        : LauncherSettingDefaults.GetInteger(tag, 4096);
                    textBox.Text = integerValue.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    textBox.Text = settings.TryGetTextOption(tag, out string? value)
                        ? value ?? string.Empty
                        : LauncherSettingDefaults.GetText(tag, textBox.Text ?? string.Empty);
                }
            }
        }

        foreach (IGrouping<string, MyRadioBox> group in GetControlDescendants(page)
                     .OfType<MyRadioBox>()
                     .Select(static radio => (Radio: radio, Parsed: TryParseRadioTag(GetTag(radio), out string? key, out int value)
                         ? (Key: key, Value: value)
                         : ((string Key, int Value)?)null))
                     .Where(static item => item.Parsed is not null)
                     .GroupBy(static item => item.Parsed!.Value.Key, static item => item.Radio))
        {
            int selectedValue = settings.TryGetIntegerOption(group.Key, out int configuredValue)
                ? configuredValue
                : LauncherSettingDefaults.GetInteger(group.Key);

            foreach (MyRadioBox radioBox in group)
            {
                if (TryParseRadioTag(GetTag(radioBox), out _, out int value))
                    radioBox.Checked = value == selectedValue;
            }
        }
    }

    internal static LauncherSettings LoadSettings()
    {
        string path = CreateSettingsPath();
        try
        {
            using LauncherSettingsStore store = new(path);
            LauncherSettings settings = store.LoadAsync().AsTask().GetAwaiter().GetResult().Settings;
            PortableLog.Debug(
                "Settings",
                $"设置读取完成；Path={path}；Bool={settings.BooleanOptions.Count}；Int={settings.IntegerOptions.Count}；Text={settings.TextOptions.Count}。");
            return settings.NormalizeOptionDictionaries();
        }
        catch (Exception ex)
        {
            PortableLog.Error(ex, "Settings", $"读取启动器设置失败：{path}");
            throw;
        }
    }

    internal static void SaveSettings(LauncherSettings settings, bool notify = true)
    {
        string settingsPath = CreateSettingsPath();
        try
        {
            using LauncherSettingsStore store = new(settingsPath);
            store.SaveAsync(settings).AsTask().GetAwaiter().GetResult();
            PortableLog.Debug(
                "Settings",
                $"设置保存完成；Path={settingsPath}；Bool={settings.BooleanOptions.Count}；Int={settings.IntegerOptions.Count}；Text={settings.TextOptions.Count}。");
            // Session stores (folders/instances) persist without re-entering UI chrome updates.
            if (notify)
                SettingsChanged?.Invoke(settings);
        }
        catch (Exception ex)
        {
            PortableLog.Error(ex, "Settings", $"保存启动器设置失败：{settingsPath}");
            throw;
        }
    }

    internal static void NotifySettingsChanged() => SettingsChanged?.Invoke(LoadSettings());

    internal static void SaveIntegerOption(string key, int value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        UpdateSettings(settings =>
        {
            settings.SetIntegerOption(key, value);
            return settings;
        });
    }

    internal static LauncherSettings UpdateSettings(
        Func<LauncherSettings, LauncherSettings> update,
        bool notify = true)
    {
        ArgumentNullException.ThrowIfNull(update);
        string settingsPath = CreateSettingsPath();
        try
        {
            using LauncherSettingsStore store = new(settingsPath);
            LauncherSettings settings = store.UpdateAsync(update).AsTask().GetAwaiter().GetResult();
            PortableLog.Debug(
                "Settings",
                $"设置事务更新完成；Path={settingsPath}；Bool={settings.BooleanOptions.Count}；Int={settings.IntegerOptions.Count}；Text={settings.TextOptions.Count}。");
            if (notify)
                SettingsChanged?.Invoke(settings);
            return settings;
        }
        catch (Exception ex)
        {
            PortableLog.Error(ex, "Settings", $"事务更新启动器设置失败：{settingsPath}");
            throw;
        }
    }

    internal static string CreateSettingsPath()
    {
        string? overridePath = Environment.GetEnvironmentVariable(SettingsPathOverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);

        DefaultPlatformPathProvider paths = new();
        return Path.Combine(paths.ApplicationDataDirectory, "PCL-N", "launcher-settings.json");
    }

    internal static string CreateDataDirectory()
    {
        string settingsDirectory = Path.GetDirectoryName(CreateSettingsPath()) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(settingsDirectory);
        return settingsDirectory;
    }

    private static string? GetTag(Control control) => control.Tag?.ToString();

    private static IEnumerable<Control> GetControlDescendants(Control page) =>
        page.GetVisualDescendants()
            .OfType<Control>()
            .Concat(page.GetLogicalDescendants().OfType<Control>())
            .Distinct();

    private static bool IsInteractive(Control page)
    {
        if (!page.IsAttachedToVisualTree())
            return false;

        return TopLevel.GetTopLevel(page) is not Window { IsVisible: false };
    }

    private static bool TryParseRadioTag(string? tag, out string key, out int value)
    {
        key = string.Empty;
        value = 0;
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        int separator = tag.LastIndexOf('/');
        if (separator <= 0 || separator == tag.Length - 1)
            return false;

        key = tag[..separator];
        return int.TryParse(tag[(separator + 1)..], out value);
    }

    private static void SetComboIndex(MyComboBox comboBox, int index)
    {
        if (comboBox.ItemCount > 0)
            comboBox.SelectedIndex = Math.Clamp(index, 0, comboBox.ItemCount - 1);
    }

    /// <summary>
    /// Populate light/dark theme color combos with real <see cref="MyComboBoxItem"/>s
    /// so the closed selection always shows text (string ItemsSource was blank on enter).
    /// </summary>
    private static void EnsureThemeColorItems(MyComboBox comboBox)
    {
        IReadOnlyList<ColorTheme> themes = ThemeOrder;
        string[] names = themes.Select(GetThemeName).ToArray();
        bool needsRebuild = comboBox.ItemCount != names.Length;
        if (!needsRebuild)
        {
            for (int i = 0; i < names.Length; i++)
            {
                string shown = comboBox.Items[i] switch
                {
                    MyComboBoxItem item => item.Content?.ToString() ?? string.Empty,
                    string s => s,
                    _ => comboBox.Items[i]?.ToString() ?? string.Empty
                };
                if (!string.Equals(shown, names[i], StringComparison.Ordinal))
                {
                    needsRebuild = true;
                    break;
                }
            }
        }

        if (!needsRebuild)
            return;

        int preserve = comboBox.SelectedIndex;
        comboBox.ItemsSource = null;
        comboBox.Items.Clear();
        foreach (string name in names)
            comboBox.Items.Add(new MyComboBoxItem { Content = name });
        if (preserve >= 0)
            comboBox.SelectedIndex = Math.Clamp(preserve, 0, names.Length - 1);
    }

    private static ColorTheme GetTheme(int index)
    {
        IReadOnlyList<ColorTheme> themes = ThemeOrder;
        return themes[Math.Clamp(index, 0, themes.Count - 1)];
    }

    private static int GetThemeIndex(ColorTheme theme)
    {
        IReadOnlyList<ColorTheme> themes = ThemeOrder;
        int index = themes.Select(static (item, index) => (item, index))
            .Where(pair => pair.item == theme)
            .Select(pair => pair.index)
            .DefaultIfEmpty(-1)
            .First();
        if (index >= 0)
            return index;
        return themes.Select(static (item, index) => (item, index))
            .First(pair => pair.item == ColorTheme.CatBlue).index;
    }

    private sealed class BindingState
    {
        private readonly Dictionary<MyComboBox, (int Index, string Text)> _comboDefaults;
        private readonly Dictionary<MyCheckBox, bool?> _checkDefaults;
        private readonly Dictionary<MySlider, int> _sliderDefaults;
        private readonly Dictionary<MyTextBox, string> _textDefaults;
        private readonly Dictionary<MyRadioBox, bool> _radioDefaults;

        public BindingState(MyPageRight page, Action<LauncherSettings>? settingsApplied)
        {
            SettingsApplied = settingsApplied;
            TaggedControls = GetControlDescendants(page)
                .Where(static control => !string.IsNullOrWhiteSpace(GetTag(control)))
                .ToArray();
            _comboDefaults = TaggedControls.OfType<MyComboBox>()
                .ToDictionary(static combo => combo, static combo => (combo.SelectedIndex, combo.Text ?? string.Empty));
            _checkDefaults = TaggedControls.OfType<MyCheckBox>()
                .ToDictionary(static check => check, static check => check.Checked);
            _sliderDefaults = TaggedControls.OfType<MySlider>()
                .ToDictionary(static slider => slider, static slider => slider.Value);
            _textDefaults = TaggedControls.OfType<MyTextBox>()
                .ToDictionary(static text => text, static text => text.Text ?? string.Empty);
            _radioDefaults = TaggedControls.OfType<MyRadioBox>()
                .ToDictionary(static radio => radio, static radio => radio.Checked);
        }

        public bool IsApplying { get; set; } = true;

        public Control[] TaggedControls { get; }

        public Action<LauncherSettings>? SettingsApplied { get; }

        public void RestoreControlDefaults()
        {
            foreach ((MyComboBox combo, (int index, string text)) in _comboDefaults)
            {
                if (combo.ItemCount > 0)
                    combo.SelectedIndex = Math.Clamp(index, 0, combo.ItemCount - 1);
                if (combo.IsEditable)
                    combo.Text = text;
            }

            foreach ((MyCheckBox check, bool? value) in _checkDefaults)
                check.Checked = value;
            foreach ((MySlider slider, int value) in _sliderDefaults)
                slider.Value = value;
            foreach ((MyTextBox text, string value) in _textDefaults)
                text.Text = value;
            foreach ((MyRadioBox radio, bool value) in _radioDefaults)
                radio.Checked = value;
        }
    }

    private static int GetComboValue(MyComboBox comboBox)
    {
        if (comboBox.SelectedItem is MyComboBoxItem { Tag: { } tag } &&
            int.TryParse(tag.ToString(), out int taggedValue))
        {
            return taggedValue;
        }

        return comboBox.SelectedIndex;
    }

    private static void SetComboValue(MyComboBox comboBox, int value)
    {
        MyComboBoxItem? taggedItem = comboBox.Items
            .OfType<MyComboBoxItem>()
            .FirstOrDefault(item => item.Tag is not null &&
                                    int.TryParse(item.Tag.ToString(), out int taggedValue) &&
                                    taggedValue == value);
        if (taggedItem is not null)
        {
            comboBox.SelectedItem = taggedItem;
            return;
        }

        SetComboIndex(comboBox, value);
    }
}

internal interface IDeferredSettingsPersistence
{
    bool IsPersistenceDeferred(string settingKey);
}
