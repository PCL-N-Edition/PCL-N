using System.Diagnostics;
using System.Globalization;
using PCL.Xsr.State;
using PCL.UI.Next;

namespace PCL.UI.Next.Benchmarks;

/// <summary>
/// The deterministic performance gate for the UI.Next renderer kernel. Timing numbers are
/// reported informationally; the enforced gates are correctness and allocation invariants that
/// must hold on every machine: clean re-renders allocate nothing, dirty relayout stays bounded,
/// and the produced scene is exactly the entity tree.
/// </summary>
internal static class Program
{
    private static int _failures;

    public static int Main()
    {
        RunCleanRenderAllocatesNothing();
        RunDirtyLeafRelayoutStaysBounded();
        RunSceneMatchesEntityTree();
        RunRenderThroughputReport();

        if (_failures > 0)
        {
            Console.Error.WriteLine($"UI.Next benchmark gate failed with {_failures} violation(s).");
            return 1;
        }

        Console.WriteLine("UI.Next benchmark gate passed.");
        return 0;
    }

    private static void RunCleanRenderAllocatesNothing()
    {
        XsrUiRenderer renderer = BuildGridRenderer(20, 25, out XsrUiTree tree, out _);
        _ = renderer.Render();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        XsrUiScene scene = renderer.Render();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Gate(
            allocated == 0 && ReferenceEquals(scene, renderer.Render()),
            $"clean re-render allocated {allocated} bytes",
            $"clean re-render of {tree.Count} entities allocates nothing and reuses the scene");
    }

    private static void RunDirtyLeafRelayoutStaysBounded()
    {
        XsrUiRenderer renderer = BuildGridRenderer(20, 25, out XsrUiTree tree, out _);
        _ = renderer.Render();
        XsrUiEntityId leaf = FindFirstLeaf(tree, out XsrUiEntityId root);
        int totalEntities = tree.Count;

        // A paint-only change must not relayout anything.
        tree.GetComponent<XsrUiElement>(leaf)!.Width = 120;
        tree.MarkDirty(leaf, XsrUiDirtyKinds.Paint);
        _ = renderer.Render();
        int paintVisits = renderer.LastLayoutVisits;

        // A structural change relayouts only the leaf's ancestor chain, not the whole tree.
        tree.SetComponent(leaf, new XsrUiElement { Width = 140, Height = 20 });
        _ = renderer.Render();
        int layoutVisits = renderer.LastLayoutVisits;
        _ = root;

        Gate(
            paintVisits == 0 && layoutVisits < totalEntities,
            $"paint-only change visited {paintVisits}; structural change visited {layoutVisits} of {totalEntities}",
            "paint-only changes skip layout; a leaf change relayouts only its ancestor chain");
    }

    private static void RunSceneMatchesEntityTree()
    {
        const int rows = 30;
        const int columns = 30;
        XsrUiRenderer renderer = BuildGridRenderer(rows, columns, out XsrUiTree tree, out XsrUiEntityId root);
        XsrUiScene scene = renderer.Render();
        int expected = tree.Count;
        int actual = scene.Count;

        bool ordered = scene.Count > 0 && scene[0].Entity.Equals(root) && scene[0].Depth == 0;
        for (int index = 1; ordered && index < scene.Count; index++)
        {
            ordered = scene[index].Depth <= scene[index - 1].Depth + 1;
        }

        Gate(
            actual == expected && ordered,
            $"scene holds {actual} of {expected} entities",
            "the produced scene carries exactly the entity tree in deterministic order");
        Report($"scene production: {actual} nodes");
    }

    private static void RunRenderThroughputReport()
    {
        XsrUiRenderer renderer = BuildGridRenderer(40, 40, out XsrUiTree tree, out _);
        _ = renderer.Render();

        const int frames = 200;
        Stopwatch watch = Stopwatch.StartNew();
        for (int frame = 0; frame < frames; frame++)
        {
            _ = renderer.Render();
        }

        watch.Stop();
        double microsecondsPerFrame = watch.Elapsed.TotalMilliseconds * 1000 / frames;
        Report(
            $"render throughput: {microsecondsPerFrame.ToString("F1", CultureInfo.InvariantCulture)}us/frame "
            + $"for {tree.Count} entities (informational, {frames} clean frames)");
    }

    private static XsrUiRenderer BuildGridRenderer(
        int rows,
        int columns,
        out XsrUiTree tree,
        out XsrUiEntityId root)
    {
        tree = new XsrUiTree();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        root = tree.Create("root");
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Vertical));

        for (int row = 0; row < rows; row++)
        {
            XsrUiEntityId line = tree.Create($"row-{row}");
            tree.SetComponent(line, new XsrUiStackPanel(XsrUiOrientation.Horizontal));
            tree.Attach(line, root);
            for (int column = 0; column < columns; column++)
            {
                XsrUiEntityId cell = tree.Create($"cell-{row}-{column}");
                tree.SetComponent(cell, new XsrUiElement { Width = 40, Height = 20 });
                tree.SetComponent(cell, new XsrUiSemantic(XsrUiSemanticRole.Text, $"cell {row}/{column}"));
                tree.Attach(cell, line);
            }
        }

        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        return renderer;
    }

    private static XsrUiEntityId FindFirstLeaf(XsrUiTree tree, out XsrUiEntityId root)
    {
        root = new XsrUiEntityId(1);
        if (!tree.IsAlive(root))
        {
            throw new InvalidOperationException("The benchmark tree has no root at handle 1.");
        }

        XsrUiEntityId? leaf = null;
        tree.Walk(
            root,
            entity =>
            {
                if (leaf is null
                    && tree.GetComponent<XsrUiElement>(entity) is not null
                    && tree.Children(entity).Count == 0)
                {
                    leaf = entity;
                }

                return true;
            });

        return leaf ?? throw new InvalidOperationException("The benchmark tree has no leaf with an element component.");
    }

    private static void Gate(bool condition, string observed, string requirement)
    {
        if (condition)
        {
            Console.WriteLine($"GATE PASS: {requirement} ({observed})");
        }
        else
        {
            Interlocked.Increment(ref _failures);
            Console.Error.WriteLine($"GATE FAIL: {requirement} ({observed})");
        }
    }

    private static void Report(string message) =>
        Console.WriteLine($"BENCH: {message}");
}
