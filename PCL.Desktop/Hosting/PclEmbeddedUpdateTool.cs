// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Security.Cryptography;
using PCL.Core.Logging;

namespace PCL.Desktop.Hosting;

internal static class PclEmbeddedUpdateTool
{
    private const string ResourceName = "PCL.Desktop.Embedded.hpatchz";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _cachedPath;

    public static async Task<string?> GetHpatchzPathAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_cachedPath) && File.Exists(_cachedPath))
            return _cachedPath;

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cachedPath) && File.Exists(_cachedPath))
                return _cachedPath;

            await using Stream? resource = typeof(PclEmbeddedUpdateTool).Assembly.GetManifestResourceStream(ResourceName);
            if (resource is null)
                return null;

            string directory = Path.Combine(Path.GetTempPath(), "PCL-N", "update-tools");
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, "hpatchz-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (FileStream target = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 64, true))
                await resource.CopyToAsync(target, cancellationToken).ConfigureAwait(false);

            await using FileStream hashStream = File.OpenRead(temporary);
            string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(hashStream, cancellationToken).ConfigureAwait(false));
            string extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            string path = Path.Combine(directory, "hpatchz-" + hash[..16] + extension);
            if (!File.Exists(path))
                File.Move(temporary, path);
            else
                File.Delete(temporary);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            _cachedPath = path;
            PortableLog.Debug("Update", $"已释放内置 hpatchz：{path}");
            return path;
        }
        finally
        {
            Gate.Release();
        }
    }
}
