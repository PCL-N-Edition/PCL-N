using Nexa.Services.Capabilities;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void RejectCapability(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Invalid capability contract accepted.");
    }
    private static void MachineRegistrySealsTypedDependencyGraph()
    {
        var a = new CapabilityDefinition<int>("cpu.test.a", "A", "CPU", "test");
        var b = new CapabilityDefinition<int>("cpu.test.b", "B", "CPU", "test", requirements: [a.Id]);
        var registry = new CapabilityRegistry([b, a]);
        AssertEqual(a.Id, registry.Definitions[0].Id);
        RejectCapability(() => _ = new CapabilityRegistry([a, a]));
        RejectCapability(() => _ = new CapabilityRegistry([b]));
        RejectCapability(() => _ = new CapabilityRegistry([new CapabilityDefinition<int>("cpu.cycle", "", "", "test", requirements: ["cpu.cycle"])]));
        RejectCapability(() => _ = new CapabilityDefinition<int>("bogus.test", "", "", "test"));
        var now = DateTimeOffset.UtcNow;
        List<ICapability> input = [a.Observe(1, now, "test")];
        var snapshot = new MachineCapabilitySnapshot(1, now, input); input.Clear();
        AssertEqual(1, snapshot.Get<int>(a.Id)!.Value);
        AssertTrue(snapshot.Get<string>(a.Id) is null);
        AssertFalse(a.Accepts(new CapabilityDefinition<string>(a.Id, "", "", "test").Observe("wrong", now, "test")));
    }
    private sealed class ProbeProvider(string id, Func<DateTimeOffset, CancellationToken, ValueTask<IReadOnlyList<ICapability>>> collect) : IMachineCapabilityProvider
    {
        public string Id => id;
        public ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken) => collect(timestamp, cancellationToken);
    }
    private static MachineCapabilityBroker CapabilityBroker(CapabilityRegistry registry, params IMachineCapabilityProvider[] providers)
    {
        var builder = new XsrStateStoreBuilder(); MachineCapabilityStateContract.DeclareState(builder);
        return new(registry, providers, builder.Build(), timeout: TimeSpan.FromMilliseconds(300));
    }
    private static async ValueTask MachineBrokerCoalescesIsolatesAndCaches()
    {
        var fact = new CapabilityDefinition<int>("cpu.test", "", "", "good");
        var failed = new CapabilityDefinition<int>("memory.test", "", "", "bad");
        var dependent = new CapabilityDefinition<int>("machine.test", "", "", "good", CapabilityKind.Derived, requirements: [failed.Id]);
        int calls = 0;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var good = new ProbeProvider("good", async (timestamp, token) =>
        {
            Interlocked.Increment(ref calls); await release.Task.WaitAsync(token);
            return Array.AsReadOnly<ICapability>([fact.Observe(42, timestamp, "fixture"), dependent.Observe(1, timestamp, "fixture")]);
        });
        var bad = new ProbeProvider("bad", (_, _) => throw new IOException("fixture"));
        var broker = CapabilityBroker(new([fact, failed, dependent]), good, bad);
        var first = broker.ReadAsync();
        using var cancel = new CancellationTokenSource();
        var canceled = broker.ReadAsync(cancellationToken: cancel.Token); cancel.Cancel();
        try { await canceled; throw new InvalidOperationException("Cancellation ignored."); } catch (OperationCanceledException) { }
        var second = broker.ReadAsync(refresh: true); release.SetResult();
        var result = await first; AssertTrue(ReferenceEquals(result, await second));
        AssertTrue(ReferenceEquals(result, await broker.ReadAsync())); AssertEqual(1, calls);
        AssertEqual(42, result.Get<int>(fact.Id)!.Value);
        AssertEqual(CapabilityAvailability.TemporarilyUnavailable, result.Get<int>(failed.Id)!.Availability);
        AssertEqual(CapabilityAvailability.DependencyMissing, result.Get<int>(dependent.Id)!.Availability);
        AssertEqual(2L, (await broker.ReadAsync(refresh: true)).Revision); AssertEqual(2, calls);
        var foreign = new ProbeProvider("bad", (time, _) => ValueTask.FromResult<IReadOnlyList<ICapability>>([fact.Observe(99, time, "wrong owner")]));
        result = await CapabilityBroker(new([fact, failed]), foreign).ReadAsync();
        AssertEqual(CapabilityAvailability.NotImplemented, result.Get<int>(fact.Id)!.Availability);
        AssertEqual(CapabilityAvailability.TemporarilyUnavailable, result.Get<int>(failed.Id)!.Availability);
        var slow = new ProbeProvider("bad", async (_, token) => { await Task.Delay(10000, token); return Array.Empty<ICapability>(); });
        result = await CapabilityBroker(new([failed]), slow).ReadAsync();
        AssertEqual(CapabilityAvailability.TemporarilyUnavailable, result.Get<int>(failed.Id)!.Availability);

        var scopedBuilder = new XsrStateStoreBuilder();
        MachineCapabilityStateContract.DeclareState(scopedBuilder);
        XsrStateStore scopedStore = scopedBuilder.Build();
        var scopedBroker = new MachineCapabilityBroker(new([fact]), [new ProbeProvider("good", (time, _) =>
            ValueTask.FromResult<IReadOnlyList<ICapability>>([fact.Observe(1, time, "fixture")]))], scopedStore);
        await scopedBroker.ReadAsync(refresh: true);
        var revisionId = scopedStore.Resolve(MachineCapabilityStateContract.RevisionKey);
        long globalRevision = scopedStore.Read<long>(revisionId).Value;
        await scopedBroker.ReadAsync(new MachineCapabilityQuery("instance", "fixture", "root"));
        AssertEqual(globalRevision, scopedStore.Read<long>(revisionId).Value);
    }
    private static void MachineMemoryAndPreflightKeepIndependentSemantics()
    {
        var values = MemoryCapabilityProvider.ParseLinux("MemTotal: 100 kB\nMemAvailable: 25 kB\nCommitted_AS: 150 kB\nCommitLimit: 200 kB\n", DateTimeOffset.UtcNow);
        var snapshot = new MachineCapabilitySnapshot(1, DateTimeOffset.UtcNow, values);
        AssertEqual(25L * 1024, snapshot.Get<long>("memory.physical.available")!.Value);
        AssertEqual(50L * 1024, snapshot.Get<long>("memory.commit.available")!.Value);
        RejectCapability(() => _ = new CapabilityPreflightIssue("estimate", PreflightSeverity.Blocked, "memory", PreflightCertainty.Estimated, true, ["estimate.heap.launch"]));
        RejectCapability(() => _ = new CapabilityPreflightIssue("estimate", PreflightSeverity.Blocked, "memory", PreflightCertainty.Verified, false, ["estimate.heap.launch"]));
        var information = new CapabilityPreflightIssue("privacy", PreflightSeverity.Information, "privacy", PreflightCertainty.Verified, false, []);
        AssertEqual(PreflightSeverity.None, CapabilityPreflightIssue.OverallSeverity([information]));
        var blocked = new CapabilityPreflightIssue("java", PreflightSeverity.Blocked, "java", PreflightCertainty.Verified, true, ["java.requirement.minimum"]);
        AssertEqual(PreflightSeverity.Blocked, CapabilityPreflightIssue.OverallSeverity([information, blocked]));
    }
}
