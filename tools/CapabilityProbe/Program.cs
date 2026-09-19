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
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--display"))
        {
            for (int round = 0; round < 20; round++)
            {
                Console.WriteLine($"round {round}...");
                var provider = new DisplayCapabilityProvider();
                foreach (ICapability fact in await provider.CollectAsync(DateTimeOffset.UtcNow, CancellationToken.None))
                {
                    Console.WriteLine($"  {fact.Id} {fact.Availability} {fact.DisplayValue} [{fact.Reason}]");
                }
            }

            return 0;
        }

        if (args.Contains("--parallel"))
        {
            // Reproduce the broker's fan-out: GPU + Java + Display concurrently, 20 times.
            for (int round = 0; round < 20; round++)
            {
                Console.WriteLine($"round {round}...");
                var displayTask = new DisplayCapabilityProvider().CollectAsync(DateTimeOffset.UtcNow, CancellationToken.None);
                var gpuTask = Task.Run(() => GpuProbes.CollectGpu(DateTimeOffset.UtcNow));
                var javaTask = Task.Run(async () => await MachineInstanceCatalog.CollectJavaAsync(
                    new Nexa.Services.Minecraft.Java.LocalJavaRuntimeLocator(), DateTimeOffset.UtcNow, CancellationToken.None));
                await Task.WhenAll(
                    displayTask.AsTask(),
                    gpuTask,
                    javaTask);
                Console.WriteLine("  all providers survived");
            }

            return 0;
        }

        if (args.Contains("--gpu"))
        {
            // Isolate the hand-rolled COM path: run the GPU probe repeatedly to expose
            // nondeterministic native crashes.
            for (int round = 0; round < 20; round++)
            {
                Console.WriteLine($"round {round}...");
                foreach (ICapability fact in GpuProbes.CollectGpu(DateTimeOffset.UtcNow))
                {
                    Console.WriteLine($"  {fact.Id} {fact.Availability} {fact.DisplayValue} [{fact.Reason}]");
                }
            }

            return 0;
        }

        if (args.Contains("--java"))
        {
            for (int round = 0; round < 10; round++)
            {
                Console.WriteLine($"round {round}...");
                foreach (ICapability fact in await MachineInstanceCatalog.CollectJavaAsync(
                    new Nexa.Services.Minecraft.Java.LocalJavaRuntimeLocator(), DateTimeOffset.UtcNow, CancellationToken.None))
                {
                    Console.WriteLine($"  {fact.Id} {fact.Availability} {fact.DisplayValue}");
                }
            }

            return 0;
        }

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
                    + (capability.Reason.Length > 0 ? $"  [{capability.Reason}]" : ""));
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
