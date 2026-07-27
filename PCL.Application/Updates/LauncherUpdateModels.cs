// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Application.Updates;

/// <summary>Identity stamped into the launcher build and used to select an update asset.</summary>
public sealed record LauncherBuildIdentity(
    string Version,
    string RuntimeId,
    string RuntimeVariant,
    string Configuration)
{
    public string NormalizedRuntimeVariant => NormalizeRuntimeVariant(RuntimeVariant);

    public static string NormalizeRuntimeVariant(string? value)
    {
        string text = value?.Trim() ?? string.Empty;
        // Host-only packages: SelfContained | NoRuntime (legacy *_{With,No}Plugin collapse to runtime).
        return text.StartsWith("NoRuntime", StringComparison.OrdinalIgnoreCase)
            ? "NoRuntime"
            : "SelfContained";
    }

    public static string NormalizeConfiguration(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "release" or "stable" or "final" => "Release",
        "beta" or "preview" or "rc" => "Beta",
        _ => "CI"
    };
}

/// <summary>A single HDiffPatch edge. Multi-hop updates contain more than one step.</summary>
public sealed record LauncherUpdatePatchStep(
    string FromVersion,
    string TargetVersion,
    string DownloadUrl,
    string Sha256,
    long Size,
    string FromSha256,
    long FromSize,
    string TargetSha256,
    long TargetSize);

/// <summary>Download and verification plan for a launcher update.</summary>
public sealed record LauncherUpdatePackage(
    string TargetVersion,
    string TargetTag,
    string FullPackageUrl,
    string TargetAssetName,
    string TargetBinaryName,
    string? TargetSha256,
    long? TargetSize,
    IReadOnlyList<LauncherUpdatePatchStep> PatchSteps,
    string RuntimeId,
    string RuntimeVariant,
    string Configuration,
    string? FullPackageSignatureUrl = null,
    string? TargetBinarySignatureUrl = null)
{
    public bool UsesPatch => PatchSteps.Count > 0;
}
