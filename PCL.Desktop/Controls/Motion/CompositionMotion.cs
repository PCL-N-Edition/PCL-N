// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;

namespace PCL.Desktop.Controls.Motion;

/// <summary>
/// Temporary render-thread motion for layout state changes. Layout is committed
/// once; the compositor interpolates only the old and final visual geometry.
/// </summary>
internal static class CompositionMotion
{
    internal static IDisposable? EnableLayoutTransition(
        Visual element,
        TimeSpan duration,
        bool animateOffset,
        bool animateSize)
    {
        CompositionVisual? visual = ElementComposition.GetElementVisual(element);
        if (visual is null || (!animateOffset && !animateSize))
            return null;

        Compositor compositor = visual.Compositor;
        ImplicitAnimationCollection animations = compositor.CreateImplicitAnimationCollection();
        CubicEaseOut easing = new();

        if (animateOffset)
        {
            Vector3DKeyFrameAnimation animation = compositor.CreateVector3DKeyFrameAnimation();
            animation.Target = nameof(CompositionVisual.Offset);
            animation.Duration = duration;
            animation.InsertExpressionKeyFrame(1f, "this.FinalValue", easing);
            animations[nameof(CompositionVisual.Offset)] = animation;
        }

        if (animateSize)
        {
            VectorKeyFrameAnimation animation = compositor.CreateVectorKeyFrameAnimation();
            animation.Target = nameof(CompositionVisual.Size);
            animation.Duration = duration;
            animation.InsertExpressionKeyFrame(1f, "this.FinalValue", easing);
            animations[nameof(CompositionVisual.Size)] = animation;
        }

        ImplicitAnimationCollection? previous = visual.ImplicitAnimations;
        visual.ImplicitAnimations = animations;
        return new LayoutTransitionScope(visual, animations, previous);
    }

    private sealed class LayoutTransitionScope(
        CompositionVisual visual,
        ImplicitAnimationCollection animations,
        ImplicitAnimationCollection? previous) : IDisposable
    {
        private bool isDisposed;

        public void Dispose()
        {
            if (isDisposed)
                return;
            isDisposed = true;

            if (ReferenceEquals(visual.ImplicitAnimations, animations))
                visual.ImplicitAnimations = previous;
        }
    }
}
