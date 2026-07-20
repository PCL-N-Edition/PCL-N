// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Launching;
using PCL.Application.Settings;
using PCL.Desktop.Features.Launching.Views;

namespace PCL.Desktop.Features.Launching;

internal sealed record MinecraftRepairExecutionResult(
    string Message,
    bool IsFailure,
    bool MadeChanges = false);

internal readonly record struct ModDownloadResult(bool Success, bool Changed);

/// <summary>In-flight game process context used by crash / AI repair.</summary>
internal sealed record RunningGameContext(
    LaunchInstanceInfo Instance,
    ILaunchHomeSurface LaunchPage,
    LauncherSettings Settings,
    Task<MinecraftLaunchFaultReport?>? FaultReport = null,
    string? NativesDirectory = null,
    string? WorldName = null,
    string? ServerAddress = null,
    MinecraftRepairSession? RepairSession = null,
    int? JavaMajorVersion = null,
    int? MemoryMegabytes = null,
    string? LoginMethod = null,
    string? LoginServerHost = null,
    string? ProfileUsername = null,
    string? ProfileUuid = null,
    bool UsedExperimentalJvmHost = false,
    string? JavaExecutableName = null,
    string? JavaExecutablePathForRedaction = null,
    int? ClasspathEntryCount = null,
    int? VmArgumentCount = null,
    int? GameArgumentCount = null,
    int? ProcessExitCode = null);
