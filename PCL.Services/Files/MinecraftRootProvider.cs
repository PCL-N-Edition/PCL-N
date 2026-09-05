namespace PCL.Services.Files;

/// <summary>
/// Resolves the Minecraft installation root. The default is the platform's vanilla launcher
/// location — Windows `%APPDATA%\.minecraft`, Linux `~/.minecraft`,
/// macOS `~/Library/Application Support/minecraft` (no dot prefix) — so composition roots
/// never guess paths themselves. A settings override composes on top of this later.
/// </summary>
public interface IMinecraftRootProvider
{
    string ResolveRoot();
}

public sealed class DefaultMinecraftRootProvider : IMinecraftRootProvider
{
    public string ResolveRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ".minecraft");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "minecraft");
        }

        if (OperatingSystem.IsLinux())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".minecraft");
        }

        throw new PlatformNotSupportedException("This operating system has no default Minecraft root.");
    }
}
