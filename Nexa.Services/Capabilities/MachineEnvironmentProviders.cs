using System.Runtime.InteropServices;

namespace Nexa.Services.Capabilities;

/// <summary>
/// Registry definitions for the machine-fact namespaces that the first slice deferred:
/// display, storage, filesystem and power. Every definition mirrors Registry 1.1; providers
/// report facts they can truly observe and mark everything else unavailable — never
/// synthesized (no marketing VRAM, no invented thermal numbers).
/// </summary>
public static class MachineEnvironmentCatalog
{
    public const string DisplayProviderId = "nexa.display";
    public const string StorageProviderId = "nexa.storage";
    public const string FilesystemProviderId = "nexa.filesystem";
    public const string PowerProviderId = "nexa.power";

    // display.*
    public static readonly CapabilityDefinition<int> DisplayCount = new("display.count", "显示器数量", "显示", DisplayProviderId);
    public static readonly CapabilityDefinition<bool> DisplayPrimaryInternal = new("display.internal", "主显示器为内建屏", "显示", DisplayProviderId);
    public static readonly CapabilityDefinition<long> DisplayPrimaryHorizontalResolution = new("display.resolution", "主显示器分辨率", "显示", DisplayProviderId,
        CapabilityKind.Metric, CapabilityStability.Session, unit: "px");
    public static readonly CapabilityDefinition<double> DisplayPrimaryRefreshHz = new("display.refresh.current", "主显示器刷新率", "显示", DisplayProviderId,
        CapabilityKind.Metric, CapabilityStability.Session, unit: "Hz");

    // storage.*
    public static readonly CapabilityDefinition<long> InstanceVolumeFreeBytes = new("storage.device.capacity.free", "实例所在卷剩余空间", "存储", StorageProviderId,
        CapabilityKind.Metric, CapabilityStability.Dynamic, unit: "bytes");

    // filesystem.*
    public static readonly CapabilityDefinition<bool> InstancePathExists = new("filesystem.path.exists", "实例路径存在", "文件系统", FilesystemProviderId,
        CapabilityKind.Fact, CapabilityStability.Dynamic);
    public static readonly CapabilityDefinition<bool> InstancePathWritable = new("filesystem.path.writable", "实例路径可写", "文件系统", FilesystemProviderId,
        CapabilityKind.Fact, CapabilityStability.Dynamic);
    public static readonly CapabilityDefinition<bool> FilesystemCaseSensitive = new("filesystem.case_sensitive", "文件系统大小写敏感", "文件系统", FilesystemProviderId,
        CapabilityKind.Fact, CapabilityStability.Static);
    public static readonly CapabilityDefinition<bool> FilesystemSymlink = new("filesystem.symlink", "符号链接支持", "文件系统", FilesystemProviderId,
        CapabilityKind.Fact, CapabilityStability.Static);
    public static readonly CapabilityDefinition<bool> FilesystemReflink = new("filesystem.reflink", "文件克隆", "文件系统", FilesystemProviderId,
        CapabilityKind.Action, CapabilityStability.Static);

    // power.*
    public static readonly CapabilityDefinition<string> PowerSource = new("power.source", "电源来源", "电源", PowerProviderId);
    public static readonly CapabilityDefinition<bool> PowerBatteryPresent = new("power.battery.present", "存在电池", "电源", PowerProviderId);
    public static readonly CapabilityDefinition<int> PowerBatteryLevelPercent = new("power.battery.level", "电池电量", "电源", PowerProviderId,
        CapabilityKind.Metric, CapabilityStability.Dynamic, unit: "%");
    public static readonly CapabilityDefinition<bool> PowerBatteryCharging = new("power.battery.charging", "电池充电中", "电源", PowerProviderId);
    public static readonly CapabilityDefinition<string> PowerProfileCurrent = new("power.profile.current", "电源模式", "电源", PowerProviderId);

    public static CapabilityRegistry MergeInto(CapabilityRegistry registry) => new(
        [.. registry.Definitions,
            DisplayCount, DisplayPrimaryInternal, DisplayPrimaryHorizontalResolution, DisplayPrimaryRefreshHz,
            InstanceVolumeFreeBytes,
            InstancePathExists, InstancePathWritable, FilesystemCaseSensitive, FilesystemSymlink, FilesystemReflink,
            PowerSource, PowerBatteryPresent, PowerBatteryLevelPercent, PowerBatteryCharging, PowerProfileCurrent]);
}

/// <summary>Observes display geometry through the platform's own APIs; nothing is estimated.</summary>
internal sealed partial class DisplayCapabilityProvider : IMachineCapabilityProvider
{
    public string Id => MachineEnvironmentCatalog.DisplayProviderId;

    public ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            return CollectWindows(timestamp);
        }
        if (OperatingSystem.IsMacOS())
        {
            return CollectMacOs(timestamp);
        }
        if (OperatingSystem.IsLinux())
        {
            return CollectLinux(timestamp);
        }

        return Unavailable(timestamp);
    }

    private ValueTask<IReadOnlyList<ICapability>> CollectWindows(DateTimeOffset timestamp)
    {
        const uint monitorDefaultToNearest = 2;
        List<Rect> monitors = [];
        bool Callback(nint monitor, nint hdc, ref Rect rect, nint data)
        {
            monitors.Add(rect);
            return true;
        }

        EnumDisplayMonitors(nint.Zero, nint.Zero, Callback, nint.Zero);
        if (monitors.Count == 0)
        {
            return Unavailable(timestamp);
        }

        nint primaryHandle = MonitorFromPoint(new Point { X = 0, Y = 0 }, monitorDefaultToNearest);
        MONITORINFOEX info = new() { Size = Marshal.SizeOf<MONITORINFOEX>() };
        bool hasInfo = GetMonitorInfo(primaryHandle, ref info);
        int width = hasInfo ? info.Monitor.Right - info.Monitor.Left : monitors[0].Right - monitors[0].Left;
        int height = hasInfo ? info.Monitor.Bottom - info.Monitor.Top : monitors[0].Bottom - monitors[0].Top;
        nint dc = GetDC(nint.Zero);
        int logicalHz = 0;
        if (dc != nint.Zero)
        {
            logicalHz = GetDeviceCaps(dc, 116); // VREFRESH
            _ = ReleaseDC(nint.Zero, dc);
        }

        double refresh = logicalHz > 1 ? logicalHz : 0;
        return ValueTask.FromResult<IReadOnlyList<ICapability>>(Array.AsReadOnly<ICapability>([
            MachineEnvironmentCatalog.DisplayCount.Observe(monitors.Count, timestamp, "Windows EnumDisplayMonitors"),
            // Telling a laptop panel from an external monitor needs QueryDisplayConfig, which
            // is a later slice; the fact reports unavailable instead of a guess.
            MachineEnvironmentCatalog.DisplayPrimaryInternal.Unavailable(
                CapabilityAvailability.NotImplemented, timestamp, "内建屏判定尚未接入"),
            MachineEnvironmentCatalog.DisplayPrimaryHorizontalResolution.Observe(checked(width * (long)height), timestamp, "Windows GetMonitorInfo"),
            refresh > 0
                ? MachineEnvironmentCatalog.DisplayPrimaryRefreshHz.Observe(refresh, timestamp, "Windows GetDeviceCaps(VREFRESH)")
                : MachineEnvironmentCatalog.DisplayPrimaryRefreshHz.Unavailable(CapabilityAvailability.Unknown, timestamp, "刷新率不可读"),
        ]));
    }

    private static ValueTask<IReadOnlyList<ICapability>> CollectMacOs(DateTimeOffset timestamp)
    {
        nint mainId = CGMainDisplayID();
        if (mainId == nint.Zero)
        {
            return Unavailable(timestamp);
        }

        uint width = CGDisplayPixelsWide(mainId);
        uint height = CGDisplayPixelsHigh(mainId);
        var mode = CGDisplayCopyDisplayMode(mainId);
        double refresh = mode != nint.Zero ? CGDisplayModeGetRefreshRate(mode) : 0;
        return ValueTask.FromResult<IReadOnlyList<ICapability>>(Array.AsReadOnly<ICapability>([
            MachineEnvironmentCatalog.DisplayCount.Unavailable(CapabilityAvailability.NotImplemented, timestamp, "多显示器枚举尚未接入"),
            MachineEnvironmentCatalog.DisplayPrimaryInternal.Unavailable(CapabilityAvailability.NotImplemented, timestamp, "内建屏判定尚未接入"),
            MachineEnvironmentCatalog.DisplayPrimaryHorizontalResolution.Observe(checked(width * (long)height), timestamp, "CoreGraphics CGMainDisplayID"),
            refresh > 0
                ? MachineEnvironmentCatalog.DisplayPrimaryRefreshHz.Observe(refresh, timestamp, "CoreGraphics CGDisplayCopyDisplayMode")
                : MachineEnvironmentCatalog.DisplayPrimaryRefreshHz.Unavailable(CapabilityAvailability.Unknown, timestamp, "刷新率不可读"),
        ]));
    }

    private static ValueTask<IReadOnlyList<ICapability>> CollectLinux(DateTimeOffset timestamp)
    {
        // X11 sessions report through the standard XRandR geometry; Wayland compositors do
        // not expose a portable per-display API to a non-graphical process.
        if (OperatingSystem.IsLinux() && File.Exists("/sys/class/graphics/fb0/virtual_size"))
        {
            string[] sizes = File.ReadAllText("/sys/class/graphics/fb0/virtual_size").Trim().Split(',');
            if (sizes.Length == 2 && int.TryParse(sizes[0], out int width) && int.TryParse(sizes[1], out int height))
            {
                return ValueTask.FromResult<IReadOnlyList<ICapability>>(Array.AsReadOnly<ICapability>([
                    MachineEnvironmentCatalog.DisplayCount.Unavailable(CapabilityAvailability.NotImplemented, timestamp, "多显示器枚举尚未接入"),
                    MachineEnvironmentCatalog.DisplayPrimaryInternal.Unavailable(CapabilityAvailability.NotImplemented, timestamp, "内建屏判定尚未接入"),
                    MachineEnvironmentCatalog.DisplayPrimaryHorizontalResolution.Observe(checked(width * (long)height), timestamp, "sysfs framebuffer virtual_size"),
                    MachineEnvironmentCatalog.DisplayPrimaryRefreshHz.Unavailable(CapabilityAvailability.NotImplemented, timestamp, "刷新率尚未接入"),
                ]));
            }
        }

        return Unavailable(timestamp);
    }

    private static ValueTask<IReadOnlyList<ICapability>> Unavailable(DateTimeOffset timestamp) =>
        ValueTask.FromResult<IReadOnlyList<ICapability>>(Array.AsReadOnly(
            MachineCapabilityCatalog.CreateRegistry().Definitions.Where(item => item.Provider == MachineEnvironmentCatalog.DisplayProviderId)
                .Select(item => item.Unavailable(CapabilityAvailability.NotImplemented, timestamp, "此平台的显示检测尚未接入")).ToArray()));

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, ref Rect rect, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFOEX info);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(nint dc, int index);

    [DllImport("CoreGraphics")]
    private static extern nint CGMainDisplayID();

    [DllImport("CoreGraphics")]
    private static extern uint CGDisplayPixelsWide(nint display);

    [DllImport("CoreGraphics")]
    private static extern uint CGDisplayPixelsHigh(nint display);

    [DllImport("CoreGraphics")]
    private static extern nint CGDisplayCopyDisplayMode(nint display);

    [DllImport("CoreGraphics")]
    private static extern double CGDisplayModeGetRefreshRate(nint mode);
}
