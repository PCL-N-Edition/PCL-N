// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using System.Text.Json;
using PCL.Application.Settings;
using PCL.Desktop.Features.Settings.Views;

namespace PCL.Desktop.Features.Launching;

/// <summary>
/// Persists experimental launch-home shortcut pins in launcher settings.
/// </summary>
public static class LaunchShortcutStore
{
    public static bool IsFeatureEnabled()
    {
        LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
        return settings.GetBooleanOption(
            LauncherSettingKeys.ExperimentalLaunchShortcuts,
            LauncherSettingDefaults.GetBoolean(LauncherSettingKeys.ExperimentalLaunchShortcuts.Value));
    }

    public static IReadOnlyList<LaunchShortcutPin> Load()
    {
        LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
        string raw = settings.GetTextOption(LauncherSettingKeys.ExperimentalLaunchShortcutsPins, string.Empty);
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        try
        {
            // Manual parse keeps AOT/trimming happy without a source-generated context.
            using JsonDocument document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            List<LaunchShortcutPin> pins = [];
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (TryReadPin(element, out LaunchShortcutPin? pin) && pin is not null)
                    pins.Add(pin);
            }

            return pins;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static void Save(IEnumerable<LaunchShortcutPin> pins)
    {
        LaunchShortcutPin[] list = pins.Take(12).ToArray();
        LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartArray();
            foreach (LaunchShortcutPin pin in list)
            {
                writer.WriteStartObject();
                writer.WriteString("id", pin.Id);
                writer.WriteString("kind", pin.Kind.ToString());
                writer.WriteString("instanceDirectory", pin.InstanceDirectory);
                writer.WriteString("title", pin.Title);
                writer.WriteString("target", pin.Target);
                if (!string.IsNullOrWhiteSpace(pin.IconPath))
                    writer.WriteString("iconPath", pin.IconPath);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        string json = Encoding.UTF8.GetString(stream.ToArray());
        settings.SetTextOption(LauncherSettingKeys.ExperimentalLaunchShortcutsPins, json);
        LauncherSettingsPageBinder.SaveSettings(settings);
    }

    private static bool TryReadPin(JsonElement element, out LaunchShortcutPin? pin)
    {
        pin = null;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        string instanceDirectory = element.TryGetProperty("instanceDirectory", out JsonElement instanceEl)
            ? instanceEl.GetString() ?? string.Empty
            : string.Empty;
        string title = element.TryGetProperty("title", out JsonElement titleEl)
            ? titleEl.GetString() ?? string.Empty
            : string.Empty;
        string target = element.TryGetProperty("target", out JsonElement targetEl)
            ? targetEl.GetString() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(instanceDirectory) ||
            string.IsNullOrWhiteSpace(title) ||
            string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        string kindText = element.TryGetProperty("kind", out JsonElement kindEl)
            ? kindEl.GetString() ?? string.Empty
            : string.Empty;
        LaunchShortcutKind kind = string.Equals(kindText, nameof(LaunchShortcutKind.Server), StringComparison.OrdinalIgnoreCase)
            ? LaunchShortcutKind.Server
            : LaunchShortcutKind.World;
        string id = element.TryGetProperty("id", out JsonElement idEl)
            ? idEl.GetString() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(id))
            id = LaunchShortcutPin.CreateId(kind, instanceDirectory, target);
        string? iconPath = element.TryGetProperty("iconPath", out JsonElement iconEl)
            ? iconEl.GetString()
            : null;
        pin = new LaunchShortcutPin(id, kind, instanceDirectory, title.Trim(), target.Trim(), iconPath);
        return true;
    }

    public static bool IsPinned(LaunchShortcutKind kind, string instanceDirectory, string target)
    {
        string id = LaunchShortcutPin.CreateId(kind, instanceDirectory, target);
        return Load().Any(pin => string.Equals(pin.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<LaunchShortcutPin> Toggle(LaunchShortcutPin pin)
    {
        List<LaunchShortcutPin> pins = Load().ToList();
        int index = pins.FindIndex(existing =>
            string.Equals(existing.Id, pin.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            pins.RemoveAt(index);
        else
            pins.Add(pin);
        Save(pins);
        return pins;
    }

    public static IReadOnlyList<LaunchShortcutPin> Remove(string id)
    {
        List<LaunchShortcutPin> pins = Load()
            .Where(pin => !string.Equals(pin.Id, id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Save(pins);
        return pins;
    }

}
