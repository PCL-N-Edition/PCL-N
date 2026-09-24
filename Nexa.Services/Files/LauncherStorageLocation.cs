using System.Text.Json.Nodes;

namespace Nexa.Services.Files;

/// <summary>A small, fixed bootstrap locator. Application data may live elsewhere.</summary>
public static class LauncherStorageLocation
{
    public static string LocatorPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexaCL", "storage.json");

    public static string? Read(string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("数据位置记录过大。");
        var document = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        if (document?["schema"]?.GetValue<int>() != 1 || document["directory"]?.GetValue<string>() is not { Length: > 0 } root || !Path.IsPathFullyQualified(root))
            throw new InvalidDataException("数据位置记录无效，请检查 storage.json。");
        return Path.GetFullPath(root);
    }

    public static void Save(string path, string directory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, new JsonObject { ["schema"] = 1, ["directory"] = Path.GetFullPath(directory) }.ToJsonString());
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
