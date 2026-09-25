using System.Globalization;

namespace Nexa.Services.Capabilities;

internal static class LinuxInputProbe
{
    internal sealed record Device(string Name, bool Keyboard, bool Mouse, bool Touch, bool Pen, bool Controller, bool Haptics);
    internal sealed record Snapshot(IReadOnlyList<Device> Devices, bool Complete, IReadOnlyList<InputDeviceFeature> Gyroscopes, bool GyroscopesComplete);

    internal static Snapshot Read(string inputRoot = "/sys/class/input", string sensorRoot = "/sys/bus/iio/devices", int wordBits = 0)
    {
        List<Device> devices = [];
        List<InputDeviceFeature> gyroscopes = [];
        bool complete = Directory.Exists(inputRoot), sensorsComplete = Directory.Exists(sensorRoot);
        try
        {
            if (complete)
                foreach (string path in Directory.EnumerateDirectories(inputRoot, "input*").Take(257))
                {
                    if (devices.Count == 256) { complete = false; break; }
                    try
                    {
                        string name = ReadText(Path.Combine(path, "name"));
                        string key = ReadText(Path.Combine(path, "capabilities", "key"));
                        string relative = ReadText(Path.Combine(path, "capabilities", "rel"));
                        string absolute = ReadText(Path.Combine(path, "capabilities", "abs"));
                        string effects = ReadText(Path.Combine(path, "capabilities", "ff"));
                        string properties = ReadText(Path.Combine(path, "properties"));
                        bool Key(int bit) => Bit(key, bit, wordBits);
                        bool pen = Key(0x140) || Key(0x141);
                        bool controller = Enumerable.Range(0x120, 32).Any(Key);
                        devices.Add(new(name, Key(30) && Key(44) && Key(28),
                            Key(0x110) && Bit(relative, 0, wordBits) && Bit(relative, 1, wordBits),
                            !pen && Bit(properties, 1, wordBits) && (Key(0x14a) || Bit(absolute, 0x39, wordBits)),
                            pen, controller, Bit(effects, 0x50, wordBits) || Bit(effects, 0x51, wordBits)));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
                    { complete = false; }
                }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { complete = false; }
        try
        {
            int count = 0;
            if (sensorsComplete)
                foreach (string path in Directory.EnumerateDirectories(sensorRoot, "iio:device*"))
                {
                    if (++count > 256) { sensorsComplete = false; break; }
                    try
                    {
                        bool angular = Directory.EnumerateFiles(path, "in_anglvel_*").Take(128).Any(file =>
                            file.EndsWith("_raw", StringComparison.Ordinal) || file.EndsWith("_input", StringComparison.Ordinal));
                        if (angular) gyroscopes.Add(new(ReadText(Path.Combine(path, "name")), true));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
                    { sensorsComplete = false; }
                }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { sensorsComplete = false; }
        return new(devices.AsReadOnly(), complete, gyroscopes.AsReadOnly(), sensorsComplete);
    }

    internal static bool Bit(string bitmap, int bit, int wordBits = 0)
    {
        int width = wordBits == 0 ? IntPtr.Size * 8 : wordBits;
        if (width is not (32 or 64) || bit < 0) throw new ArgumentOutOfRangeException(nameof(wordBits));
        string[] words = bitmap.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is 0 or > 64) throw new FormatException("Invalid kernel capability bitmap.");
        ulong result = 0;
        for (int index = 0; index < words.Length; index++)
        {
            if (words[index].Length > width / 4 || !ulong.TryParse(words[index], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong value))
                throw new FormatException("Invalid kernel capability bitmap.");
            if (words.Length - 1 - index == bit / width) result = value;
        }
        return (result & (1UL << (bit % width))) != 0;
    }

    private static string ReadText(string path)
    {
        using var reader = new StreamReader(path);
        char[] buffer = new char[8193];
        int count = reader.ReadBlock(buffer, 0, buffer.Length);
        if (count == buffer.Length) throw new IOException("Device attribute exceeds its budget.");
        return new string(buffer, 0, count).Trim();
    }

    internal static IReadOnlyList<ICapability> Project(Snapshot snapshot, DateTimeOffset timestamp)
    {
        const string source = "Linux sysfs input 能力位图";
        ICapability Flag(CapabilityDefinition<bool> definition, bool value, bool complete) => value || complete
            ? definition.Observe(value, timestamp, source)
            : definition.Unavailable(CapabilityAvailability.Unknown, timestamp, "设备枚举不完整，请检查 sysfs 访问权限后刷新。");
        return new ICapability[]
        {
            Flag(InputCatalog.InputKeyboardAvailable, snapshot.Devices.Any(item => item.Keyboard), snapshot.Complete),
            Flag(InputCatalog.InputMouseAvailable, snapshot.Devices.Any(item => item.Mouse), snapshot.Complete),
            Flag(InputCatalog.InputTouchAvailable, snapshot.Devices.Any(item => item.Touch), snapshot.Complete),
            Flag(InputCatalog.InputPenAvailable, snapshot.Devices.Any(item => item.Pen), snapshot.Complete),
            Flag(InputCatalog.InputControllerAvailable, snapshot.Devices.Any(item => item.Controller), snapshot.Complete),
            snapshot.Complete ? InputCatalog.InputControllerCount.Observe(snapshot.Devices.Count(item => item.Controller), timestamp, source)
                : InputCatalog.InputControllerCount.Unavailable(CapabilityAvailability.Unknown, timestamp, "设备枚举不完整"),
            Flag(InputCatalog.InputHapticsAvailable, snapshot.Devices.Any(item => item.Haptics), snapshot.Complete),
            snapshot.Complete ? InputCatalog.InputHapticsDevices.Observe(snapshot.Devices.Where(item => item.Controller || item.Haptics)
                .Select(item => new InputDeviceFeature(item.Name, item.Haptics)).ToArray(), timestamp, source)
                : InputCatalog.InputHapticsDevices.Unavailable(CapabilityAvailability.Unknown, timestamp, "设备枚举不完整"),
            Flag(InputCatalog.InputGyroscopeAvailable, snapshot.Gyroscopes.Count > 0, snapshot.GyroscopesComplete),
            snapshot.GyroscopesComplete ? InputCatalog.InputGyroscopeDevices.Observe(snapshot.Gyroscopes, timestamp, "Linux IIO angular velocity 通道")
                : InputCatalog.InputGyroscopeDevices.Unavailable(CapabilityAvailability.Unknown, timestamp, "IIO 枚举不完整或不可访问"),
        };
    }
}
