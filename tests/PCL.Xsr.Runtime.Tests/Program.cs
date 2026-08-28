namespace PCL.Xsr.Runtime.Tests;

internal static class Program
{
    private static readonly (string Name, Action Body)[] TestCases =
    [
        ("semantic identifiers validate without normalization", SemanticIdentifiersValidateWithoutNormalization),
        ("runtime identifier zero remains reserved", RuntimeIdentifierZeroRemainsReserved),
        ("registry assigns deterministic runtime identifiers", RegistryAssignsDeterministicRuntimeIdentifiers),
        ("registry rejects duplicate semantic identifiers", RegistryRejectsDuplicateSemanticIdentifiers),
        ("registry accepts concurrent unique registrations", RegistryAcceptsConcurrentUniqueRegistrations),
        ("registry becomes immutable after sealing", RegistryBecomesImmutableAfterSealing),
        ("snapshot resolves both identifier directions", SnapshotResolvesBothIdentifierDirections),
        ("snapshot supports concurrent numeric reads", SnapshotSupportsConcurrentNumericReads),
        ("snapshot numeric reads allocate no managed memory", SnapshotNumericReadsAllocateNoManagedMemory),
    ];

    private static int Main()
    {
        foreach ((string name, Action body) in TestCases)
        {
            body();
            Console.WriteLine($"PASS: {name}");
        }

        Console.WriteLine($"XSR runtime tests passed: {TestCases.Length}.");
        return 0;
    }

    private static void SemanticIdentifiersValidateWithoutNormalization()
    {
        XsrSemanticId identifier = XsrSemanticId.Parse("Minecraft.Launch_v2");

        AssertEqual("Minecraft.Launch_v2", identifier.Value);
        AssertTrue(identifier.IsAssigned);
        AssertFalse(XsrSemanticId.TryParse(null, out _));
        AssertFalse(XsrSemanticId.TryParse(string.Empty, out _));
        AssertFalse(XsrSemanticId.TryParse("minecraft launch", out _));
        AssertFalse(XsrSemanticId.TryParse("minecraft\u0000launch", out _));
        AssertThrows<ArgumentException>(() => XsrSemanticId.Parse(" minecraft.launch"));
    }

    private static void RuntimeIdentifierZeroRemainsReserved()
    {
        AssertFalse(default(XsrRuntimeId).IsAssigned);
        AssertThrows<ArgumentOutOfRangeException>(() => _ = new XsrRuntimeId(0));

        XsrRuntimeId identifier = new(42);
        AssertTrue(identifier.IsAssigned);
        AssertEqual(42u, identifier.Value);
    }

    private static void RegistryAssignsDeterministicRuntimeIdentifiers()
    {
        XsrSemanticId alpha = XsrSemanticId.Parse("alpha.command");
        XsrSemanticId zeta = XsrSemanticId.Parse("zeta.command");

        XsrRegistry<string> first = new();
        first.Register(zeta, "zeta");
        first.Register(alpha, "alpha");

        XsrRegistry<string> second = new();
        second.Register(alpha, "alpha");
        second.Register(zeta, "zeta");

        XsrRegistrySnapshot<string> firstSnapshot = first.Seal();
        XsrRegistrySnapshot<string> secondSnapshot = second.Seal();

        AssertEqual(
            RequiredRuntimeId(firstSnapshot, alpha),
            RequiredRuntimeId(secondSnapshot, alpha));
        AssertEqual(
            RequiredRuntimeId(firstSnapshot, zeta),
            RequiredRuntimeId(secondSnapshot, zeta));
        AssertEqual(1u, RequiredRuntimeId(firstSnapshot, alpha).Value);
        AssertEqual(2u, RequiredRuntimeId(firstSnapshot, zeta).Value);
    }

    private static void RegistryRejectsDuplicateSemanticIdentifiers()
    {
        XsrRegistry<string> registry = new();
        XsrSemanticId semanticId = XsrSemanticId.Parse("download.start");

        registry.Register(semanticId, "first");
        AssertThrows<InvalidOperationException>(() => registry.Register(semanticId, "second"));
    }

    private static void RegistryAcceptsConcurrentUniqueRegistrations()
    {
        XsrRegistry<int> registry = new();

        Parallel.For(0, 128, index =>
        {
            registry.Register(XsrSemanticId.Parse($"command.{index:D3}"), index);
        });

        XsrRegistrySnapshot<int> snapshot = registry.Seal();
        AssertEqual(128, snapshot.Count);
        AssertTrue(snapshot.TryGet(XsrSemanticId.Parse("command.000"), out XsrRegistryEntry<int> first));
        AssertTrue(snapshot.TryGet(XsrSemanticId.Parse("command.127"), out XsrRegistryEntry<int> last));
        AssertEqual(1u, first.RuntimeId.Value);
        AssertEqual(128u, last.RuntimeId.Value);
    }

    private static void RegistryBecomesImmutableAfterSealing()
    {
        XsrRegistry<string> registry = new();
        registry.Register(XsrSemanticId.Parse("state.publish"), "descriptor");

        XsrRegistrySnapshot<string> first = registry.Seal();
        XsrRegistrySnapshot<string> second = registry.Seal();

        AssertTrue(registry.IsSealed);
        AssertTrue(ReferenceEquals(first, second));
        AssertEqual(1, registry.Count);
        AssertThrows<InvalidOperationException>(() =>
            registry.Register(XsrSemanticId.Parse("state.other"), "other"));
    }

    private static void SnapshotResolvesBothIdentifierDirections()
    {
        XsrSemanticId semanticId = XsrSemanticId.Parse("query.settings");
        XsrRegistry<string> registry = new();
        registry.Register(semanticId, "settings-query");
        XsrRegistrySnapshot<string> snapshot = registry.Seal();

        AssertTrue(snapshot.TryGetRuntimeId(semanticId, out XsrRuntimeId runtimeId));
        AssertTrue(snapshot.TryGet(runtimeId, out XsrRegistryEntry<string> numericEntry));
        AssertTrue(snapshot.TryGet(semanticId, out XsrRegistryEntry<string> semanticEntry));
        AssertEqual("settings-query", numericEntry.Descriptor);
        AssertEqual(numericEntry, semanticEntry);
        AssertFalse(snapshot.TryGet(default(XsrRuntimeId), out _));
        AssertFalse(snapshot.TryGet(new XsrRuntimeId(999), out _));
        AssertFalse(snapshot.TryGet(XsrSemanticId.Parse("query.unknown"), out _));
    }

    private static void SnapshotSupportsConcurrentNumericReads()
    {
        XsrRegistry<string> registry = new();
        registry.Register(XsrSemanticId.Parse("event.completed"), "completed");
        XsrRegistrySnapshot<string> snapshot = registry.Seal();
        XsrRuntimeId runtimeId = RequiredRuntimeId(snapshot, XsrSemanticId.Parse("event.completed"));

        Parallel.For(0, 10_000, _ =>
        {
            if (!snapshot.TryGet(runtimeId, out XsrRegistryEntry<string> entry)
                || entry.Descriptor != "completed")
            {
                throw new InvalidOperationException("A concurrent runtime-ID lookup returned an invalid entry.");
            }
        });
    }

    private static void SnapshotNumericReadsAllocateNoManagedMemory()
    {
        XsrRegistry<string> registry = new();
        registry.Register(XsrSemanticId.Parse("state.progress"), "progress");
        XsrRegistrySnapshot<string> snapshot = registry.Seal();
        XsrRuntimeId runtimeId = RequiredRuntimeId(snapshot, XsrSemanticId.Parse("state.progress"));

        for (int index = 0; index < 1_000; index++)
        {
            _ = snapshot.TryGet(runtimeId, out _);
        }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        uint checksum = 0;
        for (int index = 0; index < 100_000; index++)
        {
            if (!snapshot.TryGet(runtimeId, out XsrRegistryEntry<string> entry))
            {
                throw new InvalidOperationException("The registered runtime ID was not found.");
            }

            checksum ^= entry.RuntimeId.Value;
        }

        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        GC.KeepAlive(checksum);
        AssertEqual(0L, allocatedBytes);
    }

    private static XsrRuntimeId RequiredRuntimeId(
        XsrRegistrySnapshot<string> snapshot,
        XsrSemanticId semanticId)
    {
        if (!snapshot.TryGetRuntimeId(semanticId, out XsrRuntimeId runtimeId))
        {
            throw new InvalidOperationException($"No runtime ID was assigned to '{semanticId}'.");
        }

        return runtimeId;
    }

    private static void AssertTrue(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true but received false.");
        }
    }

    private static void AssertFalse(bool value) => AssertTrue(!value);

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}' but received '{actual}'.");
        }
    }

    private static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name}.");
    }
}
