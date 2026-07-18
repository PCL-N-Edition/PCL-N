// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using System.Reflection;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using PCL.Application.Settings;

namespace PCL.Desktop.Localization;

/// <summary>
/// UI language resources. Simplified Chinese is the fallback catalog; Traditional Chinese and English
/// overlay it when selected. Lookup order is current language → Simplified Chinese → key name.
/// </summary>
public static class AvaloniaLocalizationManager
{
    public const string Auto = "auto";
    public const string FollowLanguage = "follow-language";
    public const string ChineseLanguage = "zh-CN";
    public const string TraditionalChineseLanguage = "zh-TW";
    public const string EnglishLanguage = "en-US";

    private const string ChineseResourceName = "PCL.Desktop.Localization.zh-CN.xaml";
    private const string TraditionalChineseResourceName = "PCL.Desktop.Localization.zh-TW.xaml";
    private const string EnglishResourceName = "PCL.Desktop.Localization.en-US.xaml";

    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentCulture;
    private static readonly CultureInfo SystemUiCulture = CultureInfo.CurrentUICulture;

    private static readonly object Gate = new();
    private static Dictionary<string, string>? _zhCnMap;
    private static Dictionary<string, string>? _zhTwMap;
    private static Dictionary<string, string>? _enUsMap;
    private static ResourceDictionary? _mergedLanguageResources;

    public static string CurrentLanguageCode { get; private set; } = ChineseLanguage;

    public static CultureInfo CurrentFormatCulture { get; private set; } = CultureInfo.CurrentCulture;

    public static event EventHandler? LanguageChanged;

    public static void InitializeFromSettings(LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Apply(
            settings.GetTextOption("UiLanguage", Auto),
            settings.GetTextOption("UiFormatCulture", Auto));
    }

    public static void Apply(string? languageCode, string? formatCultureCode)
    {
        string resolvedLanguage = ResolveLanguage(languageCode);
        CultureInfo uiCulture = CultureInfo.GetCultureInfo(resolvedLanguage);
        CultureInfo formatCulture = ResolveFormatCulture(formatCultureCode, uiCulture);

        CultureInfo.CurrentUICulture = uiCulture;
        CultureInfo.DefaultThreadCurrentUICulture = uiCulture;
        Thread.CurrentThread.CurrentUICulture = uiCulture;
        CultureInfo.CurrentCulture = formatCulture;
        CultureInfo.DefaultThreadCurrentCulture = formatCulture;
        Thread.CurrentThread.CurrentCulture = formatCulture;

        if (Avalonia.Application.Current is { } application)
            ApplyResources(application, resolvedLanguage);

        bool changed = !string.Equals(CurrentLanguageCode, resolvedLanguage, StringComparison.OrdinalIgnoreCase) ||
                       !string.Equals(CurrentFormatCulture.Name, formatCulture.Name, StringComparison.OrdinalIgnoreCase);
        CurrentLanguageCode = resolvedLanguage;
        CurrentFormatCulture = formatCulture;
        if (changed)
            LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Resolve UI text: current language → Chinese → <paramref name="key"/>.
    /// The <paramref name="fallback"/> parameter is ignored (kept for call-site compatibility).
    /// </summary>
    public static string GetText(string key, string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;

        EnsureMapsLoaded();

        if (string.Equals(CurrentLanguageCode, TraditionalChineseLanguage, StringComparison.OrdinalIgnoreCase) &&
            _zhTwMap is not null &&
            _zhTwMap.TryGetValue(key, out string? zhTw) &&
            !string.IsNullOrEmpty(zhTw))
        {
            return zhTw;
        }

        if (string.Equals(CurrentLanguageCode, EnglishLanguage, StringComparison.OrdinalIgnoreCase) &&
            _enUsMap is not null &&
            _enUsMap.TryGetValue(key, out string? en) &&
            !string.IsNullOrEmpty(en))
        {
            return en;
        }

        // 2) Chinese fallback
        if (_zhCnMap is not null &&
            _zhCnMap.TryGetValue(key, out string? zh) &&
            !string.IsNullOrEmpty(zh))
        {
            return zh;
        }

        // 3) Application resources (DynamicResource already merged) as last live lookup
        if (Avalonia.Application.Current?.TryGetResource(key, null, out object? value) == true &&
            value is string live &&
            !string.IsNullOrEmpty(live))
        {
            return live;
        }

        // 4) Missing Chinese (and English) → show the key
        _ = fallback;
        return key;
    }

    public static string GetTextOrFallback(string keyOrFallback)
    {
        if (string.IsNullOrWhiteSpace(keyOrFallback))
            return string.Empty;

        EnsureMapsLoaded();
        if (ContainsResourceKey(keyOrFallback))
            return GetText(keyOrFallback);

        string? resourceKey = _zhCnMap?
            .FirstOrDefault(pair => string.Equals(pair.Value, keyOrFallback, StringComparison.Ordinal))
            .Key;
        return string.IsNullOrWhiteSpace(resourceKey) ? keyOrFallback : GetText(resourceKey);
    }

    private static bool ContainsResourceKey(string key) =>
        _zhCnMap?.ContainsKey(key) == true || _zhTwMap?.ContainsKey(key) == true || _enUsMap?.ContainsKey(key) == true;
    internal static string ResolveLanguageForCulture(string? languageCode, CultureInfo systemUiCulture)
    {
        if (!string.IsNullOrWhiteSpace(languageCode) &&
            !string.Equals(languageCode, Auto, StringComparison.OrdinalIgnoreCase))
        {
            if (languageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                return EnglishLanguage;
            return IsTraditionalChinese(languageCode) ? TraditionalChineseLanguage : ChineseLanguage;
        }

        if (systemUiCulture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase))
            return EnglishLanguage;
        return IsTraditionalChinese(systemUiCulture.Name) ||
               systemUiCulture.IetfLanguageTag.Contains("Hant", StringComparison.OrdinalIgnoreCase)
            ? TraditionalChineseLanguage
            : ChineseLanguage;
    }

    private static string ResolveLanguage(string? languageCode) =>
        ResolveLanguageForCulture(languageCode, SystemUiCulture);

    private static bool IsTraditionalChinese(string languageCode) =>
        languageCode.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
        languageCode.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ||
        languageCode.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase) ||
        languageCode.Contains("Hant", StringComparison.OrdinalIgnoreCase);

    private static CultureInfo ResolveFormatCulture(string? formatCultureCode, CultureInfo uiCulture)
    {
        if (string.IsNullOrWhiteSpace(formatCultureCode) ||
            string.Equals(formatCultureCode, Auto, StringComparison.OrdinalIgnoreCase))
        {
            return SystemCulture;
        }

        if (string.Equals(formatCultureCode, FollowLanguage, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(formatCultureCode, "ui-language", StringComparison.OrdinalIgnoreCase))
        {
            return uiCulture;
        }

        try
        {
            return CultureInfo.GetCultureInfo(formatCultureCode);
        }
        catch (CultureNotFoundException)
        {
            return SystemCulture;
        }
    }

    private static void ApplyResources(Avalonia.Application application, string languageCode)
    {
        EnsureMapsLoaded();

        if (_mergedLanguageResources is not null)
        {
            application.Resources.MergedDictionaries.Remove(_mergedLanguageResources);
            _mergedLanguageResources = null;
        }

        // Always start from Chinese so DynamicResource has zh-CN defaults.
        ResourceDictionary merged = new();
        if (_zhCnMap is not null)
        {
            foreach ((string key, string value) in _zhCnMap)
                merged[key] = value;
        }

        if (string.Equals(languageCode, TraditionalChineseLanguage, StringComparison.OrdinalIgnoreCase) &&
            _zhTwMap is not null)
        {
            foreach ((string key, string value) in _zhTwMap)
                merged[key] = value;
        }
        else if (string.Equals(languageCode, EnglishLanguage, StringComparison.OrdinalIgnoreCase) &&
                 _enUsMap is not null)
        {
            foreach ((string key, string value) in _enUsMap)
                merged[key] = value;
        }

        _mergedLanguageResources = merged;
        application.Resources.MergedDictionaries.Add(merged);
    }

    private static void EnsureMapsLoaded()
    {
        if (_zhCnMap is not null && _zhTwMap is not null && _enUsMap is not null)
            return;

        lock (Gate)
        {
            _zhCnMap ??= LoadStringMap(ChineseResourceName);
            _zhTwMap ??= LoadStringMap(TraditionalChineseResourceName);
            _enUsMap ??= LoadStringMap(EnglishResourceName);
        }
    }

    private static Dictionary<string, string> LoadStringMap(string resourceName)
    {
        Assembly assembly = typeof(AvaloniaLocalizationManager).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        XDocument document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        foreach (XElement element in document.Root?.Elements() ?? [])
        {
            XAttribute? key = element.Attribute(xaml + "Key");
            if (key is null || string.IsNullOrWhiteSpace(key.Value))
                continue;
            map[key.Value] = element.Value;
        }

        return map;
    }
}
