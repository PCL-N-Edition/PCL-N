using Nexa.Services.Accounts;
using Nexa.Services.Capabilities;
using Nexa.Services.Foundation;
using Nexa.Services.Settings;

// Diagnostic: compose the full foundation (with the Minecraft root bound), then force a
// capability refresh so every provider runs — printing each capability fact and any
// exception. Not part of CI; a developer diagnostic for the real-device freeze reports.
namespace Nexa.Tools.CapabilityProbe;

public static class Program
{
    public static async Task<int> Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "nexa-cap-probe");
        Directory.CreateDirectory(root);
        string settingsFolder = Path.Combine(root, "settings");
        Directory.CreateDirectory(settingsFolder);
        string profilesFolder = Path.Combine(root, "profiles");
        Directory.CreateDirectory(profilesFolder);
        SettingsSchema schema = LauncherDefaults.CreateSchema();

        string minecraftRoot = Path.Combine(root, "minecraft");
        Directory.CreateDirectory(minecraftRoot);
        FoundationHost host = FoundationComposer.Compose(
            new LauncherSettingsJsonPort(Path.Combine(settingsFolder, "settings.json"), schema),
            schema,
            new LaunchProfileFilePort(Path.Combine(profilesFolder, "profiles.json")),
            minecraftRootDirectory: minecraftRoot);

        Console.WriteLine("refreshing all capability providers...");
        try
        {
            MachineCapabilitySnapshot snapshot = await host.MachineCapabilities.ReadAsync(refresh: true);
            foreach (ICapability capability in snapshot.Values)
            {
                Console.WriteLine(
                    $"  {capability.Id,-44} {capability.Availability,-22} {capability.DisplayValue}"
                    + (capability.Availability == CapabilityAvailability.TemporarilyUnavailable ? $"  [{capability.Reason}]" : ""));
            }

            Console.WriteLine($"snapshot revision={snapshot.Revision} count={snapshot.Values.Count}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"PROBE FAILED: {exception.GetType().Name}: {exception.Message}");
            Console.WriteLine(exception.StackTrace);
            return 1;
        }
    }
}
