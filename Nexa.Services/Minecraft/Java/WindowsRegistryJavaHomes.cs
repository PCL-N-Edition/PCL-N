using System.Runtime.InteropServices;

namespace Nexa.Services.Minecraft.Java;

/// <summary>
/// Collects registered Java homes from the Windows registry (both 64-bit and 32-bit views),
/// walking the vendor keys the legacy launcher walked and reading each version key's
/// <c>JavaHome</c> value. Registry access failures degrade silently — a locked vendor key must
/// not prevent the remaining discovery sources from running.
/// </summary>
internal static class WindowsRegistryJavaHomes
{
    private const int KeyRead = 0x20019;
    private const int Success = 0;

    private static readonly (string Path, int Depth)[] KeyRoots =
    [
        (@"SOFTWARE\JavaSoft", 3),
        (@"SOFTWARE\Eclipse Adoptium", 5),
        (@"SOFTWARE\Microsoft\JDK", 3),
        (@"SOFTWARE\Azul Systems\Zulu", 5),
        (@"SOFTWARE\Azul Systems\Zulu 64-bit", 5),
        (@"SOFTWARE\GraalVM", 4),
        (@"SOFTWARE\BellSoft", 4),
        (@"SOFTWARE\Amazon Corretto", 4),
    ];

    public static IEnumerable<string> EnumerateHomes()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        List<string> homes = [];
        foreach (int view in new[] { KeyWow6464Key, KeyWow6432Key })
        {
            foreach ((string path, int depth) in KeyRoots)
            {
                CollectView(path, view, homes, depth);
            }
        }

        foreach (string home in homes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return home;
        }
    }

    private static void CollectView(string path, int view, List<string> homes, int depth)
    {
        if (RegOpenKeyExW(HkeyLocalMachine, path, 0, KeyRead | view, out nint key) == Success)
        {
            try
            {
                Collect(key, homes, depth);
            }
            finally
            {
                _ = RegCloseKey(key);
            }
        }
    }

    private static void Collect(nint key, List<string> homes, int depth)
    {
        if (TryReadString(key, "JavaHome") is { Length: > 0 } home)
        {
            homes.Add(home);
        }

        if (depth <= 0)
        {
            return;
        }

        foreach (string subKeyName in EnumSubKeyNames(key))
        {
            if (RegOpenKeyExW(key, subKeyName, 0, KeyRead, out nint child) == Success)
            {
                try
                {
                    Collect(child, homes, depth - 1);
                }
                finally
                {
                    _ = RegCloseKey(child);
                }
            }
        }
    }

    private static List<string> EnumSubKeyNames(nint key)
    {
        const int capacity = 260;
        List<string> names = [];
        for (int index = 0; ; index++)
        {
            int length = capacity;
            char[] buffer = new char[capacity];
            int status = RegEnumKeyExW(key, index, buffer, ref length, 0, 0, 0, 0);
            if (status != Success)
            {
                break;
            }

            names.Add(new string(buffer, 0, length));
        }

        return names;
    }

    private static string? TryReadString(nint key, string valueName)
    {
        int status = RegQueryValueExW(key, valueName, 0, out int kind, 0, out int byteLength);
        if (status != Success || byteLength <= 0 || kind != RegSz)
        {
            return null;
        }

        char[] buffer = new char[byteLength / sizeof(char)];
        status = RegQueryValueExW(key, valueName, 0, out _, buffer, ref byteLength);
        if (status != Success)
        {
            return null;
        }

        string value = new(buffer);
        int terminator = value.IndexOf('\0');
        return terminator >= 0 ? value[..terminator] : value;
    }

    private const int HkeyLocalMachine = unchecked((int)0x80000002);
    private const int KeyWow6464Key = 0x0100;
    private const int KeyWow6432Key = 0x0200;
    private const int RegSz = 1;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyExW(
        nint hive, string subKey, int options, int samDesired, out nint key);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(nint key);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueExW(
        nint key, string valueName, int reserved, out int type, int reserved2, out int dataBytes);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueExW(
        nint key, string valueName, int reserved, out int type, char[] data, ref int dataBytes);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegEnumKeyExW(
        nint key, int index, char[] name, ref int nameLength, int reserved, nint className, nint classLength, nint lastWriteTime);
}
