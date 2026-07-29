// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using System.Text.Json.Serialization;
using PCL.Application.Settings;
using PCL.Core.Logging;
using PCL.Desktop.Features.Settings.Views;
using PCL.Desktop.Legal;
using PCL.Desktop.Paths;

namespace PCL.Desktop.Views.FirstRun;

/// <summary>
/// Declares which OOBE pages run for first install / forced full runs vs post-update delta runs.
/// Defaults are embedded; optional <c>pcln-oobe.json</c> next to the host binary overrides them.
/// </summary>
internal static class OobeConfiguration
{
    public const string OverrideFileName = "pcln-oobe.json";

    /// <summary>Settings: last completed OOBE content version.</summary>
    public const string SettingsKeyCompletedVersion = "UiOobeVersion";

    /// <summary>Legacy completion key from earlier first-run builds.</summary>
    public const string SettingsKeyCompletedVersionLegacy = "UiFirstRunWizardVersion";

    public const string ForceArgument = "--oobe";
    /// <summary>Resume OOBE after config-dir restart: Welcome → Online → Finish.</summary>
    public const string ResumeArgument = "--oobe-resume";
    public const string DisableEnvironmentVariable = "PCL_DISABLE_FIRST_RUN";
    public const string SettingsKeyPendingResume = "UiOobePendingResume";
    public const string ResumeMarkerFileName = "oobe-resume.flag";

    private static readonly object Gate = new();
    private static OobeManifest? _resolved;
    private static bool _forceFullFromArgs;
    private static bool _resumeFromArgs;

    /// <summary>Bump when shipping OOBE content that returning users should see (even as a short flow).</summary>
    public const string DefaultContentVersion = "1";

    public static OobeManifest Current
    {
        get
        {
            lock (Gate)
                return _resolved ??= Load();
        }
    }

    public static bool ForceFullFromCommandLine
    {
        get
        {
            lock (Gate)
                return _forceFullFromArgs;
        }
    }

    /// <summary>True when this process was started with <see cref="ResumeArgument"/>.</summary>
    public static bool ResumeFromCommandLine
    {
        get
        {
            lock (Gate)
                return _resumeFromArgs;
        }
    }

    /// <summary>Parse CLI; call once from <see cref="Program.Main"/> before UI starts.</summary>
    public static void ApplyCommandLine(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        bool force = false;
        bool resume = false;
        foreach (string arg in args)
        {
            if (string.Equals(arg, ResumeArgument, StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith(ResumeArgument + "=", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "/oobe-resume", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "-oobe-resume", StringComparison.OrdinalIgnoreCase))
            {
                resume = true;
                continue;
            }

            if (string.Equals(arg, ForceArgument, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "/oobe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "-oobe", StringComparison.OrdinalIgnoreCase))
            {
                force = true;
            }
        }

        lock (Gate)
        {
            _forceFullFromArgs = force && !resume;
            _resumeFromArgs = resume;
        }
    }

    public static void ResetForTests()
    {
        lock (Gate)
        {
            _resolved = null;
            _forceFullFromArgs = false;
            _resumeFromArgs = false;
        }
    }

    /// <summary>Write resume marker under the active data directory (survives if CLI is dropped).</summary>
    public static void WriteResumeMarker()
    {
        try
        {
            string path = Path.Combine(LauncherPathLayout.ResolveDataDirectory(), ResumeMarkerFileName);
            File.WriteAllText(path, "welcome-online");
            LauncherSettingsPageBinder.UpdateSettings(current =>
            {
                current.SetTextOption(SettingsKeyPendingResume, "welcome-online");
                return current;
            });
        }
        catch (Exception ex)
        {
            PortableLog.Warn("OOBE", "写入 OOBE resume 标记失败：" + ex.Message);
        }
    }

    public static void ClearResumeMarker()
    {
        try
        {
            string path = Path.Combine(LauncherPathLayout.ResolveDataDirectory(), ResumeMarkerFileName);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }

        try
        {
            LauncherSettingsPageBinder.UpdateSettings(current =>
            {
                current.SetTextOption(SettingsKeyPendingResume, string.Empty);
                return current;
            });
        }
        catch
        {
            // ignore
        }
    }

    public static bool HasPendingResume(LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (ResumeFromCommandLine)
            return true;

        string pending = settings.GetTextOption(SettingsKeyPendingResume, string.Empty);
        if (!string.IsNullOrWhiteSpace(pending))
            return true;

        try
        {
            string path = Path.Combine(LauncherPathLayout.ResolveDataDirectory(), ResumeMarkerFileName);
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Default post-path-restart steps: welcome, then online configuration.</summary>
    public static IReadOnlyList<OobeStepId> DefaultResumeSteps { get; } =
    [
        OobeStepId.Welcome,
        OobeStepId.Online,
        OobeStepId.Finish
    ];

    public static bool IsDisabledByEnvironment() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DisableEnvironmentVariable));

    public static string ReadCompletedVersion(LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string oobe = settings.GetTextOption(SettingsKeyCompletedVersion, string.Empty);
        if (!string.IsNullOrWhiteSpace(oobe))
            return oobe.Trim();

        string legacy = settings.GetTextOption(SettingsKeyCompletedVersionLegacy, string.Empty);
        return string.IsNullOrWhiteSpace(legacy) ? string.Empty : legacy.Trim();
    }

    /// <summary>
    /// Whether OOBE should open this session.
    /// </summary>
    public static bool ShouldRun(LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (IsDisabledByEnvironment())
            return false;
        if (ForceFullFromCommandLine || HasPendingResume(settings))
            return true;

        string completed = ReadCompletedVersion(settings);
        if (string.IsNullOrEmpty(completed))
            return true; // never finished any OOBE

        return !string.Equals(completed, Current.ContentVersion, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolve run mode: full install/force vs short update flow vs post-path resume.
    /// </summary>
    public static OobeRunPlan CreateRunPlan(LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        OobeManifest manifest = Current;
        string completed = ReadCompletedVersion(settings);

        // After config-dir apply + process restart: connect plugin first (App), then Welcome → Online.
        if (HasPendingResume(settings))
        {
            return new OobeRunPlan(
                OobeRunKind.Resume,
                DefaultResumeSteps,
                manifest.ContentVersion,
                RestartAfterComplete: false,
                Reason: ResumeFromCommandLine ? "command-line --oobe-resume" : "pending-resume-marker");
        }

        if (ForceFullFromCommandLine)
        {
            return new OobeRunPlan(
                OobeRunKind.Full,
                NormalizeSteps(manifest.FullSteps, OobeManifest.DefaultFullSteps),
                manifest.ContentVersion,
                RestartAfterComplete: manifest.RestartAfterFull,
                Reason: "command-line --oobe");
        }

        if (string.IsNullOrEmpty(completed))
        {
            // Already used the launcher (legal accepted / existing prefs) but never finished
            // the new OOBE marker — do NOT force the full install wizard again.
            if (HasPriorLauncherState(settings))
            {
                return new OobeRunPlan(
                    OobeRunKind.Update,
                    NormalizeSteps(manifest.UpdateSteps, OobeManifest.DefaultUpdateSteps),
                    manifest.ContentVersion,
                    RestartAfterComplete: manifest.RestartAfterUpdate,
                    Reason: "existing-profile-no-oobe-marker");
            }

            return new OobeRunPlan(
                OobeRunKind.Full,
                NormalizeSteps(manifest.FullSteps, OobeManifest.DefaultFullSteps),
                manifest.ContentVersion,
                RestartAfterComplete: manifest.RestartAfterFull,
                Reason: "first-run");
        }

        // Completed an older content version → configurable update (delta) flow.
        return new OobeRunPlan(
            OobeRunKind.Update,
            NormalizeSteps(manifest.UpdateSteps, OobeManifest.DefaultUpdateSteps),
            manifest.ContentVersion,
            RestartAfterComplete: manifest.RestartAfterUpdate,
            Reason: $"content {completed} → {manifest.ContentVersion}");
    }

    /// <summary>
    /// True when settings already look like a used install (not a blank first launch).
    /// </summary>
    public static bool HasPriorLauncherState(LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!string.IsNullOrWhiteSpace(
                settings.GetTextOption(EmbeddedLegalDocuments.SettingsKeyAcceptedVersion, string.Empty)))
            return true;

        // Any persisted options beyond empty defaults indicate a returning user.
        if (settings.BooleanOptions.Count > 0 ||
            settings.IntegerOptions.Count > 0 ||
            settings.TextOptions.Count > 0)
            return true;

        try
        {
            string path = LauncherSettingsPageBinder.CreateSettingsPath();
            if (File.Exists(path) && new FileInfo(path).Length > 32)
                return true;
        }
        catch
        {
            // ignore path probe failures
        }

        return false;
    }

    public static void MarkCompleted(string contentVersion)
    {
        if (string.IsNullOrWhiteSpace(contentVersion))
            contentVersion = Current.ContentVersion;

        ClearResumeMarker();
        LauncherSettingsPageBinder.UpdateSettings(current =>
        {
            current.SetTextOption(SettingsKeyCompletedVersion, contentVersion);
            current.SetTextOption(SettingsKeyCompletedVersionLegacy, contentVersion);
            current.SetTextOption(SettingsKeyPendingResume, string.Empty);
            return current;
        });
    }

    private static OobeManifest Load()
    {
        OobeManifest defaults = OobeManifest.CreateDefault();
        string path = Path.Combine(PCL.Desktop.Paths.LauncherPathLayout.GetHostDirectory(), OverrideFileName);
        if (!File.Exists(path))
            return defaults;

        try
        {
            string json = File.ReadAllText(path);
            OobeManifestFile? file = JsonSerializer.Deserialize(
                json,
                OobeConfigurationJsonContext.Default.OobeManifestFile);
            if (file is null)
                return defaults;

            OobeManifest merged = Merge(defaults, file);
            PortableLog.Info(
                "OOBE",
                $"已加载 OOBE 配置 {path}；ContentVersion={merged.ContentVersion}；Full={merged.FullSteps.Count}；Update={merged.UpdateSteps.Count}。");
            return merged;
        }
        catch (Exception ex)
        {
            PortableLog.Warn("OOBE", "读取 pcln-oobe.json 失败，使用内置默认：" + ex.Message);
            return defaults;
        }
    }

    private static OobeManifest Merge(OobeManifest defaults, OobeManifestFile file)
    {
        string version = string.IsNullOrWhiteSpace(file.ContentVersion)
            ? defaults.ContentVersion
            : file.ContentVersion.Trim();

        IReadOnlyList<OobeStepId> full = file.FullSteps is { Count: > 0 }
            ? ParseSteps(file.FullSteps)
            : defaults.FullSteps;
        IReadOnlyList<OobeStepId> update = file.UpdateSteps is { Count: > 0 }
            ? ParseSteps(file.UpdateSteps)
            : defaults.UpdateSteps;

        return new OobeManifest(
            version,
            full,
            update,
            file.RestartAfterFull ?? defaults.RestartAfterFull,
            file.RestartAfterUpdate ?? defaults.RestartAfterUpdate);
    }

    private static IReadOnlyList<OobeStepId> ParseSteps(IReadOnlyList<string> names)
    {
        List<OobeStepId> steps = [];
        foreach (string name in names)
        {
            if (TryParseStep(name, out OobeStepId step) && !steps.Contains(step))
                steps.Add(step);
        }

        // Always end with Finish if any steps and Finish missing — otherwise user cannot complete.
        if (steps.Count > 0 && !steps.Contains(OobeStepId.Finish))
            steps.Add(OobeStepId.Finish);

        // Always start with Welcome for presentation consistency when list is non-empty and Welcome omitted.
        if (steps.Count > 0 && steps[0] != OobeStepId.Welcome && steps.Contains(OobeStepId.Welcome))
        {
            steps.Remove(OobeStepId.Welcome);
            steps.Insert(0, OobeStepId.Welcome);
        }

        return steps.Count > 0 ? steps : OobeManifest.DefaultUpdateSteps;
    }

    private static IReadOnlyList<OobeStepId> NormalizeSteps(
        IReadOnlyList<OobeStepId> steps,
        IReadOnlyList<OobeStepId> fallback)
    {
        if (steps.Count == 0)
            return fallback;
        List<OobeStepId> list = steps.Distinct().ToList();
        if (!list.Contains(OobeStepId.Finish))
            list.Add(OobeStepId.Finish);
        return list;
    }

    internal static bool TryParseStep(string? name, out OobeStepId step)
    {
        step = default;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        switch (name.Trim().ToLowerInvariant())
        {
            case "welcome":
            case "start":
                step = OobeStepId.Welcome;
                return true;
            case "terms":
            case "legal-terms":
            case "tos":
                step = OobeStepId.Terms;
                return true;
            case "privacy":
            case "legal-privacy":
                step = OobeStepId.Privacy;
                return true;
            case "data":
            case "datapaths":
            case "data-paths":
            case "paths":
                step = OobeStepId.DataPaths;
                return true;
            case "online":
            case "account":
            case "cloud":
                step = OobeStepId.Online;
                return true;
            case "telemetry":
            case "privacy-telemetry":
                step = OobeStepId.Telemetry;
                return true;
            case "finish":
            case "done":
            case "complete":
                step = OobeStepId.Finish;
                return true;
            default:
                return false;
        }
    }
}

public enum OobeStepId
{
    Welcome,
    Terms,
    Privacy,
    DataPaths,
    Online,
    Telemetry,
    Finish
}

public enum OobeRunKind
{
    Full,
    Update,
    /// <summary>After path apply + process restart: Welcome → Online → Finish.</summary>
    Resume
}

public sealed record OobeRunPlan(
    OobeRunKind Kind,
    IReadOnlyList<OobeStepId> Steps,
    string ContentVersion,
    bool RestartAfterComplete,
    string Reason);

internal sealed class OobeManifest
{
    public static IReadOnlyList<OobeStepId> DefaultFullSteps { get; } =
    [
        OobeStepId.Welcome,
        OobeStepId.Terms,
        OobeStepId.Privacy,
        OobeStepId.DataPaths,
        OobeStepId.Online,
        OobeStepId.Telemetry,
        OobeStepId.Finish
    ];

    /// <summary>Post-update default: intro + outro only (middle pages opt-in via config).</summary>
    public static IReadOnlyList<OobeStepId> DefaultUpdateSteps { get; } =
    [
        OobeStepId.Welcome,
        OobeStepId.Finish
    ];

    public OobeManifest(
        string contentVersion,
        IReadOnlyList<OobeStepId> fullSteps,
        IReadOnlyList<OobeStepId> updateSteps,
        bool restartAfterFull,
        bool restartAfterUpdate)
    {
        ContentVersion = contentVersion;
        FullSteps = fullSteps;
        UpdateSteps = updateSteps;
        RestartAfterFull = restartAfterFull;
        RestartAfterUpdate = restartAfterUpdate;
    }

    public string ContentVersion { get; }

    public IReadOnlyList<OobeStepId> FullSteps { get; }

    public IReadOnlyList<OobeStepId> UpdateSteps { get; }

    public bool RestartAfterFull { get; }

    public bool RestartAfterUpdate { get; }

    public static OobeManifest CreateDefault() =>
        new(
            OobeConfiguration.DefaultContentVersion,
            DefaultFullSteps,
            DefaultUpdateSteps,
            restartAfterFull: true,
            restartAfterUpdate: false);
}

/// <summary>JSON shape for <c>pcln-oobe.json</c>.</summary>
internal sealed class OobeManifestFile
{
    public string? ContentVersion { get; set; }

    /// <summary>Step names: welcome, terms, privacy, data, online, telemetry, finish.</summary>
    public List<string>? FullSteps { get; set; }

    /// <summary>Steps for users who already completed an older ContentVersion.</summary>
    public List<string>? UpdateSteps { get; set; }

    public bool? RestartAfterFull { get; set; }

    public bool? RestartAfterUpdate { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(OobeManifestFile))]
internal sealed partial class OobeConfigurationJsonContext : JsonSerializerContext;
