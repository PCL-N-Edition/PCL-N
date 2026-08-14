// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.UI.Next.Test;

[TestClass]
public sealed class NativeHostTests
{
    [TestMethod]
    public void TextBox_BindingSynchronizesToNativeBackend()
    {
        using TestContext context = Create();
        const int valueSlice = 1;
        context.Store.Set(valueSlice, "initial");
        UiSelector<string> value = UiSelectors.String(1, valueSlice, store => store.Get<string>(valueSlice));
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(Ui.TextBox(placeholder: "Search").BindValue(value)),
            context.WindowScope);
        Drain(context.World);

        Assert.AreEqual(1, context.Backend.NativeHostCount);
        NativeHostDescriptor created = context.Backend.LastDescriptor!.Value;
        Assert.AreEqual(live.RootEntity, created.Owner);
        Assert.AreEqual("initial", created.State.Value);
        Assert.AreEqual("Search", created.State.Placeholder);
        Assert.AreEqual(36f, created.State.Bounds.Height, 0.01f);

        context.Store.Set(valueSlice, "updated");
        Drain(context.World);
        Assert.IsTrue((context.Backend.LastMutation!.Value.Flags & NativeHostMutationFlags.Value) != 0);
        Assert.AreEqual("updated", context.Backend.LastMutation.Value.State.Value);
    }

    [TestMethod]
    public void NativeInput_IsJournaledAndDoesNotMutateEcsSourceState()
    {
        using TestContext context = Create();
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(Ui.TextBox("source")),
            context.WindowScope);
        Drain(context.World);
        NativeHostHandle handle = context.Backend.LastHandle;

        context.Backend.Emit(new NativeHostEvent(
            handle,
            NativeHostEventKind.ValueChanged,
            context.Clock.Now,
            "typed",
            5,
            5));
        Assert.IsTrue(context.World.Update());

        UiNativeHostRuntime nativeHosts = context.Rendering.NativeHosts!;
        Assert.AreEqual(1, nativeHosts.FrameEvents.Count);
        Assert.AreEqual("typed", nativeHosts.FrameEvents[0].Value);
        Assert.AreEqual("source", context.World.Components.Get<NativeHostComponent>(live.RootEntity).Value);

        context.Backend.Emit(new NativeHostEvent(handle, NativeHostEventKind.GotFocus, context.Clock.Now));
        Assert.IsTrue(context.World.Update());
        Assert.AreEqual(live.RootEntity, context.Runtime.Input.Focus.GetFocused(context.InputRoot));
    }

    [TestMethod]
    public void ScopeDisposal_DestroysNativeHostImmediately()
    {
        using TestContext context = Create();
        context.Instantiator.Instantiate(Ui.Compile(Ui.TextBox()), context.WindowScope);
        Drain(context.World);
        Assert.AreEqual(1, context.Backend.NativeHostCount);

        Assert.IsTrue(context.World.DisposeScope(context.WindowScope));

        Assert.AreEqual(0, context.Backend.NativeHostCount);
        Assert.AreEqual(1, context.Backend.DestroyCount);
    }

    [TestMethod]
    public void NativeHost_RotationUsesFourCornerVisualBounds()
    {
        using TestContext context = Create();
        UiEntity textBox = context.Instantiator.Instantiate(
            Ui.Compile(
                Ui.TextBox()
                    .Width(UiLength.Pixels(100f))
                    .Height(UiLength.Pixels(40f))),
            context.WindowScope).RootEntity;
        Drain(context.World);

        context.Runtime.Animation.SetDirect(textBox, UiAnimationProperty.Rotation, 45f);
        Drain(context.World);

        NativeHostMutation mutation = context.Backend.LastMutation!.Value;
        Assert.IsTrue((mutation.Flags & NativeHostMutationFlags.Bounds) != 0);
        UiRect expected = UiVisualGeometry.ResolveBounds(context.World, textBox);
        Assert.AreEqual(expected.X, mutation.State.Bounds.X, 0.01f);
        Assert.AreEqual(expected.Y, mutation.State.Bounds.Y, 0.01f);
        Assert.AreEqual(expected.Width, mutation.State.Bounds.Width, 0.01f);
        Assert.AreEqual(expected.Height, mutation.State.Bounds.Height, 0.01f);
        Assert.IsGreaterThan(90f, mutation.State.Bounds.Width);
    }

    [TestMethod]
    public void NativeHost_EcsFocusLoss_ReconcilesToPlatformSurface()
    {
        using TestContext context = Create();
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(
                Ui.Column(
                    Ui.TextBox("native"),
                    Ui.Button("retained"))),
            context.WindowScope);
        Drain(context.World);
        UiEntity textBox = live.EntityAt(1);
        UiEntity button = live.EntityAt(2);

        Assert.IsTrue(context.Runtime.Input.Focus.Focus(textBox, context.Clock.Now));
        Assert.IsTrue(context.World.Update());
        Assert.AreEqual(context.Backend.LastHandle, context.Backend.FocusedHost);

        Assert.IsTrue(context.Runtime.Input.Focus.Focus(button, context.Clock.Now));
        Assert.IsTrue(context.World.Update());

        Assert.AreEqual(NativeHostHandle.None, context.Backend.FocusedHost);
    }

    [TestMethod]
    public void NativeHostToNativeHost_FocusTransfersExactlyOnce()
    {
        using TestContext context = Create();
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(
                Ui.Column(
                    Ui.TextBox("first"),
                    Ui.TextBox("second"))),
            context.WindowScope);
        Drain(context.World);
        UiEntity first = live.EntityAt(1);
        UiEntity second = live.EntityAt(2);
        NativeHostHandle firstHandle = context.Backend.HandleFor(first);
        NativeHostHandle secondHandle = context.Backend.HandleFor(second);

        Assert.IsTrue(context.Runtime.Input.Focus.Focus(first, context.Clock.Now));
        Assert.IsTrue(context.World.Update());
        int beforeTransfer = context.Backend.FocusReconciliationCount;

        Assert.IsTrue(context.Runtime.Input.Focus.Focus(second, context.Clock.Now));
        Assert.IsTrue(context.World.Update());

        Assert.AreEqual(firstHandle, context.Backend.PreviousFocusedHost);
        Assert.AreEqual(secondHandle, context.Backend.FocusedHost);
        Assert.AreEqual(beforeTransfer + 1, context.Backend.FocusReconciliationCount);
    }

    private static TestContext Create()
    {
        DeterministicUiClock clock = new();
        UiWorld world = new(clock);
        UiInteractiveRuntime runtime = new(world, new DeterministicTextEngine(), new UiSize(240f, 100f));
        UiScopeId applicationScope = world.CreateRootScope();
        UiScopeId windowScope = world.CreateScope(applicationScope);
        UiInputRootId inputRoot = runtime.Input.InputRoots.Register(windowScope);
        PresentationStore store = new();
        BlueprintInstantiator instantiator = new(world, store);
        NativeTestBackend backend = new();
        UiRenderingRuntime rendering = new(
            world,
            backend,
            runtime.TextCache,
            windowScope,
            new UiSize(240f, 100f),
            input: runtime.Input);
        return new TestContext(
            clock,
            world,
            runtime,
            rendering,
            backend,
            applicationScope,
            windowScope,
            inputRoot,
            store,
            instantiator);
    }

    private static void Drain(UiWorld world)
    {
        int guard = 0;
        while (world.Scheduler.NeedsFrame && guard++ < 16)
            Assert.IsTrue(world.Update());
        Assert.IsFalse(world.Scheduler.NeedsFrame);
    }

    private sealed record TestContext(
        DeterministicUiClock Clock,
        UiWorld World,
        UiInteractiveRuntime Runtime,
        UiRenderingRuntime Rendering,
        NativeTestBackend Backend,
        UiScopeId ApplicationScope,
        UiScopeId WindowScope,
        UiInputRootId InputRoot,
        PresentationStore Store,
        BlueprintInstantiator Instantiator) : IDisposable
    {
        public void Dispose()
        {
            Rendering.Dispose();
            Runtime.Dispose();
        }
    }

    private sealed class NativeTestBackend : IUiBackend, INativeHostBackend
    {
        private readonly Dictionary<NativeHostHandle, NativeHostDescriptor> _hosts = [];
        private int _nextHandle = 1;

        public UiBackendCapabilities Capabilities => UiBackendCapabilities.NativeTextInput;
        public int NativeHostCount => _hosts.Count;
        public int DestroyCount { get; private set; }
        public NativeHostHandle LastHandle { get; private set; }
        public NativeHostDescriptor? LastDescriptor { get; private set; }
        public NativeHostMutation? LastMutation { get; private set; }
        public NativeHostHandle PreviousFocusedHost { get; private set; }
        public NativeHostHandle FocusedHost { get; private set; }
        public int FocusReconciliationCount { get; private set; }
        public event Action<NativeHostEvent>? NativeHostEventRaised;

        public void Initialize(in UiBackendContext context) => _ = context;
        public void Commit(in UiCommitBatch batch) => _ = batch;
        public void RequestFrame() { }

        public NativeHostHandle CreateNativeHost(in NativeHostDescriptor descriptor)
        {
            NativeHostHandle handle = new(_nextHandle++, 1);
            _hosts.Add(handle, descriptor);
            LastHandle = handle;
            LastDescriptor = descriptor;
            return handle;
        }

        public void UpdateNativeHost(NativeHostHandle handle, in NativeHostMutation mutation)
        {
            Assert.IsTrue(_hosts.ContainsKey(handle));
            LastMutation = mutation;
        }

        public void ReconcileNativeHostFocus(NativeHostHandle focusedHost)
        {
            PreviousFocusedHost = FocusedHost;
            FocusedHost = focusedHost;
            FocusReconciliationCount++;
        }

        public void DestroyNativeHost(NativeHostHandle handle)
        {
            if (_hosts.Remove(handle))
                DestroyCount++;
        }

        public void Emit(NativeHostEvent nativeEvent) => NativeHostEventRaised?.Invoke(nativeEvent);

        public NativeHostHandle HandleFor(UiEntity owner) =>
            _hosts.Single(pair => pair.Value.Owner == owner).Key;
    }
}
