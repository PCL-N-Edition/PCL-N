using System.Collections.Frozen;
using System.Globalization;

namespace Nexa.Services.Settings;

public enum SettingsApplyTiming { Immediate, NextLaunch, NextTask, Restart }
public enum SettingsValueKind { Boolean, Number, Text, Enum, Path }
public enum SettingsLayer { Builtin, Global, Instance, Profile, Temporary }
public enum SettingsOverrideMode { Inherit, Auto, Custom }
public enum SettingsCapabilityAvailability { Available, NotImplemented, PlatformUnsupported, DependencyMissing, TemporarilyUnavailable }

public sealed record SettingsPolicyDefinition(string Key, SettingsValueKind Kind, string DefaultValue,
    bool InstanceOverride, bool SupportsAuto, string? LegacyKey, SettingsApplyTiming Timing,
    string Unit = "", long? Minimum = null, long? Maximum = null, string Choices = "", bool Exportable = true)
{
    public string Owner { get; init; } = "Nexa.Services.Settings";
    public string? Validate(SettingsOverride value)
    {
        if (value.Mode == SettingsOverrideMode.Inherit) return value.Value is null ? null : "Inherited values cannot carry a payload.";
        if (value.Mode == SettingsOverrideMode.Auto) return SupportsAuto && value.Value is null ? null : "Auto is not supported or carries a payload.";
        if (value.Mode != SettingsOverrideMode.Custom || value.Value is null) return "A custom value is required.";
        string raw = value.Value;
        return Kind switch
        {
            SettingsValueKind.Boolean when raw is not ("true" or "false") => "Expected true or false.",
            SettingsValueKind.Number when !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                || Minimum is { } min && number < min || Maximum is { } max && number > max => "Value is outside the declared range.",
            SettingsValueKind.Enum when !Choices.Split('|').Contains(raw, StringComparer.Ordinal) => "Unknown choice.",
            SettingsValueKind.Path when raw.IndexOfAny(['\0', '\r', '\n']) >= 0 || !Path.IsPathFullyQualified(raw) => "Expected a fully qualified path.",
            _ when raw.Contains('\0') => "NUL is not allowed.",
            _ => null,
        };
    }
}

public static class SettingsPolicySchema
{
    public const string StorageKey = "NexaSettingsLayers";
    public const string EmptyDocument = "{\"version\":1,\"global\":{},\"instances\":{}}";
    public static IReadOnlyList<SettingsPolicyDefinition> Definitions { get; } = Array.AsReadOnly<SettingsPolicyDefinition>([
        new("general.language", SettingsValueKind.Text, "auto", false, false, "UiLanguage", SettingsApplyTiming.Restart),
        new("general.region", SettingsValueKind.Text, "auto", false, false, "UiFormatCulture", SettingsApplyTiming.Restart),
        new("appearance.animations-disabled", SettingsValueKind.Boolean, "false", false, false, "SystemDisableUiAnimations", SettingsApplyTiming.Immediate),
        new("appearance.animation-fps", SettingsValueKind.Number, "59", false, false, "UiAniFPS", SettingsApplyTiming.Immediate, "fps", 1, 240),
        new("appearance.lock-window", SettingsValueKind.Boolean, "false", false, false, "UiLockWindowSize", SettingsApplyTiming.Immediate),
        new("appearance.low-power", SettingsValueKind.Boolean, "false", false, false, "UiUltraLowPowerMode", SettingsApplyTiming.Immediate),
        new("java.runtime", SettingsValueKind.Path, "", true, true, null, SettingsApplyTiming.NextLaunch, Exportable: false),
        new("java.auto-install", SettingsValueKind.Boolean, "false", true, false, null, SettingsApplyTiming.NextLaunch),
        new("java.vendor", SettingsValueKind.Text, "", true, false, null, SettingsApplyTiming.NextLaunch),
        new("java.compatibility", SettingsValueKind.Boolean, "true", true, false, null, SettingsApplyTiming.NextLaunch),
        new("recovery.keep-history", SettingsValueKind.Boolean, "false", true, false, null, SettingsApplyTiming.NextTask, Exportable: false),
        new("game.memory", SettingsValueKind.Number, "2048", true, true, null, SettingsApplyTiming.NextLaunch, "MiB", 256, 1048576),
        new("game.window-mode", SettingsValueKind.Enum, "windowed", true, false, null, SettingsApplyTiming.NextLaunch, Choices: "windowed|fullscreen"),
        new("game.width", SettingsValueKind.Number, "854", true, false, "LaunchArgumentWindowWidth", SettingsApplyTiming.NextLaunch, "px", 1, 32768),
        new("game.height", SettingsValueKind.Number, "480", true, false, "LaunchArgumentWindowHeight", SettingsApplyTiming.NextLaunch, "px", 1, 32768),
        new("game.title", SettingsValueKind.Text, "", true, false, "LaunchArgumentTitle", SettingsApplyTiming.NextLaunch),
        new("game.jvm", SettingsValueKind.Text, LauncherDefaults.TextDefaults["LaunchAdvanceJvm"], true, false, "LaunchAdvanceJvm", SettingsApplyTiming.NextLaunch, Exportable: false),
        new("game.arguments", SettingsValueKind.Text, "", true, false, "LaunchAdvanceGame", SettingsApplyTiming.NextLaunch, Exportable: false),
        new("game.wrapper", SettingsValueKind.Text, "", true, false, "LaunchWrapperCommand", SettingsApplyTiming.NextLaunch, Exportable: false),
        new("game.pre-launch", SettingsValueKind.Text, "", true, false, "LaunchAdvanceRun", SettingsApplyTiming.NextLaunch, Exportable: false),
        new("game.auto-repair", SettingsValueKind.Boolean, "true", true, false, "LaunchAutoRepairGame", SettingsApplyTiming.NextLaunch),
        new("game.server", SettingsValueKind.Text, "", true, false, null, SettingsApplyTiming.NextLaunch),
        new("network.proxy-mode", SettingsValueKind.Enum, "1", false, false, "SystemHttpProxyType", SettingsApplyTiming.NextTask, Choices: "0|1|2"),
        new("network.proxy-address", SettingsValueKind.Text, "", false, false, "SystemHttpProxy", SettingsApplyTiming.NextTask, Exportable: false),
        new("network.proxy-user", SettingsValueKind.Text, "", false, false, "SystemHttpProxyCustomUsername", SettingsApplyTiming.NextTask, Exportable: false),
        new("network.proxy-password", SettingsValueKind.Text, "", false, false, "SystemHttpProxyCustomPassword", SettingsApplyTiming.NextTask, Exportable: false),
        new("network.doh", SettingsValueKind.Boolean, "true", false, false, "SystemNetEnableDoH", SettingsApplyTiming.NextTask),
        new("install.inherit-vanilla", SettingsValueKind.Boolean, "false", false, false, null, SettingsApplyTiming.NextTask),
        new("diagnostics.telemetry", SettingsValueKind.Boolean, "false", false, false, "TelemetryExperienceProgram", SettingsApplyTiming.Immediate),
        new("updates.channel", SettingsValueKind.Enum, "alpha", false, false, null, SettingsApplyTiming.NextTask, Choices: "stable|alpha|beta|ci"),
        new("developer.enabled", SettingsValueKind.Boolean, "false", false, false, null, SettingsApplyTiming.Immediate),
    ]);
    public static FrozenDictionary<string, SettingsPolicyDefinition> ByKey { get; } = Definitions.ToFrozenDictionary(item => item.Key, StringComparer.Ordinal);
}

public sealed record SettingsOverride(SettingsOverrideMode Mode, string? Value = null);
public sealed record SettingsEffectiveValue(string Key, SettingsOverride Value, SettingsLayer Source, SettingsApplyTiming Timing, string? ValidationError)
{
    public IReadOnlyList<string> ArgumentRows
    {
        get
        {
            if (Key is not ("game.jvm" or "game.arguments")) return [];
            List<string> rows = [];
            var token = new System.Text.StringBuilder();
            bool quoted = false;
            foreach (char character in Value.Value ?? "")
            {
                if (character == '"') quoted = !quoted;
                if (char.IsWhiteSpace(character) && !quoted)
                {
                    if (token.Length > 0) { rows.Add(token.ToString()); token.Clear(); }
                }
                else token.Append(character);
            }
            if (token.Length > 0) rows.Add(token.ToString());
            return rows.AsReadOnly();
        }
    }
}
public sealed record SettingsEffectiveSnapshot(long Revision, IReadOnlyList<SettingsEffectiveValue> Values);
public sealed record SettingsMutation(string Key, SettingsLayer Layer, SettingsOverride Value, string? InstanceId = null);
public sealed record SettingsEffectiveQuery(string? InstanceId = null);
public sealed record SettingsImportQuery(string Document, string? InstanceId = null);
public sealed record SettingsImportPreview(long Revision, IReadOnlyList<SettingsMutation> Changes, IReadOnlyList<string> Errors);
public sealed record SettingsImportCommand(string Document, long ExpectedRevision, string? InstanceId = null);
public sealed record SettingsExportQuery(string? InstanceId = null);
public sealed record SettingsBatchCommand(IReadOnlyList<SettingsMutation> Changes, long ExpectedRevision);
public sealed record SettingsPreviewQuery(IReadOnlyList<SettingsMutation> Changes, string? InstanceId = null);
