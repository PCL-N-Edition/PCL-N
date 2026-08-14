// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.UI.Next.Backend.Avalonia.Test;

[TestClass]
[DoNotParallelize]
public sealed class AvaloniaBackendTests
{
    [TestMethod]
    public void AvaloniaTextEngine_ShapesAndWrapsText()
    {
        using HeadlessUnitTestSession session = CreateSession();
        session.Dispatch(() =>
        {
            using AvaloniaTextEngine engine = new();
            TextLayoutRequest request = new(
                "A wrapped text layout produced by Avalonia",
                FontFamilyId: 0,
                FontSize: 16f,
                FontWeight: 400,
                WidthConstraint: 90f,
                Wrapping: UiTextWrapping.Wrap,
                Direction: UiTextDirection.LeftToRight);

            TextLayoutHandle handle = engine.Layout(in request);
            UiSize measured = engine.Measure(handle);

            Assert.IsFalse(handle.IsNone);
            Assert.IsLessThanOrEqualTo(90.01f, measured.Width);
            Assert.IsGreaterThan(19f, measured.Height);
            engine.Release(handle);
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void AvaloniaSurface_AppliesRetainedCommitAndParticipatesInLayout()
    {
        using HeadlessUnitTestSession session = CreateSession();
        session.Dispatch(() =>
        {
            using AvaloniaTextEngine textEngine = new();
            AvaloniaUiBackend backend = new(textEngine);
            UiBackendContext context = new(new UiSize(320, 180));
            backend.Initialize(in context);

            RenderNodeId node = new(1, 1);
            RenderMutation[] mutations =
            [
                RenderMutation.Create(node, new UiEntity(1, 1), UiRenderNodeKind.RoundedRectangle),
                RenderMutation.SetParent(node, RenderNodeId.None),
                RenderMutation.SetBounds(node, new UiRect(12, 12, 180, 80)),
                RenderMutation.SetTransform(node, Matrix3x2.Identity),
                RenderMutation.SetOpacity(node, 1f),
                RenderMutation.SetBrush(node, UiColor.FromRgb(55, 104, 210)),
                RenderMutation.SetCornerRadius(node, 12f)
            ];
            UiCommitBatch batch = new(1, mutations);
            backend.Commit(in batch);
            backend.RequestFrame();

            Window window = new()
            {
                Width = 320,
                Height = 180,
                Content = backend.View
            };
            try
            {
                window.Show();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();

                Assert.AreEqual(1, backend.Surface.RetainedNodeCount);
                Assert.AreEqual(1, backend.Surface.CommitCount);
                Assert.IsTrue(backend.Surface.IsVisible);
                Assert.IsGreaterThan(0d, backend.Surface.Bounds.Width);
                Assert.IsGreaterThan(0d, backend.Surface.Bounds.Height);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void SurfaceRender_AfterEntityDestroyBeforeCommit_UsesLiveTextResource()
    {
        using HeadlessUnitTestSession session = CreateSession();
        session.Dispatch(() =>
        {
            UiSize viewport = new(320, 180);
            UiWorld world = new(new DeterministicUiClock());
            using AvaloniaTextEngine textEngine = new();
            using UiInteractiveRuntime runtime = new(
                world,
                textEngine,
                viewport,
                textCacheCapacity: 1);
            AvaloniaUiBackend backend = new(textEngine);
            UiScopeId scope = world.CreateRootScope();
            using UiRenderingRuntime rendering = new(
                world,
                backend,
                runtime.TextCache,
                scope,
                viewport);
            BlueprintInstantiator instantiator = new(world, new PresentationStore());
            BlueprintInstance live = instantiator.Instantiate(
                Ui.Compile(Ui.Text("retained until commit")),
                scope);
            Drain(world);
            TextLayoutHandle handle = world.Components.Get<TextLayout>(live.RootEntity).Handle;

            Window window = new()
            {
                Width = 320,
                Height = 180,
                Content = backend.View
            };
            try
            {
                window.Show();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                instantiator.Destroy(live);
                runtime.TextCache.ClearUnused();

                backend.RequestFrame();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();

                Assert.AreEqual(1, backend.Surface.RetainedNodeCount);
                Assert.AreNotEqual(UiSize.Zero, textEngine.Measure(handle));

                Assert.IsTrue(world.Update());
                Assert.AreEqual(0, backend.Surface.RetainedNodeCount);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                runtime.TextCache.ClearUnused();
                Assert.ThrowsExactly<InvalidOperationException>(() => textEngine.Measure(handle));
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void AvaloniaNativeHost_CreatesAndDestroysTextBoxOverlay()
    {
        using HeadlessUnitTestSession session = CreateSession();
        session.Dispatch(() =>
        {
            UiSize viewport = new(320, 180);
            UiWorld world = new(new DeterministicUiClock());
            using AvaloniaTextEngine textEngine = new();
            using UiInteractiveRuntime runtime = new(world, textEngine, viewport);
            UiScopeId application = world.CreateRootScope();
            UiScopeId windowScope = world.CreateScope(application);
            runtime.Input.InputRoots.Register(windowScope);
            AvaloniaUiBackend backend = new(textEngine);
            using UiRenderingRuntime rendering = new(
                world,
                backend,
                runtime.TextCache,
                windowScope,
                viewport,
                input: runtime.Input);
            BlueprintInstantiator instantiator = new(world, new PresentationStore());
            BlueprintInstance live = instantiator.Instantiate(
                Ui.Compile(Ui.TextBox("hello", "placeholder")),
                windowScope);
            Drain(world);

            Assert.AreEqual(1, backend.NativeHostCount);
            Assert.AreEqual(2, backend.View.Children.Count);
            Assert.AreEqual(1, backend.AccessibilityTree.NodeCount);
            Canvas nativeLayer = (Canvas)backend.View.Children[1];
            TextBox textBox = (TextBox)nativeLayer.Children[0];
            Assert.AreEqual("placeholder", AutomationProperties.GetName(textBox));

            instantiator.Destroy(live);
            Assert.AreEqual(0, backend.NativeHostCount);
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void AvaloniaNativeHost_EcsFocusLoss_BlursPlatformControl()
    {
        using HeadlessUnitTestSession session = CreateSession();
        session.Dispatch(() =>
        {
            UiSize viewport = new(320, 180);
            UiWorld world = new(new DeterministicUiClock());
            using AvaloniaTextEngine textEngine = new();
            using UiInteractiveRuntime runtime = new(world, textEngine, viewport);
            UiScopeId scope = world.CreateRootScope();
            runtime.Input.InputRoots.Register(scope);
            AvaloniaUiBackend backend = new(textEngine);
            using UiRenderingRuntime rendering = new(
                world,
                backend,
                runtime.TextCache,
                scope,
                viewport,
                input: runtime.Input);
            BlueprintInstantiator instantiator = new(world, new PresentationStore());
            BlueprintInstance live = instantiator.Instantiate(
                Ui.Compile(Ui.Column(Ui.TextBox("native"), Ui.Button("retained"))),
                scope);
            Window window = new() { Width = 320, Height = 180, Content = backend.View };
            try
            {
                window.Show();
                Drain(world);
                TextBox textBox = (TextBox)((Canvas)backend.View.Children[1]).Children[0];
                Assert.IsTrue(runtime.Input.Focus.Focus(live.EntityAt(1), world.Clock.Now));
                Assert.IsTrue(world.Update());
                Assert.IsTrue(textBox.IsFocused);

                Assert.IsTrue(runtime.Input.Focus.Focus(live.EntityAt(2), world.Clock.Now));
                Assert.IsTrue(world.Update());

                Assert.IsFalse(textBox.IsFocused);
                Assert.IsTrue(backend.Surface.IsFocused);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void ModalOpening_BlursBackgroundNativeTextBox()
    {
        using HeadlessUnitTestSession session = CreateSession();
        session.Dispatch(() =>
        {
            UiSize viewport = new(320, 180);
            UiWorld world = new(new DeterministicUiClock());
            using AvaloniaTextEngine textEngine = new();
            using UiInteractiveRuntime runtime = new(world, textEngine, viewport);
            UiScopeId scope = world.CreateRootScope();
            runtime.Input.InputRoots.Register(scope);
            AvaloniaUiBackend backend = new(textEngine);
            using UiRenderingRuntime rendering = new(
                world,
                backend,
                runtime.TextCache,
                scope,
                viewport,
                input: runtime.Input);
            BlueprintInstantiator instantiator = new(world, new PresentationStore());
            using UiOverlayRuntime overlays = new(world, runtime, instantiator, scope);
            BlueprintInstance live = instantiator.Instantiate(Ui.Compile(Ui.TextBox("background")), scope);
            Window window = new() { Width = 320, Height = 180, Content = backend.View };
            try
            {
                window.Show();
                Drain(world);
                TextBox textBox = (TextBox)((Canvas)backend.View.Children[1]).Children[0];
                Assert.IsTrue(runtime.Input.Focus.Focus(live.RootEntity, world.Clock.Now));
                Assert.IsTrue(world.Update());
                Assert.IsTrue(textBox.IsFocused);

                overlays.ShowModal(Ui.Compile(Ui.Button("Modal action")));
                Assert.IsTrue(world.Update());

                Assert.IsFalse(textBox.IsFocused);
                Assert.IsTrue(backend.Surface.IsFocused);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void AvaloniaNativeHostToNativeHost_FocusTransfersExactlyOnce()
    {
        using HeadlessUnitTestSession session = CreateSession();
        session.Dispatch(() =>
        {
            UiSize viewport = new(320, 180);
            UiWorld world = new(new DeterministicUiClock());
            using AvaloniaTextEngine textEngine = new();
            using UiInteractiveRuntime runtime = new(world, textEngine, viewport);
            UiScopeId scope = world.CreateRootScope();
            runtime.Input.InputRoots.Register(scope);
            AvaloniaUiBackend backend = new(textEngine);
            using UiRenderingRuntime rendering = new(
                world,
                backend,
                runtime.TextCache,
                scope,
                viewport,
                input: runtime.Input);
            BlueprintInstantiator instantiator = new(world, new PresentationStore());
            BlueprintInstance live = instantiator.Instantiate(
                Ui.Compile(Ui.Column(Ui.TextBox("first"), Ui.TextBox("second"))),
                scope);
            Window window = new() { Width = 320, Height = 180, Content = backend.View };
            try
            {
                window.Show();
                Drain(world);
                Canvas layer = (Canvas)backend.View.Children[1];
                TextBox first = (TextBox)layer.Children[0];
                TextBox second = (TextBox)layer.Children[1];
                Assert.IsTrue(runtime.Input.Focus.Focus(live.EntityAt(1), world.Clock.Now));
                Assert.IsTrue(world.Update());
                Assert.IsTrue(first.IsFocused);
                int lost = 0;
                int got = 0;
                first.LostFocus += (_, _) => lost++;
                second.GotFocus += (_, _) => got++;

                Assert.IsTrue(runtime.Input.Focus.Focus(live.EntityAt(2), world.Clock.Now));
                Assert.IsTrue(world.Update());

                Assert.IsFalse(first.IsFocused);
                Assert.IsTrue(second.IsFocused);
                Assert.AreEqual(1, lost);
                Assert.AreEqual(1, got);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void AvaloniaAccessibility_ExposesSemanticPeersAndRoutesInvoke()
    {
        using HeadlessUnitTestSession session = CreateSession();
        session.Dispatch(() =>
        {
            UiSize viewport = new(320, 180);
            UiWorld world = new(new DeterministicUiClock());
            using AvaloniaTextEngine textEngine = new();
            using UiInteractiveRuntime runtime = new(world, textEngine, viewport);
            UiScopeId scope = world.CreateRootScope();
            runtime.Input.InputRoots.Register(scope);
            AvaloniaUiBackend backend = new(textEngine);
            using UiRenderingRuntime rendering = new(
                world,
                backend,
                runtime.TextCache,
                scope,
                viewport,
                input: runtime.Input);
            BlueprintInstantiator instantiator = new(world, new PresentationStore());
            instantiator.Instantiate(
                Ui.Compile(Ui.Button("Install").Command(new UiCommand(77))),
                scope);
            Drain(world);

            Assert.IsTrue((backend.Capabilities & UiBackendCapabilities.Accessibility) != 0);
            Assert.AreEqual(2, backend.AccessibilityTree.NodeCount);
            AutomationPeer root = ControlAutomationPeer.CreatePeerForElement(backend.Surface)!;
            IReadOnlyList<AutomationPeer> roots = root.GetChildren();
            Assert.AreEqual(1, roots.Count);
            Assert.AreEqual("Install", roots[0].GetName());
            Assert.AreEqual(AutomationControlType.Button, roots[0].GetAutomationControlType());

            IInvokeProvider invoke = (IInvokeProvider)roots[0];
            invoke.Invoke();
            Assert.IsTrue(world.Update());
            Assert.IsTrue(runtime.Input.Commands.TryDequeue(out UiCommandInvocation invocation));
            Assert.AreEqual(new UiCommand(77), invocation.Command);
            Assert.AreEqual(UiCommandTrigger.Accessibility, invocation.Trigger);
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void Drain(UiWorld world)
    {
        int guard = 0;
        while (world.Scheduler.NeedsFrame && guard++ < 16)
            Assert.IsTrue(world.Update());
        Assert.IsFalse(world.Scheduler.NeedsFrame, "Runtime did not settle to idle.");
    }

    private static HeadlessUnitTestSession CreateSession() =>
        HeadlessUnitTestSession.StartNew(
            typeof(TestApplication),
            AvaloniaTestIsolationLevel.PerTest);

    private sealed class TestApplication : Application
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<TestApplication>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions
                {
                    UseHeadlessDrawing = true
                });
    }
}
