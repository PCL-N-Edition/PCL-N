// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Application.Instances;

public sealed record InstanceExportRequest
{
    public required string InstanceDirectory { get; init; }

    public required string GameDirectory { get; init; }

    public required string TargetArchivePath { get; init; }

    public IReadOnlyList<string> Rules { get; init; } = [];

    public string PackageName { get; init; } = "Minecraft Modpack";

    public string PackageVersion { get; init; } = "1.0.0";

    public string Summary { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> Dependencies { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool IncludeLauncherFiles { get; init; }

    public bool IncludeLauncherCustom { get; init; }

    public bool IncludeBundleFiles { get; init; }

    public bool ModrinthUploadMode { get; init; }

    public string? LauncherExecutablePath { get; init; }

    public string? LauncherDataDirectory { get; init; }

    public Func<
        IReadOnlyList<InstanceExportFile>,
        CancellationToken,
        Task<IReadOnlyDictionary<string, InstanceExportHostedFile>>>? ResolveHostedFilesAsync { get; init; }
}

public sealed record InstanceExportFile(
    string FullPath,
    string RelativePath,
    long Size,
    string Sha1,
    string Sha512,
    uint CurseForgeFingerprint,
    bool ModrinthOnly);

public sealed record InstanceExportHostedFile(IReadOnlyList<string> DownloadUrls);
