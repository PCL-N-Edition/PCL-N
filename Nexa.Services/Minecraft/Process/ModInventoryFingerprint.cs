using System.Security.Cryptography;
using System.Text;

namespace Nexa.Services.Minecraft.Process;

internal static class ModInventoryFingerprint
{
    public static string? Create(LaunchModInventory inventory)
    {
        if (!inventory.Complete || inventory.UnknownFiles != 0 || inventory.Mods.Count > 4096) return null;
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        List<string> entries = [];
        foreach (var mod in inventory.Mods.Where(static item => item.Enabled))
        {
            if (mod.Version == "unknown" || !mod.DependenciesComplete || mod.Dependencies.Count > 64) return null;
            bytes.SetLength(0);
            bytes.Position = 0;
            writer.Write(mod.Id);
            writer.Write(mod.Version);
            writer.Write(mod.Format);
            foreach (var dependency in mod.Dependencies.OrderBy(static item => item.Key, StringComparer.Ordinal))
            {
                writer.Write(dependency.Key);
                writer.Write(dependency.Value);
            }
            writer.Flush();
            entries.Add(Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length))));
        }
        entries.Sort(StringComparer.Ordinal);
        return "mod-metadata-1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries))));
    }
}
