using System.Globalization;
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Process;

namespace Nexa.Services.Telemetry;

internal static class RunDiagnosticTelemetry
{
    internal static readonly string[] NumericFields = ["sequence", "elapsed", "window", "epoch", "java", "classpath", "heap", "samples",
        "working", "peak", "private", "cpu", "threads", "ended", "exit", "render", "simulation", "fps", "graphics", "vsync", "fullscreen"];
    internal static readonly string[] Loaders = ["Vanilla", "OptiFine", "Forge", "NeoForge", "Fabric", "Quilt", "LiteLoader", "Cleanroom", "LabyMod", "Unknown"];
    public static IReadOnlyDictionary<string, string> Sample(JvmRunSample sample)
    {
        double[] values = [sample.Sequence, sample.ElapsedMilliseconds, sample.WindowMilliseconds, sample.ConfigurationEpoch,
            sample.JavaMajor, sample.ClasspathCount, sample.HeapLimitMiB, sample.SampleCount, sample.WorkingMeanMiB,
            sample.WorkingPeakMiB, sample.PrivateMeanMiB, sample.CpuMeanPercent, sample.ThreadsPeak, sample.Ended ? 1 : 0,
            sample.ExitCode ?? -1, sample.Settings.RenderDistance, sample.Settings.SimulationDistance, sample.Settings.MaxFps,
            sample.Settings.Graphics, sample.Settings.Vsync, sample.Settings.Fullscreen];
        var result = new Dictionary<string, string> { ["run"] = sample.SessionId.ToString("N"), ["loader"] = sample.Loader };
        for (int i = 0; i < values.Length; i++) result[NumericFields[i]] = values[i].ToString("G", CultureInfo.InvariantCulture);
        return result;
    }
    public static IEnumerable<IReadOnlyDictionary<string, string>> Inventory(JvmRunContext context)
    {
        JsonObject components = [];
        foreach (var component in context.Components) components[component.Key] = component.Value;
        List<string> pages = []; JsonArray page = []; bool complete = context.Inventory.Complete;
        foreach (var mod in context.Inventory.Mods)
        {
            JsonObject dependencies = [];
            foreach (var dependency in mod.Dependencies.Take(8)) dependencies[dependency.Key] = dependency.Value;
            JsonObject item = new()
            {
                ["id"] = mod.Id,
                ["version"] = mod.Version,
                ["format"] = mod.Format,
                ["enabled"] = mod.Enabled,
                ["dependencies"] = dependencies,
                ["complete"] = mod.DependenciesComplete && mod.Dependencies.Count <= 8
            };
            if ((page.Count >= 24 || page.ToJsonString().Length + item.ToJsonString().Length + 2 > 4096) && page.Count > 0)
            { pages.Add(page.ToJsonString()); page = []; }
            if (pages.Count >= 128) { complete = false; break; }
            page.Add((JsonNode)item);
        }
        if (page.Count > 0 || pages.Count == 0) pages.Add(page.ToJsonString());
        for (int index = 0; index < pages.Count; index++)
            yield return new Dictionary<string, string>
            {
                ["run"] = context.SessionId.ToString("N"),
                ["loader"] = context.Loader,
                ["loader_version"] = context.LoaderVersion,
                ["game"] = context.GameVersion,
                ["components"] = components.ToJsonString(),
                ["page"] = index.ToString(CultureInfo.InvariantCulture),
                ["pages"] = pages.Count.ToString(CultureInfo.InvariantCulture),
                ["unknown"] = context.Inventory.UnknownFiles.ToString(CultureInfo.InvariantCulture),
                ["complete"] = complete ? "1" : "0",
                ["mods"] = pages[index]
            };
    }
    public static bool Valid(string name, IReadOnlyDictionary<string, string> p)
    {
        if (!p.TryGetValue("run", out string? run) || run.Length != 32 || !Guid.TryParseExact(run, "N", out _)
            || !p.TryGetValue("loader", out string? loader) || !Loaders.Contains(loader)) return false;
        if (name == "diagnostic.run") return p.Count == NumericFields.Length + 6
            && NumericFields.All(key => p.TryGetValue(key, out string? value) && double.TryParse(value, CultureInfo.InvariantCulture, out double n)
                && double.IsFinite(n) && n >= -2147483648 && n <= 1e12);
        if (p.Count != 14 || !p.TryGetValue("game", out string? game) || !LaunchModInventoryReader.SafeGameVersion(game)
            || !p.TryGetValue("components", out string? components) || components.Length > 2048
            || !p.TryGetValue("loader_version", out string? version) || !LaunchModInventoryReader.SafeVersion(version)
            || !p.TryGetValue("mods", out string? mods) || mods.Length > 4096
            || !p.TryGetValue("complete", out string? complete) || complete is not ("0" or "1")) return false;
        foreach (string key in new[] { "page", "pages", "unknown" })
            if (!p.TryGetValue(key, out string? value) || !int.TryParse(value, out int number) || number < 0 || number > 4096) return false;
        try
        {
            if (JsonNode.Parse(components) is not JsonObject builds || builds.Count > 16 || builds.Any(pair =>
                !Enum.TryParse<Nexa.Services.Minecraft.Install.InstallLoader>(pair.Key, out _) || pair.Value is not JsonValue build
                || !build.TryGetValue<string>(out string? text) || !LaunchModInventoryReader.SafeVersion(text))) return false;
            return JsonNode.Parse(mods) is JsonArray items && items.Count <= 32 && items.All(node =>
                node is JsonObject item && item.Count == 6
                && item["id"] is JsonValue id && id.TryGetValue<string>(out string? idText) && LaunchModInventoryReader.SafeId(idText)
                && item["version"] is JsonValue v && v.TryGetValue<string>(out string? vText) && LaunchModInventoryReader.SafeVersion(vText)
                && item["format"]?.ToString() is "fabric.mod.json" or "quilt.mod.json" or "META-INF/mods.toml" or "META-INF/neoforge.mods.toml" or "mcmod.info"
                && item["enabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out _)
                && item["complete"] is JsonValue full && full.TryGetValue<bool>(out _)
                && item["dependencies"] is JsonObject dependencies && dependencies.Count <= 8
                && dependencies.All(pair => LaunchModInventoryReader.SafeId(pair.Key) && pair.Value is JsonValue range
                    && range.TryGetValue<string>(out string? text) && LaunchModInventoryReader.SafeRange(text)));
        }
        catch (System.Text.Json.JsonException) { return false; }
    }
}
