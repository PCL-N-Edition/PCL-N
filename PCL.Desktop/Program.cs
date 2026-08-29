using PCL.Services.Accounts;
using PCL.Services.Files;
using PCL.Services.Foundation;
using PCL.Services.Logging;
using PCL.Services.Settings;

namespace PCL.Desktop;

internal static class Program
{
    private static void Main()
    {
        // Composition root: the two-phase foundation composition. Phase one declares every
        // foundation module's state into one shared builder; phase two builds the store once
        // and constructs the services over it. Trim analysis therefore sees the real
        // foundation call graph, not an empty shell.
        AppFolders folders = AppFolders.ResolveDefault();
        folders.EnsureFolder(FolderNames.Logs);
        string settingsFolder = folders.EnsureFolder(FolderNames.Settings);
        string profilesFolder = folders.EnsureFolder(FolderNames.Profiles);

        SettingsSchema settingsSchema = LauncherDefaults.CreateSchema();

        FoundationHost host = FoundationComposer.Compose(
            new LauncherSettingsJsonPort(System.IO.Path.Combine(settingsFolder, "settings.json"), settingsSchema),
            settingsSchema,
            new LaunchProfileFilePort(System.IO.Path.Combine(profilesFolder, "profiles.json")));

        Console.WriteLine($"PCL Nexa foundation composed: {host.Services.Count} services over one host state store.");
    }
}
