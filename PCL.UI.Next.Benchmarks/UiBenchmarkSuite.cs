// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;

namespace PCL.UI.Next.Benchmarks;

public readonly record struct UiBenchmarkResult(
    string Name,
    int Operations,
    double ElapsedMilliseconds,
    long AllocatedBytes,
    string Detail);

public static class UiBenchmarkSuite
{
    public static IReadOnlyList<UiBenchmarkResult> RunAll(bool verify)
    {
        List<UiBenchmarkResult> results =
        [
            RunIdle(verify),
            RunHoverStress(verify),
            RunAnimationStress(verify),
            RunLargeVirtualList(verify),
            RunLayoutStress(verify),
            RunThemeSwitch(verify),
            RunRenderDiff(verify)
        ];
        return results;
    }

    private static UiBenchmarkResult RunIdle(bool verify)
    {
        UiWorld world = new(
            new DeterministicUiClock(),
            diagnosticsOptions: UiDiagnosticsOptions.Disabled);
        using UiInteractiveRuntime runtime = new(
            world,
            new DeterministicTextEngine(),
            new UiSize(1_000f, 1_000f),
            applyDefaults: false);
        UiScopeId scope = world.CreateRootScope();
        UiEntity root = world.CreateEntity(scope);
        for (int i = 0; i < 5_000; i++)
        {
            UiEntity entity = world.CreateEntity(scope);
            world.AttachChild(root, entity);
        }
        Drain(world);
        Require(!world.Scheduler.NeedsFrame, "Idle world retained a recurring frame request.", verify);

        const int operations = 100_000;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        int unexpectedFrames = 0;
        for (int i = 0; i < operations; i++)
            unexpectedFrames += world.Update() ? 1 : 0;
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Require(unexpectedFrames == 0, "Idle probe executed an unexpected frame.", verify);
        Require(allocated == 0, "Idle probe allocated " + allocated + " bytes.", verify);
        Require(elapsed < 1_000d, "Idle probe exceeded 1000 ms.", verify);
        return new UiBenchmarkResult("B1 Idle", operations, elapsed, allocated, "entities=5001 frames=0");
    }

    private static UiBenchmarkResult RunHoverStress(bool verify)
    {
        DeterministicUiClock clock = new();
        UiWorld world = new(clock, diagnosticsOptions: UiDiagnosticsOptions.Disabled);
        using UiInteractiveRuntime runtime = new(
            world,
            new DeterministicTextEngine(),
            new UiSize(600f, 20_000f));
        UiScopeId scope = world.CreateRootScope();
        UiInputRootId inputRoot = runtime.Input.InputRoots.Register(scope);
        UiNode[] buttons = new UiNode[1_000];
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i] = Ui.Button("B" + i)
                .Width(UiLength.Pixels(200f))
                .Height(UiLength.Pixels(20f));
        }
        BlueprintInstantiator instantiator = new(world, new PresentationStore());
        instantiator.Instantiate(Ui.Compile(Ui.Column(buttons)), scope);
        Drain(world);

        const int operations = 250;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int i = 0; i < operations; i++)
        {
            runtime.Input.EnqueuePointer(
                inputRoot,
                UiPointerEventKind.Move,
                new UiPoint(10f, (i % buttons.Length) * 20f + 5f));
            world.Update();
        }
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Require(elapsed < 5_000d, "Hover stress exceeded 5000 ms.", verify);
        return new UiBenchmarkResult("B2 Hover Stress", operations, elapsed, allocated, "clickable=1000");
    }

    private static UiBenchmarkResult RunAnimationStress(bool verify)
    {
        int[] counts = [500, 1_000, 5_000];
        double elapsedTotal = 0d;
        long allocatedTotal = 0;
        int operations = 0;
        for (int c = 0; c < counts.Length; c++)
        {
            int count = counts[c];
            DeterministicUiClock clock = new();
            UiWorld world = new(clock, diagnosticsOptions: UiDiagnosticsOptions.Disabled);
            using UiInteractiveRuntime runtime = new(
                world,
                new DeterministicTextEngine(),
                new UiSize(100f, 100f),
                applyDefaults: false);
            UiScopeId scope = world.CreateRootScope();
            UiEntity[] entities = new UiEntity[count];
            for (int i = 0; i < count; i++)
            {
                UiEntity entity = world.CreateEntity(scope);
                world.Set(entity, ResolvedStyle.Default);
                entities[i] = entity;
            }
            Drain(world);
            for (int i = 0; i < count; i++)
            {
                runtime.Animation.Retarget(
                    entities[i],
                    UiAnimationProperty.Opacity,
                    0.2f,
                    new UiAnimationSpec(UiMotion.Standard));
            }

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            int frames = 0;
            while (world.Scheduler.NeedsFrame && frames++ < 120)
            {
                clock.Advance(0.016d);
                world.Update();
            }
            elapsedTotal += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocatedTotal += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            operations += count * frames;
            Require(runtime.Animation.ActiveChannelCount == 0, "Animation stress did not settle.", verify);
        }
        Require(elapsedTotal < 8_000d, "Animation stress exceeded 8000 ms.", verify);
        return new UiBenchmarkResult(
            "B3 Animation Stress",
            operations,
            elapsedTotal,
            allocatedTotal,
            "channels=500/1000/5000");
    }

    private static UiBenchmarkResult RunLargeVirtualList(bool verify)
    {
        UiWorld world = new(
            new DeterministicUiClock(),
            diagnosticsOptions: UiDiagnosticsOptions.Disabled);
        using UiInteractiveRuntime runtime = new(
            world,
            new DeterministicTextEngine(),
            new UiSize(300f, 600f));
        UiScopeId scope = world.CreateRootScope();
        runtime.Input.InputRoots.Register(scope);
        BlueprintInstantiator instantiator = new(world, new PresentationStore());
        const int heightSlice = 1;
        UiSelector<bool> tall = UiSelectors.Bool(
            1,
            heightSlice,
            static presentation => presentation.Get<bool>(heightSlice));
        UiNode itemTemplate = Ui.If(
            tall,
            Ui.Container().Height(UiLength.Pixels(32f)),
            Ui.Container().Height(UiLength.Pixels(16f)));
        UiEntity host = instantiator.Instantiate(
            Ui.Compile(Ui.VirtualList(24f, 8, 8)),
            scope).RootEntity;
        Drain(world);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        using UiVirtualListRegistration registration = runtime.Virtualization.Register(
            host,
            new BenchmarkItemSource(100_000),
            Ui.Compile(itemTemplate));
        Drain(world);
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        if (!runtime.Virtualization.TryGetSnapshot(host, out UiVirtualizationSnapshot snapshot))
            throw new InvalidOperationException("Virtual list did not produce a snapshot.");
        Require(snapshot.RealizedCount < 100, "Virtual list realized 100 or more rows.", verify);
        Require(elapsed < 5_000d, "Virtual-list setup exceeded 5000 ms.", verify);
        return new UiBenchmarkResult(
            "B4 Large Virtual List",
            100_000,
            elapsed,
            allocated,
            $"realized={snapshot.RealizedCount} pool={snapshot.RecyclePoolCount}");
    }

    private static UiBenchmarkResult RunLayoutStress(bool verify)
    {
        UiWorld world = new(
            new DeterministicUiClock(),
            diagnosticsOptions: UiDiagnosticsOptions.Disabled);
        using UiInteractiveRuntime runtime = new(
            world,
            new DeterministicTextEngine(),
            new UiSize(800f, 600f));
        UiScopeId scope = world.CreateRootScope();
        UiNode nested = Ui.Container()
            .Width(UiLength.Pixels(10f))
            .Height(UiLength.Pixels(10f));
        const int depth = 256;
        UiGridDefinition grid = new(
            [UiGridTrack.Star()],
            [UiGridTrack.Auto()]);
        for (int i = 0; i < depth; i++)
        {
            nested = i % 2 == 0
                ? Ui.Column(nested).Padding(new UiThickness(1f))
                : Ui.Grid(grid, nested.GridCell(0, 0)).Padding(new UiThickness(1f));
        }
        BlueprintInstantiator instantiator = new(world, new PresentationStore());

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        instantiator.Instantiate(Ui.Compile(nested), scope);
        if (!world.Update())
            throw new InvalidOperationException("Deep layout did not execute its initial frame.");
        int measured = runtime.Layout.LastMeasureCount;
        Drain(world);
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Require(measured >= depth, "Deep layout did not measure the complete hierarchy.", verify);
        Require(elapsed < 5_000d, "Deep layout exceeded 5000 ms.", verify);
        return new UiBenchmarkResult(
            "B5 Layout Stress",
            depth,
            elapsed,
            allocated,
            $"depth={depth} measured={measured}");
    }

    private static UiBenchmarkResult RunThemeSwitch(bool verify)
    {
        UiWorld world = new(
            new DeterministicUiClock(),
            diagnosticsOptions: UiDiagnosticsOptions.Disabled);
        using UiInteractiveRuntime runtime = new(
            world,
            new DeterministicTextEngine(),
            new UiSize(100f, 100f),
            applyDefaults: false);
        UiClass benchmarkClass = new(9_001, "BenchmarkAccent");
        runtime.Theme.Set(UiThemeTokens.Accent, UiColor.FromRgb(1, 2, 3));
        runtime.Styles.Add(new UiStyleRule(
            benchmarkClass,
            default(UiStyleValues).WithBackground(UiThemeTokens.Accent)));
        UiScopeId scope = world.CreateRootScope();
        const int count = 10_000;
        const int dependentCount = count / 2;
        for (int i = 0; i < count; i++)
        {
            UiEntity entity = world.CreateEntity(scope);
            world.Set(
                entity,
                i < dependentCount
                    ? StyleClassSet.From([benchmarkClass.Id])
                    : default);
            world.Dirty.Mark(entity, UiDirtyFlags.Style);
        }
        Drain(world);

        UiColor next = UiColor.FromRgb(200, 100, 50);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        runtime.Theme.Set(UiThemeTokens.Accent, next);
        Drain(world);
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        ReadOnlySpan<ResolvedStyle> styles = world.Components.Pool<ResolvedStyle>().Components;
        int changed = 0;
        for (int i = 0; i < styles.Length; i++)
            changed += styles[i].Background == next ? 1 : 0;
        Require(changed == dependentCount, "Theme switch did not update exactly the dependent subset.", verify);
        Require(elapsed < 5_000d, "Theme switch exceeded 5000 ms.", verify);
        return new UiBenchmarkResult("B6 Theme Switch", count, elapsed, allocated, $"affected={changed}");
    }

    private static UiBenchmarkResult RunRenderDiff(bool verify)
    {
        UiWorld world = new(
            new DeterministicUiClock(),
            diagnosticsOptions: UiDiagnosticsOptions.Disabled);
        using UiInteractiveRuntime runtime = new(
            world,
            new DeterministicTextEngine(),
            new UiSize(500f, 500f),
            applyDefaults: false);
        UiScopeId scope = world.CreateRootScope();
        const int entityCount = 1_000;
        UiEntity[] entities = new UiEntity[entityCount];
        for (int i = 0; i < entityCount; i++)
        {
            UiEntity entity = world.CreateEntity(scope);
            world.Set(entity, new NodeKindComponent { Kind = UiNodeKind.Container });
            world.Set(entity, new LayoutRect { Value = new UiRect(i % 50 * 10f, i / 50 * 10f, 8f, 8f) });
            ResolvedStyle style = ResolvedStyle.Default;
            world.Set(entity, style);
            world.Set(entity, ComputedVisual.FromResolved(in style));
            entities[i] = entity;
        }
        HeadlessUiBackend backend = new();
        using UiRenderingRuntime rendering = new(
            world,
            backend,
            runtime.TextCache,
            scope,
            new UiSize(500f, 500f));
        Drain(world);
        for (int i = 0; i < entities.Length; i++)
            world.Dirty.Mark(entities[i], UiDirtyFlags.Render);
        world.Scheduler.RequestReactiveFrame();
        world.Update();

        int[] changes = [1, 10, 100];
        double elapsedTotal = 0d;
        long allocatedTotal = 0;
        int mutationTotal = 0;
        int[] mutationsByPass = new int[changes.Length];
        for (int pass = 0; pass < changes.Length; pass++)
        {
            int changed = changes[pass];
            float opacity = 0.8f - pass * 0.2f;
            for (int i = 0; i < changed; i++)
            {
                ComputedVisual visual = world.Components.Get<ComputedVisual>(entities[i]);
                visual.Opacity = opacity;
                world.Set(entities[i], visual);
                world.Dirty.Mark(entities[i], UiDirtyFlags.Render);
            }
            world.Scheduler.RequestReactiveFrame();
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            world.Update();
            elapsedTotal += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocatedTotal += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            int mutations = backend.LastBatch?.Mutations.Length ?? 0;
            mutationsByPass[pass] = mutations;
            mutationTotal += mutations;
            Require(mutations <= changed, $"Render diff emitted {mutations} mutations for {changed} changes.", verify);
        }
        Require(elapsedTotal < 2_000d, "Render diff probe exceeded 2000 ms.", verify);
        return new UiBenchmarkResult(
            "B7 Render Diff",
            changes.Sum(),
            elapsedTotal,
            allocatedTotal,
            $"mutations={string.Join('/', mutationsByPass)} total={mutationTotal}");
    }

    private static void Drain(UiWorld world)
    {
        int guard = 0;
        while (world.Scheduler.NeedsFrame && guard++ < 256)
            world.Update();
        if (world.Scheduler.NeedsFrame)
            throw new InvalidOperationException("Benchmark Runtime did not settle to idle.");
    }

    private static void Require(bool condition, string message, bool verify)
    {
        if (verify && !condition)
            throw new InvalidOperationException(message);
    }

    private sealed class BenchmarkItemSource(int count) : IUiVirtualItemSource
    {
        public int Count { get; } = count;
        public ulong Version => 1;
        public long GetKey(int index) => index;
        public void BindItem(int index, PresentationStore presentation) => presentation.Set(1, index % 2 == 0);
        public bool TryGetIndex(long key, out int index)
        {
            index = (int)key;
            return key >= 0 && key < Count;
        }
    }
}
