// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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
                Content = backend.Surface
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
