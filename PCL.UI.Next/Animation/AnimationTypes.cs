// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Numerics;

namespace PCL.UI.Next;

public enum UiAnimationSolverKind : byte
{
    Immediate = 0,
    Tween = 1,
    Spring = 2,
    Decay = 3,
    Direct = 4
}

public enum UiAnimationContinuity : byte
{
    Restart = 0,
    ContinueFromCurrent = 1,
    PreserveRemainingRatio = 2,
    PreserveSpeed = 3,
    PreserveVelocity = 4,
    MergeVelocity = 5
}

public enum UiAnimationProperty : byte
{
    None = 0,
    Opacity = 1,
    CornerRadius = 2,
    TranslateX = 3,
    TranslateY = 4,
    ScaleX = 5,
    ScaleY = 6,
    Rotation = 7,
    LayoutTranslateX = 8,
    LayoutTranslateY = 9,
    LayoutScaleX = 10,
    LayoutScaleY = 11
}

public enum UiAnimationOwnerReason : byte
{
    Programmatic = 0,
    StyleTransition = 1,
    LayoutTransition = 2,
    Navigation = 3,
    Gesture = 4,
    Scroll = 5
}

[Flags]
public enum UiAnimationFlags : byte
{
    None = 0,
    Essential = 1 << 0,
    AllowReducedMotion = 1 << 1,
    AllowRebase = 1 << 2
}

public enum UiAnimationCancelMode : byte
{
    SnapToCurrent = 0,
    SnapToTarget = 1,
    Discard = 2
}

public readonly record struct UiAnimationHandle(int Index, uint Generation)
{
    public static UiAnimationHandle None => default;

    public bool IsNone => Index == 0 || Generation == 0;
}

public readonly record struct UiTransitionGroupId(int Index, uint Generation)
{
    public static UiTransitionGroupId None => default;

    public bool IsNone => Index == 0 || Generation == 0;
}

public readonly record struct UiMotionToken(int Id)
{
    public bool IsNone => Id == 0;
}

public static class UiMotion
{
    public static UiMotionToken Instant { get; } = new(1);
    public static UiMotionToken FastFade { get; } = new(2);
    public static UiMotionToken Standard { get; } = new(3);
    public static UiMotionToken Emphasized { get; } = new(4);
    public static UiMotionToken Hover { get; } = new(5);
    public static UiMotionToken Press { get; } = new(6);
    public static UiMotionToken Navigation { get; } = new(7);
    public static UiMotionToken Overlay { get; } = new(8);
    public static UiMotionToken Layout { get; } = new(9);
    public static UiMotionToken SpringExpressive { get; } = new(10);
    public static UiMotionToken Scroll { get; } = new(11);
}

public readonly struct UiAnimationSpec
{
    public UiAnimationSpec(
        UiMotionToken motion,
        UiAnimationFlags flags = UiAnimationFlags.None,
        UiAnimationOwnerReason owner = UiAnimationOwnerReason.Programmatic)
    {
        Motion = motion;
        Continuity = default;
        HasContinuityOverride = false;
        Flags = flags;
        Owner = owner;
    }

    public UiAnimationSpec(
        UiMotionToken motion,
        UiAnimationContinuity continuity,
        UiAnimationFlags flags = UiAnimationFlags.None,
        UiAnimationOwnerReason owner = UiAnimationOwnerReason.Programmatic)
    {
        Motion = motion;
        Continuity = continuity;
        HasContinuityOverride = true;
        Flags = flags;
        Owner = owner;
    }

    public UiMotionToken Motion { get; }
    public UiAnimationContinuity Continuity { get; }
    public bool HasContinuityOverride { get; }
    public UiAnimationFlags Flags { get; }
    public UiAnimationOwnerReason Owner { get; }
}

public readonly struct UiTransitionDefinition
{
    public UiTransitionDefinition(UiAnimationProperty property, UiMotionToken motion)
    {
        if (property is <= UiAnimationProperty.None or > UiAnimationProperty.Rotation)
            throw new ArgumentOutOfRangeException(nameof(property));
        if (motion.IsNone)
            throw new ArgumentOutOfRangeException(nameof(motion));
        Property = property;
        Motion = motion;
        Continuity = default;
        HasContinuityOverride = false;
    }

    public UiTransitionDefinition(
        UiAnimationProperty property,
        UiMotionToken motion,
        UiAnimationContinuity continuity)
        : this(property, motion)
    {
        Continuity = continuity;
        HasContinuityOverride = true;
    }

    public UiAnimationProperty Property { get; }
    public UiMotionToken Motion { get; }
    public UiAnimationContinuity Continuity { get; }
    public bool HasContinuityOverride { get; }

    public UiAnimationSpec ToSpec(UiAnimationOwnerReason owner) =>
        HasContinuityOverride
            ? new UiAnimationSpec(Motion, Continuity, owner: owner)
            : new UiAnimationSpec(Motion, owner: owner);
}

/// <summary>Allocation-free inline transition declarations for one runtime entity.</summary>
public struct UiTransitionSet
{
    public const int MaxCount = 8;

    private UiTransitionDefinition _item0;
    private UiTransitionDefinition _item1;
    private UiTransitionDefinition _item2;
    private UiTransitionDefinition _item3;
    private UiTransitionDefinition _item4;
    private UiTransitionDefinition _item5;
    private UiTransitionDefinition _item6;
    private UiTransitionDefinition _item7;

    public int Count { get; private set; }

    public void Set(in UiTransitionDefinition definition)
    {
        for (int i = 0; i < Count; i++)
        {
            if (Get(i).Property != definition.Property)
                continue;
            SetAt(i, in definition);
            return;
        }

        if (Count >= MaxCount)
            throw new InvalidOperationException($"A node may declare at most {MaxCount} transitions.");
        SetAt(Count, in definition);
        Count++;
    }

    public bool TryGet(UiAnimationProperty property, out UiTransitionDefinition definition)
    {
        for (int i = 0; i < Count; i++)
        {
            UiTransitionDefinition current = Get(i);
            if (current.Property == property)
            {
                definition = current;
                return true;
            }
        }

        definition = default;
        return false;
    }

    public UiTransitionDefinition Get(int index) => index switch
    {
        0 when index < Count => _item0,
        1 when index < Count => _item1,
        2 when index < Count => _item2,
        3 when index < Count => _item3,
        4 when index < Count => _item4,
        5 when index < Count => _item5,
        6 when index < Count => _item6,
        7 when index < Count => _item7,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    private void SetAt(int index, in UiTransitionDefinition definition)
    {
        switch (index)
        {
            case 0: _item0 = definition; break;
            case 1: _item1 = definition; break;
            case 2: _item2 = definition; break;
            case 3: _item3 = definition; break;
            case 4: _item4 = definition; break;
            case 5: _item5 = definition; break;
            case 6: _item6 = definition; break;
            case 7: _item7 = definition; break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}

public struct TransitionSetComponent
{
    public UiTransitionSet Value { get; set; }
}

public struct LayoutTransitionComponent
{
    public UiMotionToken Motion { get; set; }
}

public readonly record struct UiVisualTransform(
    float TranslateX,
    float TranslateY,
    float ScaleX,
    float ScaleY,
    float Rotation)
{
    public static UiVisualTransform Identity => new(0f, 0f, 1f, 1f, 0f);
}

/// <summary>Runtime-authoritative current visual values. ResolvedStyle remains the target.</summary>
public struct ComputedVisual
{
    public UiColor Background { get; set; }
    public UiColor Foreground { get; set; }
    public float Opacity { get; set; }
    public float CornerRadius { get; set; }
    public UiVisualTransform Transform { get; set; }
    public UiVisualTransform LayoutTransform { get; set; }

    public static ComputedVisual FromResolved(in ResolvedStyle style) => new()
    {
        Background = style.Background,
        Foreground = style.Foreground,
        Opacity = style.Opacity,
        CornerRadius = style.CornerRadius,
        Transform = new UiVisualTransform(
            style.TranslateX,
            style.TranslateY,
            style.ScaleX,
            style.ScaleY,
            style.Rotation),
        LayoutTransform = UiVisualTransform.Identity
    };
}

/// <summary>Current world-space transform consumed by hit testing and the future renderer.</summary>
public struct ComputedTransform
{
    public Matrix3x2 Value { get; set; }

    public static ComputedTransform Identity => new() { Value = Matrix3x2.Identity };
}

public readonly record struct UiAnimationSettled(
    UiAnimationHandle Channel,
    UiEntity Entity,
    UiAnimationProperty Property,
    uint TargetGeneration,
    float Target,
    UiScopeId Scope);

public readonly record struct UiTransitionGroupCompleted(
    UiTransitionGroupId Group,
    UiScopeId Scope);

public readonly record struct UiAnimationSnapshot(
    UiAnimationHandle Channel,
    UiEntity Entity,
    UiAnimationProperty Property,
    float Current,
    float Target,
    float Velocity,
    UiAnimationSolverKind Solver,
    UiAnimationContinuity Continuity,
    UiMotionToken Motion,
    uint TargetGeneration,
    UiScopeId Scope,
    UiAnimationOwnerReason Owner,
    bool IsActive);
