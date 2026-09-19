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

/// <summary>Power-source and battery facts per platform; unknown facts stay unknown.</summary>
internal sealed partial class PowerCapabilityProvider : IMachineCapabilityProvider
{
    public string Id => MachineEnvironmentCatalog.PowerProviderId;

    public ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return CollectPowerAsync(timestamp);
    }

    private static async ValueTask<IReadOnlyList<ICapability>> CollectPowerAsync(DateTimeOffset timestamp)
    {
        if (OperatingSystem.IsWindows())
        {
            SYSTEM_POWER_STATUS status = default;
            if (!GetSystemPowerStatus(ref status))
            {
                return [];
            }

            const string source = "Windows GetSystemPowerStatus";
            List<ICapability> facts =
            [
                MachineEnvironmentCatalog.PowerSource.Observe(
                    status.ACLineStatus == 1 ? "外接电源" : status.ACLineStatus == 0 ? "电池" : "未知",
                    timestamp, source),
            ];
            if (status.BatteryFlag == 128)
            {
                facts.Add(MachineEnvironmentCatalog.PowerBatteryPresent.Observe(false, timestamp, source));
                facts.Add(MachineEnvironmentCatalog.PowerBatteryLevelPercent.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "无电池"));
                facts.Add(MachineEnvironmentCatalog.PowerBatteryCharging.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "无电池"));
            }
            else
            {
                facts.Add(MachineEnvironmentCatalog.PowerBatteryPresent.Observe(true, timestamp, source));
                facts.Add(status.BatteryPercent is >= 0 and <= 100
                    ? MachineEnvironmentCatalog.PowerBatteryLevelPercent.Observe(status.BatteryPercent, timestamp, source)
                    : MachineEnvironmentCatalog.PowerBatteryLevelPercent.Unavailable(CapabilityAvailability.Unknown, timestamp, "电量不可读"));
                facts.Add(MachineEnvironmentCatalog.PowerBatteryCharging.Observe(
                    (status.BatteryFlag & 8) != 0, timestamp, source));
            }

            return facts;
        }

        if (OperatingSystem.IsLinux())
        {
            // /sys/class/power_supply is the standard ACPInterface; a desktop reads "no battery".
            string supply = "/sys/class/power_supply/BAT0/capacity";
            const string source = "Linux /sys/class/power_supply";
            if (File.Exists(supply))
            {
                string text = (await File.ReadAllTextAsync(supply).ConfigureAwait(false)).Trim();
                return Array.AsReadOnly<ICapability>(
                [
                    MachineEnvironmentCatalog.PowerSource.Observe("电池", timestamp, source),
                    MachineEnvironmentCatalog.PowerBatteryPresent.Observe(true, timestamp, source),
                    int.TryParse(text, out int percent) && percent is >= 0 and <= 100
                        ? MachineEnvironmentCatalog.PowerBatteryLevelPercent.Observe(percent, timestamp, source)
                        : MachineEnvironmentCatalog.PowerBatteryLevelPercent.Unavailable(CapabilityAvailability.Unknown, timestamp, "电量不可读"),
                    MachineEnvironmentCatalog.PowerBatteryCharging.Unavailable(CapabilityAvailability.Unknown, timestamp, "充电状态读取尚未接入"),
                ]);
            }

            return Array.AsReadOnly<ICapability>(
            [
                MachineEnvironmentCatalog.PowerSource.Observe("外接电源", timestamp, source),
                MachineEnvironmentCatalog.PowerBatteryPresent.Observe(false, timestamp, source),
                MachineEnvironmentCatalog.PowerBatteryLevelPercent.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "无电池"),
                MachineEnvironmentCatalog.PowerBatteryCharging.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "无电池"),
            ]);
        }

        if (OperatingSystem.IsMacOS())
        {
            // IOPSCopyPowerSourcesInfo interop is a later slice; report unknown, not guesses.
            return Array.AsReadOnly<ICapability>(
            [
                MachineEnvironmentCatalog.PowerSource.Unavailable(CapabilityAvailability.NotImplemented, timestamp, "macOS 电源检测尚未接入"),
                MachineEnvironmentCatalog.PowerBatteryPresent.Unavailable(CapabilityAvailability.NotImplemented, timestamp, "macOS 电源检测尚未接入"),
                MachineEnvironmentCatalog.PowerBatteryLevelPercent.Unavailable(CapabilityAvailability.NotImplemented, timestamp, "macOS 电源检测尚未接入"),
                MachineEnvironmentCatalog.PowerBatteryCharging.Unavailable(CapabilityAvailability.NotImplemented, timestamp, "macOS 电源检测尚未接入"),
            ]);
        }

        return [];
    }

    private static readonly bool supportsSymlinks = DetectSymlinkSupport();

    private static bool DetectSymlinkSupport()
    {
        if (OperatingSystem.IsWindows())
        {
            // Creating symlinks needs developer mode or admin; report capability, not permission.
            return OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build >= 14972;
        }

        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryPercent;
        public byte Reserved;
        public uint BatteryLifetime;
        public uint BatteryFullLifetime;

        public static SYSTEM_POWER_STATUS default_value => default;
    }

    [DllImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(ref SYSTEM_POWER_STATUS status);
}
