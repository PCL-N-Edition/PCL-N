// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Settings;

namespace PCL.Desktop.Features.Launching;

internal enum MinecraftRepairAttempt
{
    None = 0,
    ConventionalApplied = 1,
    ModelApplied = 2
}

/// <summary>Tracks an in-flight AI / dependency repair + relaunch session.</summary>
internal sealed class MinecraftRepairSession(LauncherSettings settings)
{
    public LauncherSettings Settings { get; } = settings;

    public MinecraftRepairTransaction Transaction { get; } = new();

    public MinecraftRepairAttempt Attempt { get; set; }

    public string? LastModelAnalysis { get; set; }

    public string? LastRepairSummary { get; set; }
}
