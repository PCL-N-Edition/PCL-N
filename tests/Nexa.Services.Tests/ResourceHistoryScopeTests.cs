using Nexa.Services.Capabilities;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void ResourceHistoryIsolatedByInstanceDirectory()
    {
        var now = DateTimeOffset.UtcNow;
        string first = Path.Combine(Path.GetTempPath(), "nexa-history-a", "versions", "same-name");
        string second = Path.Combine(Path.GetTempPath(), "nexa-history-b", "versions", "same-name");
        var history = new ResourceObservationHistory();
        history.Record(new(first, "Fabric", 21, 20, 0, 1024, 256, 1536, 1800, 128, 1000, now));
        history.Record(new(second, "Fabric", 21, 20, 0, 8192, 256, 9000, 9500, 128, 1000, now));
        history.Record(new("relative/instance", "Fabric", 21, 20, 0, 16384, 256, 17000, 18000, 128, 1000, now));
        var projection = new ResourceEstimatorProjection(history: history);
        Dictionary<string, ICapability> facts = [];
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
