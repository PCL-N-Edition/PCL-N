// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Application.Launching;

/// <summary>
/// Describes the JVM hosted launch path. The contract intentionally contains only
/// serializable data because the JVM always runs in an isolated PCL N process.
/// </summary>
public sealed record MinecraftJvmHostRequest
{
    public required string JavaExecutablePath { get; init; }

    public required string WorkingDirectory { get; init; }

    public required string MainClass { get; init; }

    public required string PlayerName { get; init; }

    public required string PlayerUuid { get; init; }

    public string AccessToken { get; init; } = "0";

    public int JavaMajorVersion { get; init; }

    public string[] VmArguments { get; init; } = [];

    public string[] ClasspathEntries { get; init; } = [];

    public string[] GameArguments { get; init; } = [];

    public MinecraftJvmHostIdentityMode IdentityMode { get; init; }

    public string? AuthServer { get; init; }

    public string? AuthServerMetadata { get; init; }

    public string? OfflineSkinSource { get; init; }

    public bool OfflineSkinSlim { get; init; }

    /// <summary>Assigned immediately before the host process is spawned.</summary>
    public string PipeName { get; init; } = string.Empty;
}

public enum MinecraftJvmHostIdentityMode
{
    Official,
    ThirdParty,
    Offline
}
