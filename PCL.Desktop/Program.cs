using PCL.Desktop.Ui;
using PCL.Services.Accounts;
using PCL.Services.Composition;
using PCL.Services.Files;
using PCL.Services.Foundation;
using PCL.Services.Minecraft;
using PCL.Services.Settings;
using PCL.UI.Next;
using PCL.UI.Next.Backend.Avalonia;

namespace PCL.Desktop;

internal static class Program
{
    private static int Main(string[] args)
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
        FoundationRuntime runtime = FoundationRuntimeComposer.Compose(host);
        MinecraftRuntime minecraft = MinecraftRuntimeComposer.Compose(hostStore: runtime.Host.StateStore);

        XsrUiShell shell = PxmlShellComposer.Compose(
            runtime.Host.StateStore,
            new XsrUiShellOptions
            {
                Style = ResolveShellStyle(args),
                Title = "PCL Nexa",
                Version = "2.0.0.alpha.1",
            });

        Console.WriteLine(
            $"PCL Nexa foundation composed: {runtime.Host.Services.Count} services, "
            + $"{runtime.Commands.Count} command routes, {runtime.Queries.Count} query routes over one host state store; "
            + $"Minecraft routes: {minecraft.Commands.Count} commands/{minecraft.Queries.Count} queries; "
            + $"UI style: {shell.Style}.");
        if (args.Any(argument => string.Equals(argument, "--validate-shell", StringComparison.OrdinalIgnoreCase)))
        {
            XsrUiScene scene = shell.Render(new XsrUiSize(1280, 800));
            Console.WriteLine($"PXML shell validated: {scene.Count} semantic nodes.");
            return 0;
        }

        return AvaloniaUiShellHost.Run(shell, args);
    }

    private static XsrUiShellStyle ResolveShellStyle(string[] args) =>
        args.Any(argument =>
            string.Equals(argument, "--ui-style=liquid-glass", StringComparison.OrdinalIgnoreCase)
            || string.Equals(argument, "--liquid-glass", StringComparison.OrdinalIgnoreCase))
            ? XsrUiShellStyle.LiquidGlass
            : XsrUiShellStyle.Experimental;
}
