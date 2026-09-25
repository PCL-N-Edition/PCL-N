using System.Runtime.InteropServices;

namespace Nexa.Services.Capabilities;

/// <summary>Registry definitions for formfactor.* (§15) and input.* (§16).</summary>
public static class FormFactorCatalog
{
    public const string ProviderId = "nexa.formfactor";

    public static readonly CapabilityDefinition<string> FormFactorType = new(
        "formfactor.type", "设备形态", "形态", ProviderId);
    public static readonly CapabilityDefinition<bool> FormFactorPortable = new(
        "formfactor.portable", "便携设备", "形态", ProviderId);
    public static readonly CapabilityDefinition<bool> FormFactorBatteryPowered = new(
        "formfactor.battery_powered", "电池供电", "形态", ProviderId, CapabilityKind.Derived,
        CapabilityStability.Dynamic, ["power.battery.present"]);
    public static readonly CapabilityDefinition<bool> FormFactorHandheld = new(
        "formfactor.handheld", "掌机形态", "形态", ProviderId);

    public static readonly string[] Scope =
    [
        FormFactorType.Id, FormFactorPortable.Id, FormFactorBatteryPowered.Id, FormFactorHandheld.Id,
    ];

    public static readonly Dictionary<string, ICapabilityDefinition> Definitions = new(StringComparer.Ordinal)
    {
        [FormFactorType.Id] = FormFactorType,
        [FormFactorPortable.Id] = FormFactorPortable,
        [FormFactorBatteryPowered.Id] = FormFactorBatteryPowered,
        [FormFactorHandheld.Id] = FormFactorHandheld,
    };
}

/// <summary>input.* availability facts (§16). Presence only — recent-usage tracking needs
/// global hooks and stays unplugged by name.</summary>
public static class InputCatalog
{
    public const string ProviderId = "nexa.input";

    public static readonly CapabilityDefinition<bool> InputKeyboardAvailable = new(
        "input.keyboard.available", "键盘可用", "输入", ProviderId);
    public static readonly CapabilityDefinition<bool> InputMouseAvailable = new(
        "input.mouse.available", "鼠标可用", "输入", ProviderId);
    public static readonly CapabilityDefinition<bool> InputTouchAvailable = new(
        "input.touch.available", "触摸屏可用", "输入", ProviderId);
    public static readonly CapabilityDefinition<bool> InputPenAvailable = new(
        "input.pen.available", "触控笔可用", "输入", ProviderId);
    public static readonly CapabilityDefinition<int> InputControllerCount = new(
        "input.controller.count", "已连接手柄数", "输入", ProviderId, CapabilityKind.Metric);
    public static readonly CapabilityDefinition<bool> InputControllerAvailable = new(
        "input.controller.available", "手柄可用", "输入", ProviderId);
    public static readonly CapabilityDefinition<bool> InputGyroscopeAvailable = new(
        "input.gyroscope.available", "陀螺仪可用", "输入", ProviderId);
    public static readonly CapabilityDefinition<bool> InputHapticsAvailable = new(
        "input.haptics.available", "振动反馈可用", "输入", ProviderId);
    public static readonly CapabilityDefinition<IReadOnlyList<InputDeviceFeature>> InputGyroscopeDevices = new(
        "input.gyroscope.devices", "陀螺仪", "输入", ProviderId);
    public static readonly CapabilityDefinition<IReadOnlyList<InputDeviceFeature>> InputHapticsDevices = new(
        "input.haptics.devices", "振动反馈", "输入", ProviderId);
    public static readonly CapabilityDefinition<string> InputUsagePrimary = new(
        "input.usage.primary", "主要输入方式", "输入", ProviderId, CapabilityKind.Metric,
        CapabilityStability.Dynamic);
    public static readonly CapabilityDefinition<bool> InputUsageRecentKeyboard = new(
        "input.usage.recent.keyboard", "最近使用键盘", "输入", ProviderId, CapabilityKind.Metric,
        CapabilityStability.Dynamic);
    public static readonly CapabilityDefinition<bool> InputUsageRecentMouse = new(
        "input.usage.recent.mouse", "最近使用鼠标", "输入", ProviderId, CapabilityKind.Metric,
        CapabilityStability.Dynamic);
    public static readonly CapabilityDefinition<bool> InputUsageRecentTouch = new(
        "input.usage.recent.touch", "最近使用触摸", "输入", ProviderId, CapabilityKind.Metric,
        CapabilityStability.Dynamic);
    public static readonly CapabilityDefinition<bool> InputUsageRecentController = new(
        "input.usage.recent.controller", "最近使用手柄", "输入", ProviderId, CapabilityKind.Metric,
        CapabilityStability.Dynamic);

    public static IReadOnlyList<ICapabilityDefinition> Definitions() => (ICapabilityDefinition[])
    [
        InputKeyboardAvailable, InputMouseAvailable, InputTouchAvailable, InputPenAvailable,
        InputControllerCount, InputControllerAvailable, InputGyroscopeAvailable, InputHapticsAvailable,
        InputGyroscopeDevices, InputHapticsDevices,
        InputUsagePrimary, InputUsageRecentKeyboard, InputUsageRecentMouse, InputUsageRecentTouch,
        InputUsageRecentController,
    ];
}

public sealed record InputDeviceFeature(string DeviceName, bool Available);

public enum InputUsageKind { Unknown, Keyboard, Mouse, Touch, Controller }

/// <summary>
/// Session-local input evidence. Platform hosts report input after their own hit testing; the
/// capability provider only reads this bounded, lock-protected history and never installs a
/// global hook.
/// </summary>
public sealed class InputUsageTracker(TimeProvider? clock = null, TimeSpan? recentWindow = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly TimeSpan _recentWindow = recentWindow ?? TimeSpan.FromMinutes(5);
    private readonly DateTimeOffset?[] _lastSeen = new DateTimeOffset?[5];
    private InputUsageKind _primary;

    public void Record(InputUsageKind kind)
    {
        if (kind == InputUsageKind.Unknown) return;
        lock (_gate)
        {
            _primary = kind;
            _lastSeen[(int)kind] = _clock.GetUtcNow();
        }
    }

    public InputUsageSnapshot Read()
    {
        lock (_gate)
        {
            DateTimeOffset now = _clock.GetUtcNow();
            bool Recent(InputUsageKind kind) => _lastSeen[(int)kind] is { } seen && now - seen <= _recentWindow;
            return new(_primary, Recent(InputUsageKind.Keyboard), Recent(InputUsageKind.Mouse),
                Recent(InputUsageKind.Touch), Recent(InputUsageKind.Controller));
        }
    }
}

public sealed record InputUsageSnapshot(InputUsageKind Primary, bool Keyboard, bool Mouse, bool Touch, bool Controller);

/// <summary>
/// Form-factor heuristic (§15): battery + internal panel = Laptop; battery + touch-first + no
/// keyboard = Handheld (Steam-Deck class); otherwise Desktop. The inputs come from facts the
/// other providers already collected, so this provider never re-probes hardware.
/// </summary>
public sealed class FormFactorCapabilityProvider(Func<bool>? batteryPresent = null, Func<bool>? internalDisplay = null,
    Func<bool>? touchAvailable = null, Func<bool>? keyboardAvailable = null, Func<bool>? controllerAvailable = null)
    : IMachineCapabilityProvider
{
    private readonly Func<bool> _batteryPresent = batteryPresent ?? DefaultBatteryProbe;
    private readonly Func<bool> _internalDisplay = internalDisplay ?? DefaultInternalPanelProbe;
    private readonly Func<bool> _touchAvailable = touchAvailable ?? DefaultTouchProbe;
    private readonly Func<bool> _keyboardAvailable = keyboardAvailable ?? DefaultKeyboardProbe;
    private readonly Func<bool> _controllerAvailable = controllerAvailable ?? DefaultControllerProbe;

    public string Id => FormFactorCatalog.ProviderId;

    public ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string source = "形态启发式（电池 + 内建屏 + 触摸）";
        bool battery = Safe(_batteryPresent);
        bool internalPanel = Safe(_internalDisplay);
        bool touch = Safe(_touchAvailable);
        bool handheld = battery && internalPanel && touch && !_keyboardAvailable() && _controllerAvailable();
        string type = handheld ? "Handheld" : battery && internalPanel ? "Laptop" : "Desktop";
        return ValueTask.FromResult<IReadOnlyList<ICapability>>(Array.AsReadOnly(new ICapability[]
        {
            FormFactorCatalog.FormFactorType.Observe(type, timestamp, source),
            FormFactorCatalog.FormFactorPortable.Observe(battery, timestamp, source),
            FormFactorCatalog.FormFactorBatteryPowered.Observe(battery, timestamp, source),
            FormFactorCatalog.FormFactorHandheld.Observe(handheld, timestamp, source),
        }));

        static bool Safe(Func<bool> probe)
        {
            try
            {
                return probe();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
            {
                return false;
            }
        }
    }

    private static bool DefaultBatteryProbe() =>
        OperatingSystem.IsWindows()
            ? GetSystemPowerStatus(out var status) && status is { BatteryFlag: not 128, ACLineStatus: not 255 }
            : File.Exists("/sys/class/power_supply/BAT0");

    private static bool DefaultInternalPanelProbe() =>
        OperatingSystem.IsWindows() && DisplayCapabilityProbe.IsPrimaryInternalProbe();

    private static bool DefaultTouchProbe() =>
        OperatingSystem.IsWindows()
            ? WindowsInputProbe.HasTouch(GetSystemMetrics(SmDigitizer))
            : OperatingSystem.IsLinux() && LinuxInputProbe.Read().Devices.Any(device => device.Touch);

    private static bool DefaultKeyboardProbe() => OperatingSystem.IsWindows()
        ? WindowsInputProbe.KeyboardPresent() != false
        : OperatingSystem.IsLinux() && LinuxInputProbe.Read().Devices.Any(device => device.Keyboard);

    private static bool DefaultControllerProbe()
    {
        if (OperatingSystem.IsWindows())
            for (uint user = 0; user < 4; user++) if (XInputGetState(user, out _) == 0) return true;
        return OperatingSystem.IsLinux() && Directory.Exists("/dev/input")
            && Directory.EnumerateFiles("/dev/input", "js*").Any();
    }

    private const int SmDigitizer = 0x2004;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryPercent;
        public byte Reserved;
        public uint BatteryLifetime;
        public uint BatteryFullLifetime;
    }

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern int GetSystemMetrics(int index);

    [DllImport("xinput1_4.dll", SetLastError = false)]
    private static extern int XInputGetState(uint userIndex, out XInputState state);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public ushort GamepadButtons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }
}

/// <summary>input.* availability: presence probes only, per platform.</summary>
public sealed class InputCapabilityProvider(InputUsageTracker? usage = null) : IMachineCapabilityProvider
{
    private readonly InputUsageTracker _usage = usage ?? new InputUsageTracker();
    public string Id => InputCatalog.ProviderId;

    public ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult<IReadOnlyList<ICapability>>(AppendUsage(CollectWindows(timestamp), timestamp));
        }

        if (OperatingSystem.IsLinux())
        {
            return ValueTask.FromResult<IReadOnlyList<ICapability>>(AppendUsage(CollectLinux(timestamp), timestamp));
        }

        if (OperatingSystem.IsMacOS())
            return ValueTask.FromResult<IReadOnlyList<ICapability>>(AppendUsage(MacInputProbe.Collect(timestamp), timestamp));
        return ValueTask.FromResult<IReadOnlyList<ICapability>>(AppendUsage(Unavailable(timestamp, "此平台尚未接入输入设备通道"), timestamp));
    }

    private System.Collections.ObjectModel.ReadOnlyCollection<ICapability> AppendUsage(IEnumerable<ICapability> facts, DateTimeOffset timestamp)
    {
        InputUsageSnapshot usage = _usage.Read();
        const string source = "Nexa 会话输入事件";
        return Array.AsReadOnly(facts.Concat(new ICapability[]
        {
            InputCatalog.InputUsagePrimary.Observe(usage.Primary.ToString(), timestamp, source,
                usage.Primary == InputUsageKind.Unknown ? CapabilityConfidence.Low : CapabilityConfidence.High),
            InputCatalog.InputUsageRecentKeyboard.Observe(usage.Keyboard, timestamp, source),
            InputCatalog.InputUsageRecentMouse.Observe(usage.Mouse, timestamp, source),
            InputCatalog.InputUsageRecentTouch.Observe(usage.Touch, timestamp, source),
            InputCatalog.InputUsageRecentController.Observe(usage.Controller, timestamp, source),
        }).ToArray());
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<ICapability> CollectWindows(DateTimeOffset timestamp)
    {
        const string source = "Windows GetSystemMetrics / XInput";
        const int SmDigitizer = 0x2004;
        const int SmMousePresent = 0x13;
        int digitizer = GetSystemMetrics(SmDigitizer);
        bool? keyboard = WindowsInputProbe.KeyboardPresent();
        List<(string Name, bool Haptics)> controllers = ReadXInputControllers();
        IReadOnlyList<InputDeviceFeature> haptics = Array.AsReadOnly(controllers
            .Select(static controller => new InputDeviceFeature(controller.Name, controller.Haptics)).ToArray());
        return Array.AsReadOnly(new ICapability[]
        {
            keyboard is { } present
                ? InputCatalog.InputKeyboardAvailable.Observe(present, timestamp, "Windows Raw Input 设备列表")
                : InputCatalog.InputKeyboardAvailable.Unavailable(CapabilityAvailability.TemporarilyUnavailable, timestamp, "无法读取 Windows 键盘设备列表"),
            InputCatalog.InputMouseAvailable.Observe(GetSystemMetrics(SmMousePresent) != 0, timestamp, source),
            InputCatalog.InputTouchAvailable.Observe(WindowsInputProbe.HasTouch(digitizer), timestamp, source),
            InputCatalog.InputPenAvailable.Observe(WindowsInputProbe.HasPen(digitizer), timestamp, source),
            InputCatalog.InputControllerCount.Observe(controllers.Count, timestamp, source),
            InputCatalog.InputControllerAvailable.Observe(controllers.Count > 0, timestamp, source),
            InputCatalog.InputGyroscopeAvailable.Unavailable(CapabilityAvailability.Unknown, timestamp, "XInput 不公开陀螺仪通道"),
            InputCatalog.InputHapticsAvailable.Observe(controllers.Any(static controller => controller.Haptics), timestamp, source),
            InputCatalog.InputGyroscopeDevices.Unavailable(CapabilityAvailability.Unknown, timestamp, "XInput 不公开陀螺仪通道"),
            InputCatalog.InputHapticsDevices.Observe(haptics, timestamp, source),
        });
    }

    private static IReadOnlyList<ICapability> CollectLinux(DateTimeOffset timestamp) =>
        LinuxInputProbe.Project(LinuxInputProbe.Read(), timestamp);

    private static System.Collections.ObjectModel.ReadOnlyCollection<ICapability> Unavailable(DateTimeOffset timestamp, string reason) =>
        Array.AsReadOnly(InputCatalog.Definitions().Where(static definition => !definition.Id.StartsWith("input.usage.", StringComparison.Ordinal))
            .Select(definition => definition.Unavailable(CapabilityAvailability.NotImplemented, timestamp, reason)).ToArray());

    private static int CountXInputControllers()
    {
        // XInputGetState over the four user slots; battery-free and returns ERROR_EMPTY on
        // free slots, so any success means a controller is present.
        int connected = 0;
        for (uint user = 0; user < 4; user++)
        {
            if (XInputGetState(user, out _) == 0)
            {
                connected++;
            }
        }

        return connected;
    }

    private static List<(string Name, bool Haptics)> ReadXInputControllers()
    {
        List<(string Name, bool Haptics)> devices = [];
        for (uint user = 0; user < 4; user++)
        {
            if (XInputGetCapabilities(user, 0, out XInputCapabilities capabilities) != 0)
            {
                continue;
            }

            string kind = capabilities.SubType switch
            {
                0x02 => "方向盘",
                0x03 => "街机摇杆",
                0x04 => "飞行摇杆",
                0x05 => "舞蹈垫",
                0x06 => "吉他控制器",
                0x08 => "鼓控制器",
                _ => "游戏手柄",
            };
            bool haptics = (capabilities.Flags & 0x0001) != 0
                || capabilities.Vibration.LeftMotorSpeed != 0
                || capabilities.Vibration.RightMotorSpeed != 0;
            devices.Add(($"XInput {kind} {user + 1}", haptics));
        }

        return devices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public ushort GamepadButtons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputCapabilities
    {
        public byte Type;
        public byte SubType;
        public ushort Flags;
        public XInputGamepad Gamepad;
        public XInputVibration Vibration;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputVibration
    {
        public ushort LeftMotorSpeed;
        public ushort RightMotorSpeed;
    }

    [DllImport("xinput1_4.dll", SetLastError = false)]
    private static extern int XInputGetState(uint userIndex, out XInputState state);

    [DllImport("xinput1_4.dll", SetLastError = false)]
    private static extern int XInputGetCapabilities(uint userIndex, uint flags, out XInputCapabilities capabilities);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern int GetSystemMetrics(int index);
}
