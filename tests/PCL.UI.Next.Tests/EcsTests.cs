using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.UI.Next.Tests;

internal static partial class Program
{
    private static void EntityCreateDestroyRecyclesHandles()
    {
        XsrUiTree tree = new();
        XsrUiEntityId first = tree.Create("first");
        XsrUiEntityId second = tree.Create("second");

        AssertTrue(first.IsAssigned);
        AssertTrue(second.IsAssigned);
        AssertTrue(tree.IsAlive(first));
        AssertEqual(2, tree.Count);
        AssertEqual("first", tree.Name(first));

        tree.Destroy(first);
        AssertFalse(tree.IsAlive(first));
        AssertEqual(1, tree.Count);
        AssertThrows<InvalidOperationException>(() => tree.Name(first));

        // The index is recycled with an advanced generation: the stale handle stays dead and
        // can never silently resolve to the new entity.
        XsrUiEntityId recycled = tree.Create("recycled");
        AssertEqual(first.Index, recycled.Index);
        AssertTrue(recycled.Generation > first.Generation);
        AssertFalse(first.Equals(recycled));
        AssertFalse(tree.IsAlive(first));
        AssertTrue(tree.IsAlive(recycled));
        AssertEqual("recycled", tree.Name(recycled));
        AssertThrows<InvalidOperationException>(() => tree.Name(first));
    }

    private static void AttachPreservesDeterministicChildOrder()
    {
        XsrUiTree tree = new();
        XsrUiEntityId parent = tree.Create("parent");
        XsrUiEntityId alpha = tree.Create("alpha");
        XsrUiEntityId beta = tree.Create("beta");
        XsrUiEntityId gamma = tree.Create("gamma");

        tree.Attach(alpha, parent);
        tree.Attach(beta, parent);
        tree.Attach(gamma, parent);

        AssertSequence(
            new[] { alpha, beta, gamma },
            tree.Children(parent).ToArray());

        // Re-attaching moves the child to the end.
        tree.Attach(alpha, parent);
        AssertSequence(
            new[] { beta, gamma, alpha },
            tree.Children(parent).ToArray());

        tree.Detach(beta);
        AssertSequence(new[] { gamma, alpha }, tree.Children(parent).ToArray());
        AssertFalse(tree.Parent(beta).IsAssigned);
    }

    private static void AttachRejectsCycles()
    {
        XsrUiTree tree = new();
        XsrUiEntityId root = tree.Create("root");
        XsrUiEntityId child = tree.Create("child");
        XsrUiEntityId grandchild = tree.Create("grandchild");

        tree.Attach(child, root);
        tree.Attach(grandchild, child);

        AssertThrows<InvalidOperationException>(() => tree.Attach(root, child));
        AssertThrows<InvalidOperationException>(() => tree.Attach(root, grandchild));
        AssertThrows<InvalidOperationException>(() => tree.Attach(child, child));
    }

    private static void DestroyReleasesWholeSubtree()
    {
        XsrUiTree tree = new();
        XsrUiEntityId root = tree.Create("root");
        XsrUiEntityId child = tree.Create("child");
        XsrUiEntityId grandchild = tree.Create("grandchild");
        tree.Attach(child, root);
        tree.Attach(grandchild, child);
        tree.SetComponent(child, new XsrUiText("hello"));
        tree.SetComponent(grandchild, new XsrUiStateBinding(default));
        AssertEqual(3, tree.Count);

        tree.Destroy(child);

        AssertFalse(tree.IsAlive(child));
        AssertFalse(tree.IsAlive(grandchild));
        AssertTrue(tree.IsAlive(root));
        AssertEqual(1, tree.Count);
        AssertSequence([], tree.Children(root).ToArray());
    }

    private static void ComponentsSetGetAndRemove()
    {
        XsrUiTree tree = new();
        XsrUiEntityId entity = tree.Create("component-host");

        AssertNull(tree.GetComponent<XsrUiText>(entity));

        XsrUiText text = new("content");
        tree.SetComponent(entity, text);
        AssertTrue(ReferenceEquals(text, tree.GetComponent<XsrUiText>(entity)));
        AssertNull(tree.GetComponent<XsrUiElement>(entity));

        tree.SetComponent<XsrUiText>(entity, null);
        AssertNull(tree.GetComponent<XsrUiText>(entity));

        AssertThrows<InvalidOperationException>(() => tree.GetComponent<XsrUiText>(default));
    }

    private static void ComponentChangesMarkStructureDirty()
    {
        XsrUiTree tree = new();
        XsrUiEntityId entity = tree.Create("host");

        tree.SetComponent(entity, new XsrUiText("one"));
        AssertTrue(tree.DirtyKinds(entity).HasFlag(XsrUiDirtyKinds.Structure));

        tree.ClearDirty(entity);
        AssertEqual(XsrUiDirtyKinds.None, tree.DirtyKinds(entity));

        // Content mutation is direct; the renderer marks paint dirty explicitly.
        tree.GetComponent<XsrUiText>(entity)!.Content = "two";
        AssertEqual(XsrUiDirtyKinds.None, tree.DirtyKinds(entity));
        tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
        AssertTrue(tree.DirtyKinds(entity).HasFlag(XsrUiDirtyKinds.Paint));
    }

    private static void DirtyFlagsBubbleAndClear()
    {
        XsrUiTree tree = new();
        XsrUiEntityId root = tree.Create("root");
        XsrUiEntityId child = tree.Create("child");
        XsrUiEntityId leaf = tree.Create("leaf");
        tree.Attach(child, root);
        tree.Attach(leaf, child);

        // Building the hierarchy marks structure dirty; start the scenario from a clean tree.
        tree.ClearDirty(root);
        tree.ClearDirty(child);
        tree.ClearDirty(leaf);

        AssertFalse(tree.HasDirtySubtree(root));
        tree.MarkDirty(leaf, XsrUiDirtyKinds.Layout);
        AssertTrue(tree.HasDirtySubtree(root));
        AssertTrue(tree.HasDirtySubtree(child));
        AssertTrue(tree.HasDirtySubtree(leaf));
        AssertEqual(XsrUiDirtyKinds.None, tree.DirtyKinds(root));

        tree.ClearDirty(leaf);
        AssertFalse(tree.HasDirtySubtree(root));
        AssertFalse(tree.HasDirtySubtree(child));

        tree.MarkDirty(leaf, XsrUiDirtyKinds.Paint);
        tree.MarkDirty(child, XsrUiDirtyKinds.Layout);
        tree.ClearDirty(leaf);
        AssertTrue(tree.HasDirtySubtree(child));
        AssertTrue(tree.HasDirtySubtree(root));
    }

    private static void DirtyEnumerationIsDeterministic()
    {
        XsrUiTree tree = new();
        XsrUiEntityId first = tree.Create("first");
        XsrUiEntityId second = tree.Create("second");
        tree.MarkDirty(second, XsrUiDirtyKinds.Paint);
        tree.MarkDirty(first, XsrUiDirtyKinds.Layout);

        // Ascending entity-ID order regardless of the order flags were raised.
        AssertSequence(new[] { first, second }, tree.DirtyEntities().ToArray());

        tree.ClearDirty(first);
        AssertSequence(new[] { second }, tree.DirtyEntities().ToArray());
    }

    private static void StateBridgeMarksOnlyBoundEntities()
    {
        XsrStateStoreBuilder states = new();
        states.Cell<int>("ui.progress".AsXsrId(), "Download");

        XsrUiTree tree = new();
        XsrUiEntityId bound = tree.Create("bound");
        XsrUiEntityId unbound = tree.Create("unbound");
        tree.SetComponent(bound, new XsrUiStateBinding(progress_placeholder()));

        XsrUiStateBridge bridge = new(tree);
        XsrStateStore store = states.Build(bridge);
        XsrStateId progress = store.Resolve("ui.progress".AsXsrId());
        tree.SetComponent(bound, new XsrUiStateBinding(progress));
        tree.ClearDirty(bound);
        tree.ClearDirty(unbound);

        // The bridge only enqueues on the publisher thread; the tree is untouched until the
        // render thread drains.
        store.Publish(progress, 42);
        AssertEqual(1, bridge.PendingCount);
        AssertEqual(XsrUiDirtyKinds.None, tree.DirtyKinds(bound));
        bridge.DrainAndMark(store);

        AssertTrue(tree.DirtyKinds(bound).HasFlag(XsrUiDirtyKinds.State));
        AssertEqual(XsrUiDirtyKinds.None, tree.DirtyKinds(unbound));
        AssertEqual(0, bridge.PendingCount);

        // Duplicate notifications coalesce into one pending entry.
        store.Publish(progress, 43);
        store.Publish(progress, 44);
        AssertEqual(1, bridge.PendingCount);
    }

    private static XsrStateId progress_placeholder()
    {
        // A binding needs a state ID before the store exists; it is replaced after the build.
        return default;
    }

    private static void DestroyedEntitiesDropStateDependencies()
    {
        XsrStateStoreBuilder states = new();
        states.Cell<int>("ui.count".AsXsrId(), "Owner");
        XsrStateStore store = states.Build();
        XsrStateId count = store.Resolve("ui.count".AsXsrId());

        XsrUiTree tree = new();
        XsrUiEntityId entity = tree.Create("bound");
        tree.SetComponent(entity, new XsrUiStateBinding(count));

        // Replacing the binding moves the dependency instead of duplicating it.
        tree.SetComponent(entity, new XsrUiStateBinding(count));
        AssertSequence(new[] { entity }, tree.StateDependents(count).ToArray());

        tree.Destroy(entity);
        AssertSequence([], tree.StateDependents(count).ToArray());
    }

    private static void EntitiesCarryMultipleStateBindings()
    {
        XsrStateStoreBuilder states = new();
        states.Cell<string>("account.name".AsXsrId(), "Account");
        states.Cell<bool>("account.valid".AsXsrId(), "Account");
        XsrStateStore store = states.Build();
        XsrStateId name = store.Resolve("account.name".AsXsrId());
        XsrStateId valid = store.Resolve("account.valid".AsXsrId());

        XsrUiTree tree = new();
        XsrUiEntityId entity = tree.Create("row");
        tree.SetComponent(entity, new XsrUiText(string.Empty) { BoundState = name });
        tree.SetComponent(entity, new XsrUiStateBinding(valid, XsrUiStateProperty.Visibility));

        // Text and visibility bindings coexist; neither replaces the other.
        AssertSequence(new[] { entity }, tree.StateDependents(name).ToArray());
        AssertSequence(new[] { entity }, tree.StateDependents(valid).ToArray());

        tree.MarkStateDirty(valid);
        AssertTrue(tree.DirtyKinds(entity).HasFlag(XsrUiDirtyKinds.State));

        // Unbinding the text record keeps the visibility record.
        tree.UnbindState(
            entity,
            new XsrUiStateDependency(
                name,
                XsrUiStateProperty.Text,
                XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint));
        AssertEqual([], tree.StateDependents(name).ToArray());
        AssertSequence(new[] { entity }, tree.StateDependents(valid).ToArray());
    }

    private static void WalkVisitsDepthFirstInOrder()
    {
        XsrUiTree tree = new();
        XsrUiEntityId root = tree.Create("root");
        XsrUiEntityId alpha = tree.Create("alpha");
        XsrUiEntityId beta = tree.Create("beta");
        XsrUiEntityId alphaChild = tree.Create("alpha-child");
        tree.Attach(alpha, root);
        tree.Attach(beta, root);
        tree.Attach(alphaChild, alpha);

        List<XsrUiEntityId> visited = [];
        tree.Walk(root, entity =>
        {
            visited.Add(entity);
            return true;
        });

        AssertSequence(new[] { root, alpha, alphaChild, beta }, visited.ToArray());

        List<XsrUiEntityId> shallow = [];
        tree.Walk(
            root,
            entity =>
            {
                shallow.Add(entity);
                return true;
            },
            _ => false);
        AssertSequence(new[] { root }, shallow.ToArray());
    }

    private static void AssertNull<T>(T? value)
        where T : class
    {
        if (value is not null)
        {
            throw new InvalidOperationException("Expected null but received a value.");
        }
    }
}
