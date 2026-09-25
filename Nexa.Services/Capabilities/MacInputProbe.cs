using System.Runtime.InteropServices;

namespace Nexa.Services.Capabilities;

/// <summary>Read-only HID collection discovery. Does not open devices or install input hooks.</summary>
internal static partial class MacInputProbe
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    internal static unsafe IReadOnlyList<ICapability> Collect(DateTimeOffset timestamp)
    {
        nint manager = IOHIDManagerCreate(0, 0), set = 0;
        try
        {
            if (manager == 0) return Unavailable(timestamp);
            IOHIDManagerSetDeviceMatching(manager, 0);
            set = IOHIDManagerCopyDevices(manager);
            if (set == 0) return Unavailable(timestamp);
            nint count = CFSetGetCount(set);
            if (count < 0 || count > 4096) return Unavailable(timestamp);
            nint[] devices = new nint[(int)count];
            fixed (nint* buffer = devices) CFSetGetValues(set, buffer);
            bool Has(uint page, uint usage) => devices.Any(device => IOHIDDeviceConformsTo(device, page, usage) != 0);
            const string source = "macOS IOKit HID usage collections";
            int controllers = devices.Count(device => IOHIDDeviceConformsTo(device, 1, 4) != 0 || IOHIDDeviceConformsTo(device, 1, 5) != 0);
            return new ICapability[]
            {
                InputCatalog.InputKeyboardAvailable.Observe(Has(1, 6), timestamp, source),
                InputCatalog.InputMouseAvailable.Observe(Has(1, 2) || Has(0x0d, 5), timestamp, source),
                InputCatalog.InputTouchAvailable.Observe(Has(0x0d, 4), timestamp, source),
                InputCatalog.InputPenAvailable.Observe(Has(0x0d, 2), timestamp, source),
                InputCatalog.InputControllerAvailable.Observe(controllers > 0, timestamp, source),
                InputCatalog.InputControllerCount.Observe(controllers, timestamp, source),
                InputCatalog.InputGyroscopeAvailable.Unavailable(CapabilityAvailability.Unknown, timestamp, "需要 GameController 运动传感器通道"),
                InputCatalog.InputGyroscopeDevices.Unavailable(CapabilityAvailability.Unknown, timestamp, "需要 GameController 运动传感器通道"),
                InputCatalog.InputHapticsAvailable.Unavailable(CapabilityAvailability.Unknown, timestamp, "需要 CoreHaptics 能力通道"),
                InputCatalog.InputHapticsDevices.Unavailable(CapabilityAvailability.Unknown, timestamp, "需要 CoreHaptics 能力通道"),
            };
        }
        finally
        {
            if (set != 0) CFRelease(set);
            if (manager != 0) CFRelease(manager);
        }
    }

    private static ICapability[] Unavailable(DateTimeOffset timestamp) => InputCatalog.Definitions()
        .Where(definition => !definition.Id.StartsWith("input.usage.", StringComparison.Ordinal))
        .Select(definition => definition.Unavailable(CapabilityAvailability.TemporarilyUnavailable, timestamp, "无法枚举 macOS HID 设备")).ToArray();

    [LibraryImport(IOKit)] private static partial nint IOHIDManagerCreate(nint allocator, uint options);
    [LibraryImport(IOKit)] private static partial void IOHIDManagerSetDeviceMatching(nint manager, nint matching);
    [LibraryImport(IOKit)] private static partial nint IOHIDManagerCopyDevices(nint manager);
    [LibraryImport(IOKit)] private static partial byte IOHIDDeviceConformsTo(nint device, uint usagePage, uint usage);
    [LibraryImport(CoreFoundation)] private static partial nint CFSetGetCount(nint set);
    [LibraryImport(CoreFoundation)] private static unsafe partial void CFSetGetValues(nint set, nint* values);
    [LibraryImport(CoreFoundation)] private static partial void CFRelease(nint value);
}
