// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

public enum UiEasingKind : byte
{
    Linear = 0,
    EaseIn = 1,
    EaseOut = 2,
    EaseInOut = 3,
    CubicBezier = 4
}

public readonly record struct UiEasing(
    UiEasingKind Kind,
    float X1 = 0f,
    float Y1 = 0f,
    float X2 = 1f,
    float Y2 = 1f)
{
    public static UiEasing Linear => new(UiEasingKind.Linear);
    public static UiEasing Standard => new(UiEasingKind.CubicBezier, 0.2f, 0f, 0f, 1f);
    public static UiEasing Emphasized => new(UiEasingKind.CubicBezier, 0.2f, 0f, 0f, 1f);
    public static UiEasing Decelerate => new(UiEasingKind.CubicBezier, 0f, 0f, 0f, 1f);
    public static UiEasing Accelerate => new(UiEasingKind.CubicBezier, 0.3f, 0f, 1f, 1f);

    public float Evaluate(float progress)
    {
        float x = Math.Clamp(progress, 0f, 1f);
        return Kind switch
        {
            UiEasingKind.Linear => x,
            UiEasingKind.EaseIn => x * x,
            UiEasingKind.EaseOut => 1f - ((1f - x) * (1f - x)),
            UiEasingKind.EaseInOut => x * x * (3f - (2f * x)),
            UiEasingKind.CubicBezier => EvaluateBezier(x),
            _ => x
        };
    }

    public float Derivative(float progress)
    {
        float x = Math.Clamp(progress, 0f, 1f);
        return Kind switch
        {
            UiEasingKind.Linear => 1f,
            UiEasingKind.EaseIn => 2f * x,
            UiEasingKind.EaseOut => 2f * (1f - x),
            UiEasingKind.EaseInOut => 6f * x * (1f - x),
            UiEasingKind.CubicBezier => DerivativeBezier(x),
            _ => 1f
        };
    }

    private float EvaluateBezier(float progress)
    {
        float parameter = SolveBezierParameter(progress);
        return Cubic(parameter, Y1, Y2);
    }

    private float DerivativeBezier(float progress)
    {
        float parameter = SolveBezierParameter(progress);
        float dx = CubicDerivative(parameter, X1, X2);
        if (MathF.Abs(dx) < 0.00001f)
            return 0f;
        return CubicDerivative(parameter, Y1, Y2) / dx;
    }

    private float SolveBezierParameter(float x)
    {
        float parameter = x;
        for (int i = 0; i < 5; i++)
        {
            float error = Cubic(parameter, X1, X2) - x;
            float derivative = CubicDerivative(parameter, X1, X2);
            if (MathF.Abs(derivative) < 0.00001f)
                break;
            parameter = Math.Clamp(parameter - (error / derivative), 0f, 1f);
        }

        float low = 0f;
        float high = 1f;
        for (int i = 0; i < 8; i++)
        {
            float value = Cubic(parameter, X1, X2);
            if (MathF.Abs(value - x) < 0.00001f)
                break;
            if (value < x)
                low = parameter;
            else
                high = parameter;
            parameter = (low + high) * 0.5f;
        }

        return parameter;
    }

    private static float Cubic(float t, float p1, float p2)
    {
        float inverse = 1f - t;
        return (3f * inverse * inverse * t * p1) +
               (3f * inverse * t * t * p2) +
               (t * t * t);
    }

    private static float CubicDerivative(float t, float p1, float p2)
    {
        float inverse = 1f - t;
        return (3f * inverse * inverse * p1) +
               (6f * inverse * t * (p2 - p1)) +
               (3f * t * t * (1f - p2));
    }
}

public readonly record struct UiMotionDefinition(
    UiAnimationSolverKind Solver,
    UiAnimationContinuity Continuity,
    float DurationSeconds,
    UiEasing Easing,
    float SpringResponse,
    float SpringDampingRatio,
    float DecayFriction,
    float PositionTolerance = 0.001f,
    float VelocityTolerance = 0.001f)
{
    public static UiMotionDefinition Immediate => new(
        UiAnimationSolverKind.Immediate,
        UiAnimationContinuity.ContinueFromCurrent,
        0f,
        UiEasing.Linear,
        0f,
        1f,
        0f);
}

/// <summary>Stable semantic motion-token lookup shared by all animation channels.</summary>
public sealed class UiMotionRegistry
{
    private readonly Dictionary<int, UiMotionDefinition> _definitions = [];

    public UiMotionRegistry() => RegisterDefaults();

    public void Set(UiMotionToken token, in UiMotionDefinition definition)
    {
        if (token.IsNone)
            throw new ArgumentOutOfRangeException(nameof(token));
        Validate(in definition);
        _definitions[token.Id] = definition;
    }

    public UiMotionDefinition Get(UiMotionToken token)
    {
        if (token.IsNone || !_definitions.TryGetValue(token.Id, out UiMotionDefinition definition))
            throw new KeyNotFoundException("Motion token is not registered: " + token.Id);
        return definition;
    }

    internal UiMotionDefinition Resolve(
        UiMotionToken token,
        UiAnimationFlags flags,
        bool animationsEnabled,
        bool reducedMotion)
    {
        if (!animationsEnabled)
            return UiMotionDefinition.Immediate;

        UiMotionDefinition definition = Get(token);
        if (!reducedMotion || (flags & UiAnimationFlags.AllowReducedMotion) != 0)
            return definition;
        if ((flags & UiAnimationFlags.Essential) == 0)
            return UiMotionDefinition.Immediate;

        return definition with
        {
            DurationSeconds = Math.Min(definition.DurationSeconds, 0.08f),
            SpringResponse = definition.SpringResponse <= 0f
                ? 0f
                : Math.Min(definition.SpringResponse, 0.18f),
            SpringDampingRatio = Math.Max(1f, definition.SpringDampingRatio)
        };
    }

    private void RegisterDefaults()
    {
        Set(UiMotion.Instant, UiMotionDefinition.Immediate);
        Set(UiMotion.FastFade, Tween(0.10f, UiEasing.Standard));
        Set(UiMotion.Standard, Tween(0.20f, UiEasing.Standard));
        Set(UiMotion.Emphasized, Tween(0.30f, UiEasing.Emphasized));
        Set(UiMotion.Hover, Spring(response: 0.24f, dampingRatio: 1f));
        Set(UiMotion.Press, Spring(response: 0.18f, dampingRatio: 1f));
        Set(UiMotion.Navigation, Spring(response: 0.40f, dampingRatio: 1f));
        Set(UiMotion.Overlay, Tween(0.20f, UiEasing.Decelerate));
        Set(UiMotion.Layout, Spring(response: 0.35f, dampingRatio: 1f));
        Set(UiMotion.SpringExpressive, Spring(response: 0.40f, dampingRatio: 0.8f));
        Set(UiMotion.Scroll, new UiMotionDefinition(
            UiAnimationSolverKind.Decay,
            UiAnimationContinuity.MergeVelocity,
            0f,
            UiEasing.Linear,
            0f,
            1f,
            8f,
            PositionTolerance: 0.01f,
            VelocityTolerance: 0.5f));
    }

    private static UiMotionDefinition Tween(float duration, UiEasing easing) => new(
        UiAnimationSolverKind.Tween,
        UiAnimationContinuity.ContinueFromCurrent,
        duration,
        easing,
        0f,
        1f,
        0f);

    private static UiMotionDefinition Spring(float response, float dampingRatio) => new(
        UiAnimationSolverKind.Spring,
        UiAnimationContinuity.PreserveVelocity,
        0f,
        UiEasing.Linear,
        response,
        dampingRatio,
        0f);

    private static void Validate(in UiMotionDefinition definition)
    {
        if (definition.DurationSeconds < 0f || !float.IsFinite(definition.DurationSeconds))
            throw new ArgumentOutOfRangeException(nameof(definition));
        if (definition.SpringResponse < 0f || !float.IsFinite(definition.SpringResponse))
            throw new ArgumentOutOfRangeException(nameof(definition));
        if (definition.SpringDampingRatio < 0f || !float.IsFinite(definition.SpringDampingRatio))
            throw new ArgumentOutOfRangeException(nameof(definition));
        if (definition.DecayFriction < 0f || !float.IsFinite(definition.DecayFriction))
            throw new ArgumentOutOfRangeException(nameof(definition));
        if (definition.PositionTolerance <= 0f || !float.IsFinite(definition.PositionTolerance))
            throw new ArgumentOutOfRangeException(nameof(definition));
        if (definition.VelocityTolerance <= 0f || !float.IsFinite(definition.VelocityTolerance))
            throw new ArgumentOutOfRangeException(nameof(definition));
    }
}
