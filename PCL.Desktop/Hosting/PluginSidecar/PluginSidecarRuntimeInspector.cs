// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Runtime.InteropServices;
using System.Text.Json;

namespace PCL.Desktop.Hosting.PluginSidecar;

internal sealed record PluginSidecarRuntimeCheck(
    bool CanStart,
    bool IsFrameworkDependent,
    string Message);

internal static class PluginSidecarRuntimeInspector
{
    internal const string VariantMarkerFileName = "pcln-sidecar-runtime";

    public static PluginSidecarRuntimeCheck Inspect(string executable) =>
        Inspect(executable, EnumerateRuntimeRoots(executable));

    internal static PluginSidecarRuntimeCheck Inspect(
        string executable,
        IEnumerable<string> runtimeRoots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(runtimeRoots);

        string directory = Path.GetDirectoryName(Path.GetFullPath(executable))
                           ?? AppContext.BaseDirectory;
        string runtimeConfigPath = Path.Combine(
            directory,
            Path.GetFileNameWithoutExtension(executable) + ".runtimeconfig.json");
        if (!File.Exists(runtimeConfigPath))
        {
            return new PluginSidecarRuntimeCheck(
                true,
                false,
                "未找到 sidecar runtimeconfig，将由系统启动器继续诊断。");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(runtimeConfigPath));
            JsonElement runtimeOptions = document.RootElement.GetProperty("runtimeOptions");
            bool frameworkDependent = runtimeOptions.TryGetProperty("framework", out JsonElement framework);
            string? marker = ReadVariantMarker(directory);
            if (string.Equals(marker, "SelfContained", StringComparison.OrdinalIgnoreCase) && frameworkDependent)
            {
                return new PluginSidecarRuntimeCheck(
                    false,
                    true,
                    "插件 sidecar 包标记为 SelfContained，但实际依赖系统 .NET；发布产物已损坏。");
            }
            if (string.Equals(marker, "NoRuntime", StringComparison.OrdinalIgnoreCase) && !frameworkDependent)
            {
                return new PluginSidecarRuntimeCheck(
                    false,
                    false,
                    "插件 sidecar 包标记为 NoRuntime，但实际包含运行时；发布产物已损坏。");
            }

            if (!frameworkDependent)
            {
                return new PluginSidecarRuntimeCheck(
                    true,
                    false,
                    "插件 sidecar 使用随包 CoreCLR 运行时。");
            }

            string frameworkName = framework.TryGetProperty("name", out JsonElement nameElement)
                ? nameElement.GetString() ?? "Microsoft.NETCore.App"
                : "Microsoft.NETCore.App";
            string requiredText = framework.TryGetProperty("version", out JsonElement versionElement)
                ? versionElement.GetString() ?? "10.0.0"
                : "10.0.0";
            if (!TryParseRuntimeVersion(requiredText, out Version? parsedRequiredVersion))
            {
                return new PluginSidecarRuntimeCheck(
                    false,
                    true,
                    $"无法识别插件 sidecar 要求的 .NET 版本：{requiredText}。");
            }
            Version requiredVersion = parsedRequiredVersion!;

            string localHostFxr = Path.Combine(directory, GetHostFxrFileName());
            if (File.Exists(localHostFxr) &&
                !HasCompatibleFramework([directory], frameworkName, requiredVersion))
            {
                return new PluginSidecarRuntimeCheck(
                    false,
                    true,
                    "NoRuntime 插件 sidecar 错误携带了不完整的本地 .NET 宿主，它会屏蔽系统已安装的运行时。" +
                    "请重新安装新版 NoRuntime 或 SelfContained 包。");
            }

            if (HasCompatibleFramework(runtimeRoots, frameworkName, requiredVersion))
            {
                return new PluginSidecarRuntimeCheck(
                    true,
                    true,
                    $"插件 sidecar 使用系统 {frameworkName} {requiredText}。");
            }

            string architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            return new PluginSidecarRuntimeCheck(
                false,
                true,
                $"当前安装的是 NoRuntime 插件 sidecar，但没有找到兼容的 {architecture} " +
                $"{frameworkName} {requiredVersion.Major}.{requiredVersion.Minor} 运行时。" +
                "请安装对应架构的 .NET Runtime，或改用 SelfContained 安装包。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new PluginSidecarRuntimeCheck(
                false,
                false,
                "无法验证插件 sidecar 运行时配置：" + exception.Message);
        }
    }

    private static string? ReadVariantMarker(string directory)
    {
        string path = Path.Combine(directory, VariantMarkerFileName);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    private static bool HasCompatibleFramework(
        IEnumerable<string> runtimeRoots,
        string frameworkName,
        Version requiredVersion)
    {
        foreach (string root in runtimeRoots.Where(static root => !string.IsNullOrWhiteSpace(root)))
        {
            string frameworkDirectory = Path.Combine(root, "shared", frameworkName);
            if (!Directory.Exists(frameworkDirectory))
                continue;

            foreach (string versionDirectory in Directory.EnumerateDirectories(frameworkDirectory))
            {
                if (!TryParseRuntimeVersion(Path.GetFileName(versionDirectory), out Version? parsedInstalledVersion))
                    continue;
                Version installedVersion = parsedInstalledVersion!;
                if (installedVersion.Major == requiredVersion.Major && installedVersion >= requiredVersion)
                    return true;
            }
        }

        return false;
    }

    private static bool TryParseRuntimeVersion(string value, out Version? version) =>
        Version.TryParse(value.Split('-', 2)[0], out version);

    private static string GetHostFxrFileName() =>
        OperatingSystem.IsWindows()
            ? "hostfxr.dll"
            : OperatingSystem.IsMacOS()
                ? "libhostfxr.dylib"
                : "libhostfxr.so";

    private static IEnumerable<string> EnumerateRuntimeRoots(string executable)
    {
        string? executableDirectory = Path.GetDirectoryName(Path.GetFullPath(executable));
        if (!string.IsNullOrWhiteSpace(executableDirectory))
            yield return executableDirectory;

        string architecture = RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();
        string? architectureRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT_" + architecture);
        if (!string.IsNullOrWhiteSpace(architectureRoot))
            yield return architectureRoot;

        string? configuredRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            yield return configuredRoot;

        if (OperatingSystem.IsWindows())
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
                yield return Path.Combine(programFiles, "dotnet");
            yield break;
        }

        yield return "/usr/share/dotnet";
        yield return "/usr/local/share/dotnet";
    }
}
