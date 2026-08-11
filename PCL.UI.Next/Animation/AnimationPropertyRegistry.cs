// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

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
            UiAnimationProperty.LayoutTranslateX => visual.LayoutTransform.TranslateX,
            UiAnimationProperty.LayoutTranslateY => visual.LayoutTransform.TranslateY,
            UiAnimationProperty.LayoutScaleX => visual.LayoutTransform.ScaleX,
            UiAnimationProperty.LayoutScaleY => visual.LayoutTransform.ScaleY,
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
            UiAnimationProperty.LayoutTranslateX or UiAnimationProperty.LayoutTranslateY => 0f,
            UiAnimationProperty.LayoutScaleX or UiAnimationProperty.LayoutScaleY => 1f,
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
            case UiAnimationProperty.LayoutTranslateX:
                if (visual.LayoutTransform.TranslateX.Equals(value)) return;
                visual.LayoutTransform = visual.LayoutTransform with { TranslateX = value };
                break;
            case UiAnimationProperty.LayoutTranslateY:
                if (visual.LayoutTransform.TranslateY.Equals(value)) return;
                visual.LayoutTransform = visual.LayoutTransform with { TranslateY = value };
                break;
            case UiAnimationProperty.LayoutScaleX:
                if (visual.LayoutTransform.ScaleX.Equals(value)) return;
                visual.LayoutTransform = visual.LayoutTransform with { ScaleX = value };
                break;
            case UiAnimationProperty.LayoutScaleY:
                if (visual.LayoutTransform.ScaleY.Equals(value)) return;
                visual.LayoutTransform = visual.LayoutTransform with { ScaleY = value };
                break;
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
        UiAnimationProperty.LayoutTranslateX or
        UiAnimationProperty.LayoutTranslateY or
        UiAnimationProperty.LayoutScaleX or
        UiAnimationProperty.LayoutScaleY;

    public static float Constrain(UiAnimationProperty property, float value)
    {
        if (!float.IsFinite(value))
            return DefaultValue(property);
        return property switch
        {
            UiAnimationProperty.Opacity => Math.Clamp(value, 0f, 1f),
            UiAnimationProperty.CornerRadius => Math.Max(0f, value),
            UiAnimationProperty.ScaleX or
            UiAnimationProperty.ScaleY or
            UiAnimationProperty.LayoutScaleX or
            UiAnimationProperty.LayoutScaleY => Math.Max(0.0001f, value),
            _ => value
        };
    }

    public static float DefaultValue(UiAnimationProperty property) => property switch
    {
        UiAnimationProperty.Opacity or
        UiAnimationProperty.ScaleX or
        UiAnimationProperty.ScaleY or
        UiAnimationProperty.LayoutScaleX or
        UiAnimationProperty.LayoutScaleY => 1f,
        _ => 0f
    };

    private static ComputedVisual CreateVisual(UiWorld world, UiEntity entity)
    {
        ResolvedStyle style = world.Components.TryGet(entity, out ResolvedStyle resolved)
            ? resolved
            : ResolvedStyle.Default;
        return ComputedVisual.FromResolved(in style);
    }
}
