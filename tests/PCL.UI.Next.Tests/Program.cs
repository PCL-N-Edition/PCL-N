using PCL.Xsr;

namespace PCL.UI.Next.Tests;

internal static partial class Program
{
    private static readonly (string Name, Func<ValueTask> Body)[] TestCases =
    [
        // XSR-201: ECS kernel.
        ("entities create and destroy with recycled handles", Sync(EntityCreateDestroyRecyclesHandles)),
        ("attach preserves deterministic child order", Sync(AttachPreservesDeterministicChildOrder)),
        ("attach rejects cycles and self attachment", Sync(AttachRejectsCycles)),
        ("destroy releases the whole subtree", Sync(DestroyReleasesWholeSubtree)),
        ("components set get and remove", Sync(ComponentsSetGetAndRemove)),
        ("component changes mark structure dirty", Sync(ComponentChangesMarkStructureDirty)),
        ("dirty flags bubble to ancestors and clear precisely", Sync(DirtyFlagsBubbleAndClear)),
        ("dirty enumeration is ascending and deterministic", Sync(DirtyEnumerationIsDeterministic)),
        ("state bridge marks only bound entities dirty", Sync(StateBridgeMarksOnlyBoundEntities)),
        ("destroyed entities drop state dependencies", Sync(DestroyedEntitiesDropStateDependencies)),
        ("walk visits depth first in order", Sync(WalkVisitsDepthFirstInOrder)),
    ];

    private static async Task<int> Main()
    {
        foreach ((string name, Func<ValueTask> body) in TestCases)
        {
            await body().ConfigureAwait(false);
            Console.WriteLine($"PASS: {name}");
        }

        Console.WriteLine($"UI.Next renderer tests passed: {TestCases.Length}.");
        return 0;
    }

    private static Func<ValueTask> Sync(Action action) => () =>
    {
        action();
        return ValueTask.CompletedTask;
    };

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

    private static void AssertSequence<T>(T[] expected, T[] actual)
        where T : IEquatable<T>
    {
        if (expected.Length != actual.Length
            || !expected.Zip(actual, (left, right) => left.Equals(right)).All(equal => equal))
        {
            throw new InvalidOperationException(
                $"Expected sequence [{string.Join(", ", expected)}] but received [{string.Join(", ", actual)}].");
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

    private static XsrSemanticId AsXsrId(this string value) => XsrSemanticId.Parse(value);
}
