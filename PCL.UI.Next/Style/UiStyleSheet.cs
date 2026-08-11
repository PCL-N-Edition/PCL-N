// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

public readonly struct UiStyleRule
{
    public UiStyleRule(
        UiClass styleClass,
        UiStyleValues values,
        InteractionState requiredState = InteractionState.None,
        InteractionState forbiddenState = InteractionState.None,
        int priority = 0)
    {
        ClassId = styleClass.Id;
        Values = values;
        RequiredState = requiredState;
        ForbiddenState = forbiddenState;
        Priority = priority;
    }

    public int ClassId { get; }
    public UiStyleValues Values { get; }
    public InteractionState RequiredState { get; }
    public InteractionState ForbiddenState { get; }
    public int Priority { get; }

    internal bool Matches(in StyleClassSet classes, InteractionState state) =>
        classes.Contains(ClassId) &&
        (state & RequiredState) == RequiredState &&
        (state & ForbiddenState) == 0;
}

/// <summary>Ordered static/dynamic class rules. Later equal-priority rules override earlier rules.</summary>
public sealed class UiStyleSheet
{
    private readonly List<UiStyleRule> _rules = [];
    private ulong _version;

    public event Action? Changed;

    public ulong Version => _version;

    internal IReadOnlyList<UiStyleRule> Rules => _rules;

    public void Add(UiStyleRule rule)
    {
        int insert = _rules.Count;
        for (int i = 0; i < _rules.Count; i++)
        {
            if (_rules[i].Priority > rule.Priority)
            {
                insert = i;
                break;
            }
        }

        _rules.Insert(insert, rule);
        unchecked
        {
            _version++;
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_rules.Count == 0)
            return;
        _rules.Clear();
        unchecked
        {
            _version++;
        }
        Changed?.Invoke();
    }

    internal bool DependsOn(in StyleClassSet classes, int tokenId)
    {
        for (int i = 0; i < _rules.Count; i++)
        {
            UiStyleRule rule = _rules[i];
            if (classes.Contains(rule.ClassId) && rule.Values.ReferencesToken(tokenId))
                return true;
        }

        return false;
    }
}

public static class UiDefaultStyles
{
    public static void Apply(UiStyleSheet styles)
    {
        ArgumentNullException.ThrowIfNull(styles);

        styles.Add(new UiStyleRule(
            UiClass.Body,
            default(UiStyleValues)
                .WithForeground(UiThemeTokens.TextPrimary)
                .WithFontSize(UiThemeTokens.FontBody)));

        styles.Add(new UiStyleRule(
            UiClass.PageTitle,
            default(UiStyleValues)
                .WithForeground(UiThemeTokens.TextPrimary)
                .WithFontSize(UiThemeTokens.FontTitle)
                .WithFontWeight(600)));

        styles.Add(new UiStyleRule(
            UiClass.Button,
            default(UiStyleValues)
                .WithBackground(UiThemeTokens.Surface)
                .WithForeground(UiThemeTokens.TextPrimary)
                .WithCornerRadius(UiThemeTokens.RadiusSmall)
                .WithPadding(new UiThickness(12f, 7f))
                .WithFontSize(UiThemeTokens.FontBody)));

        styles.Add(new UiStyleRule(
            UiClass.Button,
            default(UiStyleValues).WithBackground(UiThemeTokens.SurfaceHover),
            requiredState: InteractionState.Hovered,
            forbiddenState: InteractionState.Disabled,
            priority: 10));

        styles.Add(new UiStyleRule(
            UiClass.Button,
            default(UiStyleValues).WithOpacity(0.72f),
            requiredState: InteractionState.Disabled,
            priority: 20));

        styles.Add(new UiStyleRule(
            UiClass.Card,
            default(UiStyleValues)
                .WithBackground(UiThemeTokens.Surface)
                .WithCornerRadius(UiThemeTokens.RadiusCard)
                .WithPadding(new UiThickness(12f))));
    }
}
