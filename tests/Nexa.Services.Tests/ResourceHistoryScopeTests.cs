using Nexa.Services.Capabilities;
using Nexa.Services.Minecraft.Process;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void ResourceHistoryRequiresMatchingPersistedSettings()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nexa-options-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            AssertTrue(GameOptionsFingerprint.Read(directory) is null);
            string path = Path.Combine(directory, "options.txt");
            File.WriteAllText(path, "renderDistance:8\nsimulationDistance:6\n");
            string? first = GameOptionsFingerprint.Read(directory);
            AssertTrue(first is not null);
            File.WriteAllText(path, "renderDistance:24\nsimulationDistance:6\n");
            string? second = GameOptionsFingerprint.Read(directory);
            AssertTrue(second is not null && second != first);
            var now = DateTimeOffset.UtcNow;
            var history = new ResourceObservationHistory();
            history.Record(new(directory, "Fabric", 21, 1, 0, 1024, 0, 2048, 0, 0, 1000, now)
            { ModFingerprint = "same-mods", SettingsFingerprint = first });
            var projection = new ResourceEstimatorProjection(history: history);
            Dictionary<string, ICapability> facts = new()
            {
                [ModCatalog.ModMetadataFingerprint.Id] = ModCatalog.ModMetadataFingerprint.Observe("same-mods", now, "fixture"),
                [ResourceHistoryCatalog.SettingsFingerprint.Id] = ResourceHistoryCatalog.SettingsFingerprint.Observe(second!, now, "fixture"),
            };
            long Count() => ((Capability<long>)projection.Project(facts, now, new(directory))
                .Single(item => item.Id == "estimate.history.sample_count")).Value;
            AssertEqual(0L, Count());
            facts[ResourceHistoryCatalog.SettingsFingerprint.Id] = ResourceHistoryCatalog.SettingsFingerprint.Observe(first!, now, "fixture");
            AssertEqual(1L, Count());
            facts.Remove(ResourceHistoryCatalog.SettingsFingerprint.Id);
            AssertEqual(0L, Count());
            File.WriteAllBytes(path, new byte[65536]);
            AssertTrue(GameOptionsFingerprint.Read(directory) is not null);
            File.WriteAllBytes(path, new byte[65537]);
            AssertTrue(GameOptionsFingerprint.Read(directory) is null);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void ResourceHistoryRequiresMatchingModIdentity()
    {
        var now = DateTimeOffset.UtcNow;
        string directory = Path.Combine(Path.GetTempPath(), "nexa-mod-history");
        LaunchModIdentity optimization = new("optimizer", "1.0", "fabric.mod.json", true,
            new Dictionary<string, string> { ["minecraft"] = "1.21.1", ["fabricloader"] = ">=0.16" }, true);
        LaunchModIdentity terrain = optimization with { Id = "terrain" };
        string Key(params LaunchModIdentity[] mods) => ModInventoryFingerprint.Create(new(mods, 0, true))!;
        string first = Key(optimization), second = Key(terrain);
        AssertFalse(first == second);
        AssertFalse(first == Key(optimization with { Version = "2.0" }));
        AssertEqual(Key(optimization, terrain), Key(terrain, optimization));
        AssertEqual(first, Key(optimization, terrain with { Enabled = false }));
        AssertEqual(first, Key(optimization with
        {
            Dependencies = new Dictionary<string, string>
            { ["fabricloader"] = ">=0.16", ["minecraft"] = "1.21.1" }
        }));
        AssertFalse(first == Key(optimization with { Dependencies = new Dictionary<string, string> { ["minecraft"] = "1.20.1" } }));
        AssertTrue(ModInventoryFingerprint.Create(new([optimization], 1, false)) is null);
        AssertTrue(ModInventoryFingerprint.Create(new([optimization with { Version = "unknown" }], 0, true)) is null);
        AssertTrue(ModInventoryFingerprint.Create(new([optimization with { DependenciesComplete = false }], 0, true)) is null);
        var history = new ResourceObservationHistory();
        history.Record(new(directory, "Fabric", 21, 1, 0, 1024, 0, 1500, 0, 0, 1000, now) { SettingsFingerprint = "options-fixture", ModFingerprint = first });
        history.Record(new(directory, "Fabric", 21, 1, 0, 8192, 0, 9000, 0, 0, 1000, now) { SettingsFingerprint = "options-fixture", ModFingerprint = second });
        history.Record(new(directory, "Fabric", 21, 1, 0, 16384, 0, 17000, 0, 0, 1000, now));
        var projection = new ResourceEstimatorProjection(history: history);
        Dictionary<string, ICapability> facts = new() { [ResourceHistoryCatalog.SettingsFingerprint.Id] = ResourceHistoryCatalog.SettingsFingerprint.Observe("options-fixture", now, "fixture"), [ModCatalog.ModMetadataFingerprint.Id] = ModCatalog.ModMetadataFingerprint.Observe(first, now, "fixture") };
        long Read(string id) => ((Capability<long>)projection.Project(facts, now, new(directory)).Single(item => item.Id == id)).Value;
        AssertEqual(1L, Read("estimate.history.sample_count"));
        AssertEqual(1024L, Read("estimate.history.heap.p95"));
        facts[ModCatalog.ModMetadataFingerprint.Id] = ModCatalog.ModMetadataFingerprint.Observe(second, now, "fixture");
        AssertEqual(8192L, Read("estimate.history.heap.p95"));
        facts.Clear();
        AssertEqual(0L, Read("estimate.history.sample_count"));
    }

    private static void ResourceHistoryIsolatedByInstanceDirectory()
    {
        var now = DateTimeOffset.UtcNow;
        string first = Path.Combine(Path.GetTempPath(), "nexa-history-a", "versions", "same-name");
        string second = Path.Combine(Path.GetTempPath(), "nexa-history-b", "versions", "same-name");
        var history = new ResourceObservationHistory();
        history.Record(new(first, "Fabric", 21, 20, 0, 1024, 256, 1536, 1800, 128, 1000, now) { SettingsFingerprint = "options-fixture", ModFingerprint = "fixture" });
        history.Record(new(second, "Fabric", 21, 20, 0, 8192, 256, 9000, 9500, 128, 1000, now) { SettingsFingerprint = "options-fixture", ModFingerprint = "fixture" });
        history.Record(new("relative/instance", "Fabric", 21, 20, 0, 16384, 256, 17000, 18000, 128, 1000, now) { SettingsFingerprint = "options-fixture", ModFingerprint = "fixture" });
        var projection = new ResourceEstimatorProjection(history: history);
        Dictionary<string, ICapability> facts = new() { [ResourceHistoryCatalog.SettingsFingerprint.Id] = ResourceHistoryCatalog.SettingsFingerprint.Observe("options-fixture", now, "fixture"), [ModCatalog.ModMetadataFingerprint.Id] = ModCatalog.ModMetadataFingerprint.Observe("fixture", now, "fixture") };
        long Read(MachineCapabilityQuery query, string key) => ((Capability<long>)projection.Project(facts, now, query)
            .Single(value => value.Id == key)).Value;
        var selected = new MachineCapabilityQuery(first);
        AssertEqual(1L, Read(selected, "estimate.history.sample_count"));
        AssertEqual(1024L, Read(selected, "estimate.history.heap.p95"));
        AssertEqual(8192L, Read(new(second), "estimate.history.heap.p95"));
        AssertEqual(1024L, Read(new(first + Path.DirectorySeparatorChar + "."), "estimate.history.heap.p95"));
        foreach (var query in new[] { new MachineCapabilityQuery(), new(InstanceId: "same-name"), new("relative/instance"), new(first + "-other") })
            AssertEqual(0L, Read(query, "estimate.history.sample_count"));
    }
}
