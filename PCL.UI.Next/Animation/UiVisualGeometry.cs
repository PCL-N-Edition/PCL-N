// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Numerics;

namespace PCL.UI.Next;

/// <summary>
/// Canonical world-space geometry shared by hit testing, accessibility, native hosts and overlays.
/// It resolves the current component state directly, so callers after animation sampling do not
/// observe a stale <see cref="ComputedTransform"/> from the previous transform pass.
/// </summary>
public static class UiVisualGeometry
{
    public static bool TryResolveBounds(UiWorld world, UiEntity entity, out UiRect bounds)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.Entities.IsAlive(entity) ||
            !world.Components.TryGet(entity, out LayoutRect layout))
        {
            bounds = UiRect.Empty;
            return false;
        }

        bounds = TransformBounds(layout.Value, ResolveWorldTransform(world, entity));
        return true;
    }

    public static UiRect ResolveBounds(UiWorld world, UiEntity entity) =>
        TryResolveBounds(world, entity, out UiRect bounds) ? bounds : UiRect.Empty;

    public static Matrix3x2 ResolveWorldTransform(UiWorld world, UiEntity entity)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.Entities.EnsureAlive(entity);
        return UiTransformMath.ComputeWorld(world, entity);
    }

    public static UiRect TransformBounds(UiRect rect, Matrix3x2 transform)
    {
        Vector2 topLeft = Vector2.Transform(new Vector2(rect.X, rect.Y), transform);
        Vector2 topRight = Vector2.Transform(new Vector2(rect.Right, rect.Y), transform);
        Vector2 bottomLeft = Vector2.Transform(new Vector2(rect.X, rect.Bottom), transform);
        Vector2 bottomRight = Vector2.Transform(new Vector2(rect.Right, rect.Bottom), transform);
        float left = MathF.Min(MathF.Min(topLeft.X, topRight.X), MathF.Min(bottomLeft.X, bottomRight.X));
        float top = MathF.Min(MathF.Min(topLeft.Y, topRight.Y), MathF.Min(bottomLeft.Y, bottomRight.Y));
        float right = MathF.Max(MathF.Max(topLeft.X, topRight.X), MathF.Max(bottomLeft.X, bottomRight.X));
        float bottom = MathF.Max(MathF.Max(topLeft.Y, topRight.Y), MathF.Max(bottomLeft.Y, bottomRight.Y));
        return new UiRect(left, top, Math.Max(0f, right - left), Math.Max(0f, bottom - top));
    }
}
