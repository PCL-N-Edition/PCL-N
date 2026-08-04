// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Desktop.Diagnostics;

/// <summary>Severity of a single compatibility probe result.</summary>
public enum CompatibilityStatus
{
    /// <summary>Dependency is healthy.</summary>
    Ok = 0,
    /// <summary>Works via fallback or with reduced quality; user may toggle mitigations.</summary>
    Degraded = 1,
    /// <summary>Unavailable; optional features impacted.</summary>
    Unavailable = 2,
    /// <summary>Required dependency failed with no usable alternative.</summary>
    Fatal = 3
}

/// <summary>One probe item shown on OOBE / Settings → Compatibility.</summary>
public sealed record CompatibilityCheckItem(
    string Id,
    string Title,
    string Detail,
    CompatibilityStatus Status,
    bool IsRequired,
    /// <summary>Settings key the user may flip as a mitigation (null = none).</summary>
    string? MitigationSettingKey = null,
    string? MitigationLabel = null);

/// <summary>Full self-check snapshot for the current process.</summary>
public sealed record CompatibilityReport(
    DateTimeOffset Timestamp,
    IReadOnlyList<CompatibilityCheckItem> Items)
{
    public bool HasFatal => Items.Any(static i => i.Status == CompatibilityStatus.Fatal);

    /// <summary>True when the launcher can continue (no fatal required failures).</summary>
    public bool CanRun => !HasFatal;

    public int OkCount => Items.Count(static i => i.Status == CompatibilityStatus.Ok);

    public int IssueCount => Items.Count(static i => i.Status is not CompatibilityStatus.Ok);
}
