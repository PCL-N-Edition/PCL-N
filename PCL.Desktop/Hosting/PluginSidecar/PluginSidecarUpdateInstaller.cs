// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCL.Application.Updates;
using PCL.Core.Logging;
using PCL.Desktop.Paths;

namespace PCL.Desktop.Hosting.PluginSidecar;

/// <summary>
/// Applies an independent Sidecar CAS package without replacing the AOT host.
/// Portable/embedded layouts extract under <c>{data}/runtime/sidecar/{hash}/</c>;
/// scatter layouts prefer replacing <c>{installRoot}/sidecar/</c> when writable.
/// </summary>
internal static class PluginSidecarUpdateInstaller
{
    public static async Task<string> InstallFromZipAsync(
        string zipPath,
        PluginSidecarUpdateCheckResult update,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentNullException.ThrowIfNull(update);
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("Sidecar 更新包不存在。", zipPath);

        string? scatterRoot = TryResolveWritableScatterSidecarRoot();
        if (!string.IsNullOrWhiteSpace(scatterRoot))
        {
            string installed = await ExtractToDirectoryAsync(zipPath, scatterRoot, cancellationToken)
                .ConfigureAwait(false);
            await WriteStateAsync(
                    Path.Combine(scatterRoot, ".pcl-sidecar-update.json"),
                    update,
                    installed,
                    layout: "scatter")
                .ConfigureAwait(false);
            await RestartSidecarAsync(cancellationToken).ConfigureAwait(false);
            PortableLog.Info("PluginSidecarUpdate", "已将 Sidecar 更新安装到散包目录：" + installed);
            return installed;
        }

        await using FileStream zipStream = new(
            zipPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        string hash = Convert.ToHexStringLower(
            await SHA256.HashDataAsync(zipStream, cancellationToken).ConfigureAwait(false));
        zipStream.Position = 0;

        string dataRoot = LauncherPathLayout.ResolveDataDirectory();
        string runtimeRoot = Path.Combine(
            dataRoot,
            PclEmbeddedPluginSidecar.RelativeRuntimeFolder.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(runtimeRoot);
        string installDir = Path.Combine(runtimeRoot, hash[..16]);
        string exePath = await ExtractToDirectoryAsync(zipPath, installDir, cancellationToken)
            .ConfigureAwait(false);
        await WriteStateAsync(
                Path.Combine(runtimeRoot, "current.json"),
                update,
                exePath,
                layout: "portable",
                contentHash: hash)
            .ConfigureAwait(false);

        PclEmbeddedPluginSidecar.InvalidateCache();
        Environment.SetEnvironmentVariable("PCL_PLUGIN_SIDECAR_EXE", exePath);
        Environment.SetEnvironmentVariable("PCL_PLUGIN_SIDECAR_DIR", installDir);
        await RestartSidecarAsync(cancellationToken).ConfigureAwait(false);
        PortableLog.Info("PluginSidecarUpdate", "已将 Sidecar 更新安装到数据目录：" + exePath);
        return exePath;
    }

    public static PluginSidecarInstallIdentity ResolveLocalIdentity()
    {
        string runtimeId = PluginSidecarUpdateService.ResolveRuntimeId();
        string variant = "SelfContained";
        string version = "0.0.0";
        string? commit = null;

        string dataRoot = LauncherPathLayout.ResolveDataDirectory();
        string statePath = Path.Combine(
            dataRoot,
            PclEmbeddedPluginSidecar.RelativeRuntimeFolder.Replace('/', Path.DirectorySeparatorChar),
            "current.json");
        if (File.Exists(statePath))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(statePath));
                if (document.RootElement.TryGetProperty("version", out JsonElement versionNode) &&
                    versionNode.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(versionNode.GetString()))
                {
                    version = versionNode.GetString()!.Trim();
                }

                if (document.RootElement.TryGetProperty("commitSha", out JsonElement commitNode) &&
                    commitNode.ValueKind == JsonValueKind.String)
                {
                    commit = commitNode.GetString();
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                PortableLog.Debug("PluginSidecarUpdate", "读取 Sidecar current.json 失败：" + ex.Message);
            }
        }

        return new PluginSidecarInstallIdentity(runtimeId, variant, version, commit);
    }

    private static string? TryResolveWritableScatterSidecarRoot()
    {
        string? installRoot = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(installRoot))
            return null;

        string sidecar = Path.Combine(installRoot, "sidecar");
        try
        {
            Directory.CreateDirectory(sidecar);
            string probe = Path.Combine(sidecar, ".write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return sidecar;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            PortableLog.Debug("PluginSidecarUpdate", "散包 sidecar 目录不可写，回退数据目录：" + ex.Message);
            return null;
        }
    }

    private static async Task<string> ExtractToDirectoryAsync(
        string zipPath,
        string installDir,
        CancellationToken cancellationToken)
    {
        string tempDir = installDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                         ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
            Directory.CreateDirectory(tempDir);

            ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);
            string exeName = PluginSidecarPaths.ExecutableFileName;
            if (!Directory.EnumerateFiles(tempDir, exeName, SearchOption.AllDirectories).Any())
                throw new InvalidDataException("Sidecar 更新包缺少可执行文件：" + exeName);

            if (Directory.Exists(installDir))
                Directory.Delete(installDir, recursive: true);
            Directory.Move(tempDir, installDir);
            string relocated = Directory.EnumerateFiles(installDir, exeName, SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? throw new InvalidDataException("解压后找不到 Sidecar 可执行文件。");
            await File.WriteAllTextAsync(
                    Path.Combine(installDir, ".extracted"),
                    DateTimeOffset.UtcNow.ToString("O"),
                    cancellationToken)
                .ConfigureAwait(false);
            return relocated;
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    private static async Task WriteStateAsync(
        string path,
        PluginSidecarUpdateCheckResult update,
        string executablePath,
        string layout,
        string? contentHash = null)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        // Build via JsonObject to keep Native AOT free of reflection serializers.
        JsonObject state = new()
        {
            ["version"] = update.LatestVersion,
            ["tag"] = update.ReleaseName,
            ["commitSha"] = update.RemoteCommitSha,
            ["channel"] = update.Channel,
            ["layout"] = layout,
            ["executablePath"] = executablePath,
            ["contentHash"] = contentHash,
            ["installedAt"] = DateTimeOffset.UtcNow.ToString("O")
        };
        await File.WriteAllTextAsync(path, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))
            .ConfigureAwait(false);
    }

    private static async Task RestartSidecarAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PluginSidecarSupervisor.Instance.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PortableLog.Warn("PluginSidecarUpdate", "停止旧 Sidecar 失败：" + ex.Message);
        }

        PclEmbeddedPluginSidecar.InvalidateCache();
        bool started = await PluginSidecarSupervisor.Instance.TryStartAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!started)
            PortableLog.Warn("PluginSidecarUpdate", "Sidecar 更新后未能立即启动；将在下次宿主初始化时重试。");
    }
}
