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
            string path = await ExtractToolAsync(
                    resource,
                    directory,
                    cancellationToken)
                .ConfigureAwait(false);
            _cachedPath = path;
            PortableLog.Debug("Update", $"已释放内置 hpatchz：{path}");
            return path;
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static async Task<string> ExtractToolAsync(
        Stream resource,
        string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, "hpatchz-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (FileStream target = new(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 64,
                             useAsync: true))
            {
                await resource.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            string hash;
            await using (FileStream hashStream = File.OpenRead(temporary))
            {
                hash = Convert.ToHexStringLower(
                    await SHA256.HashDataAsync(hashStream, cancellationToken).ConfigureAwait(false));
            }

            string extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            string path = Path.Combine(directory, $"hpatchz-{hash[..16]}{extension}");
            try
            {
                File.Move(temporary, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another launcher process extracted the same content first.
                File.Delete(temporary);
            }

            temporary = string.Empty;
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            return path;
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Preserve the extraction failure that caused cleanup to run.
                }
            }
        }
    }
}
