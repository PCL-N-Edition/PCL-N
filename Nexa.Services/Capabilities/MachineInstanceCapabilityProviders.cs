using System.Runtime.InteropServices;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Downloads;
using Nexa.Services.Minecraft.Java;

namespace Nexa.Services.Capabilities;

/// <summary>
/// Storage, filesystem, power and the Minecraft environment namespaces (java.*, loader.*,
/// minecraft.files.*). The environment facts reuse the production locators and the shared
/// file verifier — capabilities never probe with second-grade logic.
/// </summary>
public static class MachineInstanceCatalog
{
    public const string JavaProviderId = "nexa.java";
    public const string MinecraftProviderId = "nexa.minecraft";

    // power.* facts beyond the display slice's definitions live with their provider.
    public static readonly CapabilityDefinition<long> InstanceVolumeFree =
        MachineEnvironmentCatalog.InstanceVolumeFreeBytes;
    public static readonly CapabilityDefinition<bool> InstancePathExists =
        MachineEnvironmentCatalog.InstancePathExists;
    public static readonly CapabilityDefinition<bool> InstancePathWritable =
        MachineEnvironmentCatalog.InstancePathWritable;

    // java.*
    public static readonly CapabilityDefinition<bool> JavaInstalled = new("java.installed", "已安装 Java 运行时", "Java", JavaProviderId);
    public static readonly CapabilityDefinition<int> JavaRuntimeCount = new("java.runtime.count", "可用 Java 运行时数量", "Java", JavaProviderId,
        CapabilityKind.Metric, CapabilityStability.Session);
    public static readonly CapabilityDefinition<string> JavaRuntimePath = new("java.runtime.path", "首选 Java 路径", "Java", JavaProviderId);
    public static readonly CapabilityDefinition<string> JavaRuntimeVersion = new("java.runtime.version", "首选 Java 版本", "Java", JavaProviderId);
    public static readonly CapabilityDefinition<int> JavaRuntimeMajor = new("java.runtime.major", "首选 Java 主版本", "Java", JavaProviderId,
        CapabilityKind.Metric, CapabilityStability.Session);

    // minecraft.files.*
    public static readonly CapabilityDefinition<int> MinecraftFilesRequired = new("minecraft.files.required", "实例必需文件数", "游戏文件", MinecraftProviderId,
        CapabilityKind.Metric, CapabilityStability.Dynamic);
    public static readonly CapabilityDefinition<int> MinecraftFilesMissing = new("minecraft.files.missing", "缺失/损坏文件数", "游戏文件", MinecraftProviderId,
        CapabilityKind.Metric, CapabilityStability.Dynamic);

    public static CapabilityRegistry MergeInto(CapabilityRegistry registry) => new(
        [.. registry.Definitions,
            JavaInstalled, JavaRuntimeCount, JavaRuntimePath, JavaRuntimeVersion, JavaRuntimeMajor,
            MinecraftFilesRequired, MinecraftFilesMissing]);

    /// <summary>Scope facts for one instance path (storage volume + writability).</summary>
    public static IReadOnlyList<ICapability> CollectPathScope(string instanceDirectory, DateTimeOffset timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceDirectory);
        const string source = "Directory / DriveInfo";
        string full = Path.GetFullPath(instanceDirectory);
        bool exists = Directory.Exists(full);
        List<ICapability> facts =
        [
            InstancePathExists.Observe(exists, timestamp, source),
        ];
        if (exists)
        {
            bool writable;
            try
            {
                string probe = Path.Combine(full, ".nexa-write-probe");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                writable = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                writable = false;
            }

            facts.Add(InstancePathWritable.Observe(writable, timestamp, source));
            DriveInfo? drive = FindDrive(full);
            facts.Add(drive is { } info && info.IsReady
                ? InstanceVolumeFree.Observe(info.AvailableFreeSpace, timestamp, source)
                : InstanceVolumeFree.Unavailable(CapabilityAvailability.Unknown, timestamp, "卷信息不可读"));
        }
        else
        {
            facts.Add(InstancePathWritable.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "路径不存在"));
            facts.Add(InstanceVolumeFree.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "路径不存在"));
        }

        return Array.AsReadOnly<ICapability>([.. facts]);
    }

    private static DriveInfo? FindDrive(string path)
    {
        DirectoryInfo? directory = new(path);
        while (directory is not null && directory.Parent is not null)
        {
            directory = directory.Parent;
        }

        return directory is null ? null : new DriveInfo(directory.FullName);
    }

    /// <summary>Java facts from the production locator — the same scan the launch path uses.</summary>
    public static async ValueTask<IReadOnlyList<ICapability>> CollectJavaAsync(
        IJavaRuntimeLocator locator, DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(locator);
        IReadOnlyList<JavaRuntimeCandidate> runtimes;
        try
        {
            runtimes = await locator.FindAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            return Array.AsReadOnly(new ICapability[]
            {
                JavaInstalled.Unavailable(CapabilityAvailability.TemporarilyUnavailable, timestamp, exception.Message),
            });
        }

        const string source = "LocalJavaRuntimeLocator";
        JavaRuntimeCandidate? preferred = runtimes.FirstOrDefault(static candidate => candidate.IsAvailable && candidate.IsEnabled);
        return Array.AsReadOnly(new ICapability[]
        {
            JavaInstalled.Observe(runtimes.Any(static candidate => candidate.IsAvailable && candidate.IsEnabled), timestamp, source),
            JavaRuntimeCount.Observe(runtimes.Count(static candidate => candidate.IsAvailable && candidate.IsEnabled), timestamp, source),
            preferred is { } runtime
                ? JavaRuntimePath.Observe(runtime.Installation.JavaExecutablePath, timestamp, source)
                : JavaRuntimePath.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "没有可用运行时"),
            preferred is { } found
                ? JavaRuntimeVersion.Observe(found.Installation.Version.ToString(), timestamp, source)
                : JavaRuntimeVersion.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "没有可用运行时"),
            preferred is { } selected
                ? JavaRuntimeMajor.Observe(selected.Installation.Version.Major, timestamp, source)
                : JavaRuntimeMajor.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "没有可用运行时"),
        });
    }

    /// <summary>minecraft.files facts: verify the plan the launch pipeline would demand.</summary>
    public static async ValueTask<IReadOnlyList<ICapability>> CollectMinecraftFilesAsync(
        IReadOnlyList<MinecraftExpectedFile> expected, DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        int missing = 0;
        foreach (MinecraftExpectedFile file in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await MinecraftFileVerifier.VerifyAsync(file, cancellationToken).ConfigureAwait(false))
            {
                missing++;
            }
        }

        const string source = "MinecraftFileVerifier";
        return Array.AsReadOnly(new ICapability[]
        {
            MinecraftFilesRequired.Observe(expected.Count, timestamp, source),
            MinecraftFilesMissing.Observe(missing, timestamp, source),
        });
    }
}

/// <summary>Filesystem-shape facts from platform semantics.</summary>
internal sealed class FilesystemCapabilityProvider : IMachineCapabilityProvider
{
    public string Id => MachineEnvironmentCatalog.FilesystemProviderId;

    public ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ICapability[] facts =
        [
            MachineEnvironmentCatalog.FilesystemCaseSensitive.Observe(
                !OperatingSystem.IsWindows(), timestamp, ".NET platform semantics"),
            MachineEnvironmentCatalog.FilesystemSymlink.Observe(SupportsSymlinks, timestamp, ".NET FileSystem/OS semantics"),
            // Reflink/clone (APFS clonefile, Btrfs, ReFS) needs per-filesystem interop; a
            // wrong "yes" would silently duplicate data, so the action stays unavailable.
            MachineEnvironmentCatalog.FilesystemReflink.Unavailable(
                CapabilityAvailability.NotImplemented, timestamp, "文件克隆检测尚未接入"),
        ];
        return ValueTask.FromResult<IReadOnlyList<ICapability>>(facts);
    }

    private static readonly bool SupportsSymlinks = OperatingSystem.IsWindows()
        ? Environment.OSVersion.Version.Build >= 14972
        : true;
}

/// <summary>Instance-path scope: existence, writability, and the volume's free space.</summary>
internal sealed class StorageCapabilityProvider(string? instanceDirectory) : IMachineCapabilityProvider
{
    public string Id => MachineEnvironmentCatalog.StorageProviderId;

    public ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(instanceDirectory))
        {
            return ValueTask.FromResult<IReadOnlyList<ICapability>>(
            [
                MachineEnvironmentCatalog.InstancePathExists.Unavailable(
                    CapabilityAvailability.TemporarilyUnavailable, timestamp, "尚未确定 Minecraft 目录"),
                MachineEnvironmentCatalog.InstancePathWritable.Unavailable(
                    CapabilityAvailability.TemporarilyUnavailable, timestamp, "尚未确定 Minecraft 目录"),
                MachineEnvironmentCatalog.InstanceVolumeFreeBytes.Unavailable(
                    CapabilityAvailability.TemporarilyUnavailable, timestamp, "尚未确定 Minecraft 目录"),
            ]);
        }

        return ValueTask.FromResult(MachineInstanceCatalog.CollectPathScope(instanceDirectory, timestamp));
    }

}

