using Nexa.Services.Capabilities;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask NativeInputProbeEnumeratesWithoutHooks()
    {
        var facts = await new InputCapabilityProvider().CollectAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        AssertEqual(InputCatalog.Definitions().Count, facts.Count);
        AssertEqual(facts.Count, facts.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        if (OperatingSystem.IsWindows())
            AssertEqual(CapabilityAvailability.Unknown, facts.Single(item => item.Id == InputCatalog.InputGyroscopeAvailable.Id).Availability);
        if (OperatingSystem.IsMacOS())
            AssertFalse(facts.Any(item => item.Availability == CapabilityAvailability.NotImplemented));
    }

    private static void LinuxInputUsesCapabilityBitsAndPreservesUnknown()
    {
        AssertTrue(LinuxInputProbe.Bit("1 0", 64, 64));
        AssertFalse(LinuxInputProbe.Bit("1 0", 32, 64));
        AssertTrue(LinuxInputProbe.Bit("1 0", 32, 32));
        bool rejected = false;
        try { LinuxInputProbe.Bit("invalid 0", 0); } catch (FormatException) { rejected = true; }
        AssertTrue(rejected);
        string root = Path.Combine(Path.GetTempPath(), "nexa-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string input = Path.Combine(root, "input"), sensors = Path.Combine(root, "sensors");
            Directory.CreateDirectory(input); Directory.CreateDirectory(sensors);
            void Device(string id, string key, string relative, string absolute, string ff, string properties)
            {
                string path = Path.Combine(input, id);
                Directory.CreateDirectory(Path.Combine(path, "capabilities"));
                File.WriteAllText(Path.Combine(path, "name"), "Fixture without a product-type name");
                File.WriteAllText(Path.Combine(path, "properties"), properties);
                foreach (var pair in new[] { ("key", key), ("rel", relative), ("abs", absolute), ("ff", ff) })
                    File.WriteAllText(Path.Combine(path, "capabilities", pair.Item1), pair.Item2);
            }
            Device("input0", "100050000000", "0", "0", "0", "0"); // A, Z and Enter are corrected below.
            File.WriteAllText(Path.Combine(input, "input0", "capabilities", "key"), ((1UL << 30) | (1UL << 44) | (1UL << 28)).ToString("x", System.Globalization.CultureInfo.InvariantCulture));
            Device("input1", "100000000 0 0 0 0", "0", "0", "10000 0", "0"); // BTN_JOYSTICK and FF_RUMBLE.
            Device("input2", "0", "0", "200000000000000", "0", "2"); // multitouch tracking + direct.
            var snapshot = LinuxInputProbe.Read(input, sensors, 64);
            AssertTrue(snapshot.Complete);
            AssertTrue(snapshot.Devices.Any(item => item.Keyboard));
            AssertTrue(snapshot.Devices.Any(item => item.Controller && item.Haptics));
            AssertTrue(snapshot.Devices.Any(item => item.Touch));
            var facts = LinuxInputProbe.Project(snapshot, DateTimeOffset.UtcNow);
            AssertEqual(1, ((Capability<int>)facts.Single(item => item.Id == InputCatalog.InputControllerCount.Id)).Value);
            File.Delete(Path.Combine(input, "input0", "capabilities", "key"));
            snapshot = LinuxInputProbe.Read(input, sensors, 64);
            AssertFalse(snapshot.Complete);
            facts = LinuxInputProbe.Project(snapshot, DateTimeOffset.UtcNow);
            AssertEqual(CapabilityAvailability.Unknown, facts.Single(item => item.Id == InputCatalog.InputKeyboardAvailable.Id).Availability);
            AssertEqual(CapabilityAvailability.Available, facts.Single(item => item.Id == InputCatalog.InputHapticsAvailable.Id).Availability);
            AssertFalse(LinuxInputProbe.Read(Path.Combine(root, "absent"), sensors).Complete);
        }
        finally { Directory.Delete(root, true); }
    }
}
