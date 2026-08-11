// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Numerics;

namespace PCL.UI.Next;

internal static class AnimationPropertyRegistry
{
    public static float ReadCurrent(UiWorld world, UiEntity entity, UiAnimationProperty property)
    {
        ComputedVisual visual = world.Components.TryGet(entity, out ComputedVisual current)
            ? current
            : CreateVisual(world, entity);
        return property switch
        {
            UiAnimationProperty.Opacity => visual.Opacity,
            UiAnimationProperty.CornerRadius => visual.CornerRadius,
            UiAnimationProperty.TranslateX => visual.Transform.TranslateX,
            UiAnimationProperty.TranslateY => visual.Transform.TranslateY,
            UiAnimationProperty.ScaleX => visual.Transform.ScaleX,
            UiAnimationProperty.ScaleY => visual.Transform.ScaleY,
            UiAnimationProperty.Rotation => visual.Transform.Rotation,
            UiAnimationProperty.LayoutM11 => ReadLayoutMatrix(world, entity).M11,
            UiAnimationProperty.LayoutM12 => ReadLayoutMatrix(world, entity).M12,
            UiAnimationProperty.LayoutM21 => ReadLayoutMatrix(world, entity).M21,
            UiAnimationProperty.LayoutM22 => ReadLayoutMatrix(world, entity).M22,
            UiAnimationProperty.LayoutM31 => ReadLayoutMatrix(world, entity).M31,
            UiAnimationProperty.LayoutM32 => ReadLayoutMatrix(world, entity).M32,
            _ => throw new ArgumentOutOfRangeException(nameof(property))
        };
    }

    public static float ReadTarget(UiWorld world, UiEntity entity, UiAnimationProperty property)
    {
        ResolvedStyle style = world.Components.TryGet(entity, out ResolvedStyle resolved)
            ? resolved
            : ResolvedStyle.Default;
        return property switch
        {
            UiAnimationProperty.Opacity => style.Opacity,
            UiAnimationProperty.CornerRadius => style.CornerRadius,
            UiAnimationProperty.TranslateX => style.TranslateX,
            UiAnimationProperty.TranslateY => style.TranslateY,
            UiAnimationProperty.ScaleX => style.ScaleX,
            UiAnimationProperty.ScaleY => style.ScaleY,
            UiAnimationProperty.Rotation => style.Rotation,
            UiAnimationProperty.LayoutM11 or UiAnimationProperty.LayoutM22 => 1f,
            UiAnimationProperty.LayoutM12 or
            UiAnimationProperty.LayoutM21 or
            UiAnimationProperty.LayoutM31 or
            UiAnimationProperty.LayoutM32 => 0f,
            _ => throw new ArgumentOutOfRangeException(nameof(property))
        };
    }

    public static void WriteCurrent(
        UiWorld world,
        UiEntity entity,
        UiAnimationProperty property,
        float value)
    {
        if (!world.Entities.IsAlive(entity))
            return;
        ComputedVisual visual = world.Components.TryGet(entity, out ComputedVisual current)
            ? current
            : CreateVisual(world, entity);
        value = Constrain(property, value);
        switch (property)
        {
            case UiAnimationProperty.Opacity:
                if (visual.Opacity.Equals(value)) return;
                visual.Opacity = value;
                break;
            case UiAnimationProperty.CornerRadius:
                if (visual.CornerRadius.Equals(value)) return;
                visual.CornerRadius = value;
                break;
            case UiAnimationProperty.TranslateX:
                if (visual.Transform.TranslateX.Equals(value)) return;
                visual.Transform = visual.Transform with { TranslateX = value };
                break;
            case UiAnimationProperty.TranslateY:
                if (visual.Transform.TranslateY.Equals(value)) return;
                visual.Transform = visual.Transform with { TranslateY = value };
                break;
            case UiAnimationProperty.ScaleX:
                if (visual.Transform.ScaleX.Equals(value)) return;
                visual.Transform = visual.Transform with { ScaleX = value };
                break;
            case UiAnimationProperty.ScaleY:
                if (visual.Transform.ScaleY.Equals(value)) return;
                visual.Transform = visual.Transform with { ScaleY = value };
                break;
            case UiAnimationProperty.Rotation:
                if (visual.Transform.Rotation.Equals(value)) return;
                visual.Transform = visual.Transform with { Rotation = value };
                break;
            case UiAnimationProperty.LayoutM11:
            case UiAnimationProperty.LayoutM12:
            case UiAnimationProperty.LayoutM21:
            case UiAnimationProperty.LayoutM22:
            case UiAnimationProperty.LayoutM31:
            case UiAnimationProperty.LayoutM32:
                WriteLayoutMatrix(world, entity, property, value);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(property));
        }

        world.Set(entity, visual);
        UiDirtyFlags dirty = UiDirtyFlags.Render;
        if (AffectsTransform(property))
            dirty |= UiDirtyFlags.Transform;
        world.Dirty.Mark(entity, dirty);
    }

    public static ComputedVisual EnsureVisual(UiWorld world, UiEntity entity)
    {
        if (world.Components.TryGet(entity, out ComputedVisual visual))
            return visual;
        visual = CreateVisual(world, entity);
        world.Set(entity, visual);
        return visual;
    }

    public static bool AffectsTransform(UiAnimationProperty property) => property is
        UiAnimationProperty.TranslateX or
        UiAnimationProperty.TranslateY or
        UiAnimationProperty.ScaleX or
        UiAnimationProperty.ScaleY or
        UiAnimationProperty.Rotation or
        UiAnimationProperty.LayoutM11 or
        UiAnimationProperty.LayoutM12 or
        UiAnimationProperty.LayoutM21 or
        UiAnimationProperty.LayoutM22 or
        UiAnimationProperty.LayoutM31 or
        UiAnimationProperty.LayoutM32;

    public static float Constrain(UiAnimationProperty property, float value)
    {
        if (!float.IsFinite(value))
            return DefaultValue(property);
        return property switch
        {
            UiAnimationProperty.Opacity => Math.Clamp(value, 0f, 1f),
            UiAnimationProperty.CornerRadius => Math.Max(0f, value),
            UiAnimationProperty.ScaleX or UiAnimationProperty.ScaleY => Math.Max(0.0001f, value),
            _ => value
        };
    }

    public static float DefaultValue(UiAnimationProperty property) => property switch
    {
        UiAnimationProperty.Opacity or
        UiAnimationProperty.ScaleX or
        UiAnimationProperty.ScaleY or
        UiAnimationProperty.LayoutM11 or
        UiAnimationProperty.LayoutM22 => 1f,
        _ => 0f
    };

    private static ComputedVisual CreateVisual(UiWorld world, UiEntity entity)
    {
        ResolvedStyle style = world.Components.TryGet(entity, out ResolvedStyle resolved)
            ? resolved
            : ResolvedStyle.Default;
        return ComputedVisual.FromResolved(in style);
    }

    private static Matrix3x2 ReadLayoutMatrix(UiWorld world, UiEntity entity) =>
        world.Components.TryGet(entity, out ComputedLayoutTransform transform)
            ? transform.Value
            : Matrix3x2.Identity;

    private static void WriteLayoutMatrix(
        UiWorld world,
        UiEntity entity,
        UiAnimationProperty property,
        float value)
    {
        Matrix3x2 matrix = ReadLayoutMatrix(world, entity);
        float previous = property switch
        {
            UiAnimationProperty.LayoutM11 => matrix.M11,
            UiAnimationProperty.LayoutM12 => matrix.M12,
            UiAnimationProperty.LayoutM21 => matrix.M21,
            UiAnimationProperty.LayoutM22 => matrix.M22,
            UiAnimationProperty.LayoutM31 => matrix.M31,
            UiAnimationProperty.LayoutM32 => matrix.M32,
            _ => throw new ArgumentOutOfRangeException(nameof(property))
        };
        if (previous.Equals(value))
            return;
        switch (property)
        {
            case UiAnimationProperty.LayoutM11: matrix.M11 = value; break;
            case UiAnimationProperty.LayoutM12: matrix.M12 = value; break;
            case UiAnimationProperty.LayoutM21: matrix.M21 = value; break;
            case UiAnimationProperty.LayoutM22: matrix.M22 = value; break;
            case UiAnimationProperty.LayoutM31: matrix.M31 = value; break;
            case UiAnimationProperty.LayoutM32: matrix.M32 = value; break;
        }
        world.Set(entity, new ComputedLayoutTransform { Value = matrix });
        world.Dirty.Mark(entity, UiDirtyFlags.Transform | UiDirtyFlags.Render);
    }
}
