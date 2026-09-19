using Nexa.Services.Accounts;
using Nexa.Services.Capabilities;
using Nexa.Services.Foundation;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Settings;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

// C1+C2 contract tests: environment projections answer from the real services (loader from
// the manifest chain, account from the roster, settings from options.txt, java compatibility
// from the XSR-608 requirement machinery), and the broker derivation pass computes
// cross-provider facts with honest DependencyMissing when inputs are gone.
internal static partial class Program
{
    private static async ValueTask EnvironmentProjectionsAnswerForThePrimaryInstance()
    {
        string root = Path.Combine(Path.GetTempPath(), "nexa-cap-proj", Guid.NewGuid().ToString("N"));
        string versions = Path.Combine(root, "versions", "fabric-test");
        Directory.CreateDirectory(versions);
        await File.WriteAllTextAsync(Path.Combine(versions, "fabric-test.json"), """
            {
              "id": "fabric-test",
              "inheritsFrom": "1.20.1",
              "releaseTime": "2026-09-19T00:00:00Z",
              "time": "2026-09-19T00:00:00Z",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
              "libraries": [ { "name": "net.fabricmc:fabric-loader:0.16.9" } ]
            }
            """);
        Directory.CreateDirectory(Path.Combine(root, "versions", "1.20.1"));
        await File.WriteAllTextAsync(Path.Combine(root, "versions", "1.20.1", "1.20.1.json"), """
            { "id": "1.20.1", "javaVersion": { "component": "java-runtime-gamma", "majorVersion": 17 } }
            """);
        string newer = Path.Combine(root, "versions", "newer-vanilla");
        Directory.CreateDirectory(newer);
        await File.WriteAllTextAsync(Path.Combine(newer, "newer-vanilla.json"), """
            { "id": "newer-vanilla", "releaseTime": "2027-01-01T00:00:00Z", "mainClass": "net.minecraft.client.main.Main" }
            """);
        await File.WriteAllTextAsync(Path.Combine(versions, "options.txt"), "renderDistance:8\ngraphicsMode:fast\n");

        LoaderCapabilityProvider loader = new(root);
        MachineCapabilityQuery scope = new(InstanceDirectory: versions, InstanceId: "fabric-test");
        IReadOnlyList<ICapability> loaderFacts = await loader.CollectAsync(DateTimeOffset.UtcNow, scope, CancellationToken.None);
        AssertEqual("Fabric", loaderFacts.Single(fact => fact.Id == "loader.type").DisplayValue);
        AssertEqual("是", loaderFacts.Single(fact => fact.Id == "loader.present").DisplayValue);
        AssertEqual("否", loaderFacts.Single(fact => fact.Id == "loader.derived.missing").DisplayValue);

        // Settings read from the isolated instance's options.txt (isolation defaults on).
        MinecraftEnvironmentCapabilityProvider minecraft = new(root);
        IReadOnlyList<ICapability> minecraftFacts = await minecraft.CollectAsync(DateTimeOffset.UtcNow, scope, CancellationToken.None);
        AssertEqual("是", minecraftFacts.Single(fact => fact.Id == "minecraft.settings.readable").DisplayValue);
        AssertEqual("8", minecraftFacts.Single(fact => fact.Id == "minecraft.settings.render_distance").DisplayValue);
        AssertEqual("fast", minecraftFacts.Single(fact => fact.Id == "minecraft.settings.graphics_mode").DisplayValue);

        // Java requirement from the manifest chain: the parent pins Java 17+.
        IReadOnlyList<ICapability> javaFacts = await JavaCompatibilityProjection.CollectAsync(
            new NoJavaLocator(), root, scope, DateTimeOffset.UtcNow, CancellationToken.None);
        AssertEqual("17.0", javaFacts.Single(fact => fact.Id == "java.requirement.minimum").DisplayValue);

        Directory.Delete(root, recursive: true);
    }

    private sealed class NoJavaLocator : IJavaRuntimeLocator
    {
        public ValueTask<IReadOnlyList<JavaRuntimeCandidate>> FindAllAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<JavaRuntimeCandidate>>([]);

        public ValueTask<JavaRuntimeCandidate?> InspectAsync(string javaPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("fixture");
    }

    private static async ValueTask AccountProjectionReadsTheRoster()
    {
        SettingsSchema schema = LauncherDefaults.CreateSchema();
        FoundationHost host = FoundationComposer.Compose(
            new InMemorySettingsPort(), schema,
            new LaunchProfileFilePort(Path.Combine(Path.GetTempPath(), "nexa-cap-acct", Guid.NewGuid().ToString("N"), "profiles.json")));
        _ = host.Accounts.AddProfile(new LaunchProfile { Username = "Steve", Kind = LaunchProfileKind.Offline }).IsSuccess;
        _ = host.Accounts.AddProfile(new LaunchProfile { Username = "Alex", Kind = LaunchProfileKind.Microsoft }).IsSuccess;

        AccountCapabilityProvider provider = new(host.Accounts);
        IReadOnlyList<ICapability> facts = await provider.CollectAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        AssertEqual("是", facts.Single(fact => fact.Id == "account.available").DisplayValue);
        AssertEqual("Steve", facts.Single(fact => fact.Id == "account.selected").DisplayValue);
        AssertEqual("Offline", facts.Single(fact => fact.Id == "account.authentication.provider").DisplayValue);
        AssertEqual("否", facts.Single(fact => fact.Id == "account.authentication.required").DisplayValue);
        AssertEqual("是", facts.Single(fact => fact.Id == "account.authentication.valid").DisplayValue);
    }

    private static async ValueTask DerivationPassComputesCrossProviderFacts()
    {
        var nativeArch = new CapabilityDefinition<string>("platform.arch.native", "", "", "runtime");
        var processArch = new CapabilityDefinition<string>("platform.arch.process", "", "", "runtime");
        var usable = new CapabilityDefinition<long>("memory.physical.usable", "", "", "memory");
        var commitTotal = new CapabilityDefinition<long>("memory.commit.total", "", "", "memory");
        var commitLimit = new CapabilityDefinition<long>("memory.commit.limit", "", "", "memory");
        var commitAvailable = new CapabilityDefinition<long>("memory.commit.available", "", "", "memory");
        var physicalAvailable = new CapabilityDefinition<long>("memory.physical.available", "", "", "memory");
        var installed = new CapabilityDefinition<bool>("java.installed", "", "", "java");
        var hardCompat = new CapabilityDefinition<bool>("java.compatibility.hard", "", "", "java");
        CapabilityRegistry registry = new([
            .. MachineDerivedRules.Definitions(),
            nativeArch, processArch, usable, physicalAvailable, commitTotal, commitLimit, commitAvailable, installed, hardCompat,
        ]);
        var runtime = new ProbeProvider("runtime", (timestamp, _) => ValueTask.FromResult<IReadOnlyList<ICapability>>(
        [
            nativeArch.Observe("X64", timestamp, "fixture"),
            processArch.Observe("X64", timestamp, "fixture"),
        ]));
        var memory = new ProbeProvider("memory", (timestamp, _) => ValueTask.FromResult<IReadOnlyList<ICapability>>(
        [
            usable.Observe(2L * 1024 * 1024 * 1024, timestamp, "fixture"),
            commitAvailable.Unavailable(CapabilityAvailability.PlatformUnsupported, timestamp, "fixture platform"),
        ]));
        var java = new ProbeProvider("java", (timestamp, _) => ValueTask.FromResult<IReadOnlyList<ICapability>>(
        [
            installed.Observe(true, timestamp, "fixture"),
            hardCompat.Observe(false, timestamp, "fixture"),
        ]));
        var builder = new XsrStateStoreBuilder();
        MachineCapabilityStateContract.DeclareState(builder);
        MachineCapabilityBroker broker = new(registry, [runtime, memory, java], builder.Build(),
            derivations: MachineDerivedRules.Defaults());
        MachineCapabilitySnapshot snapshot = await broker.ReadAsync(refresh: true);

        AssertTrue(snapshot.Get<bool>("platform.compatibility.native_execution")!.Value);
        AssertFalse(snapshot.Get<bool>("platform.compatibility.emulated_execution")!.Value);
        // 2 GiB usable trips machine.memory.low but not constrained/abundant.
        AssertTrue(snapshot.Get<bool>("machine.memory.low")!.Value);
        AssertFalse(snapshot.Get<bool>("machine.memory.constrained")!.Value);
        AssertFalse(snapshot.Get<bool>("machine.memory.abundant")!.Value);
        AssertEqual(CapabilityAvailability.DependencyMissing, snapshot.Get<bool>("memory.derived.commit_low")!.Availability);
        AssertFalse(snapshot.Get<bool>("java.derived.missing")!.Value);
        AssertTrue(snapshot.Get<bool>("java.derived.hard_incompatible")!.Value);

        // With memory gone entirely the derived facts degrade honestly.
        var noMemory = new ProbeProvider("memory", (_, _) => throw new IOException("fixture"));
        builder = new XsrStateStoreBuilder();
        MachineCapabilityStateContract.DeclareState(builder);
        broker = new(registry, [runtime, noMemory, java], builder.Build(), derivations: MachineDerivedRules.Defaults());
        snapshot = await broker.ReadAsync(refresh: true);
        AssertEqual(CapabilityAvailability.DependencyMissing, snapshot.Get<bool>("machine.memory.low")!.Availability);
        AssertTrue(snapshot.Get<bool>("java.derived.hard_incompatible")!.Value);
    }

    private static async ValueTask FormFactorHeuristicClassifiesTheDevice()
    {
        // Laptop: battery + internal panel, no touch-first.
        FormFactorCapabilityProvider laptop = new(batteryPresent: () => true, internalDisplay: () => true,
            touchAvailable: () => false, keyboardAvailable: () => true, controllerAvailable: () => false);
        IReadOnlyList<ICapability> facts = await laptop.CollectAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        AssertEqual("Laptop", facts.Single(fact => fact.Id == "formfactor.type").DisplayValue);
        AssertEqual("否", facts.Single(fact => fact.Id == "formfactor.handheld").DisplayValue);

        // Handheld: battery + touch + controller-first (no internal keyboard assumption).
        FormFactorCapabilityProvider handheld = new(batteryPresent: () => true, internalDisplay: () => true,
            touchAvailable: () => true, keyboardAvailable: () => false, controllerAvailable: () => true);
        facts = await handheld.CollectAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        AssertEqual("Handheld", facts.Single(fact => fact.Id == "formfactor.type").DisplayValue);

        // Desktop: no battery.
        FormFactorCapabilityProvider desktop = new(batteryPresent: () => false, internalDisplay: () => false,
            touchAvailable: () => false, keyboardAvailable: () => true, controllerAvailable: () => false);
        facts = await desktop.CollectAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        AssertEqual("Desktop", facts.Single(fact => fact.Id == "formfactor.type").DisplayValue);
        AssertEqual("否", facts.Single(fact => fact.Id == "formfactor.portable").DisplayValue);
    }
}
