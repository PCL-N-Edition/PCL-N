// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Resolves class/state rules to a single target style and propagates precise dirtiness.</summary>
public sealed class StyleSystem : IUiSystem
{
    private readonly UiWorld _world;
    private readonly ThemeRegistry _theme;
    private readonly UiStyleSheet _styles;
    private readonly List<int> _changedTokens = [];
    private readonly List<UiEntity> _entities = [];
    private readonly List<UiEntity> _dirty = [];
    private ulong _styleSheetVersion;

    public StyleSystem(UiWorld world, ThemeRegistry theme, UiStyleSheet styles)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _styles = styles ?? throw new ArgumentNullException(nameof(styles));
        _theme.TokenChanged += OnExternalStyleChanged;
        _styles.Changed += OnExternalStyleChanged;
    }

    public UiSystemPhase Phase => UiSystemPhase.StyleResolve;

    public string Name => "style.resolve";

    public void Update(UiWorld world, in UiFrameContext frame)
    {
        _ = frame;
        _changedTokens.Clear();
        _theme.DrainChangedTokens(_changedTokens);

        bool sheetChanged = _styleSheetVersion != _styles.Version;
        if (sheetChanged || _changedTokens.Count > 0)
            MarkAffectedEntities(world, sheetChanged);
        _styleSheetVersion = _styles.Version;

        _dirty.Clear();
        world.Dirty.Collect(UiDirtyFlags.Style, _dirty);
        for (int i = 0; i < _dirty.Count; i++)
        {
            UiEntity entity = _dirty[i];
            if (!world.Entities.IsAlive(entity) ||
                (world.Dirty.GetFlags(entity) & UiDirtyFlags.Style) == 0 ||
                HasDirtyStyleParent(world, entity))
                continue;
            ResolveSubtree(world, entity);
        }
    }

    private void MarkAffectedEntities(UiWorld world, bool sheetChanged)
    {
        _entities.Clear();
        world.Components.Pool<StyleClassSet>().CopyEntitiesTo(_entities);
        for (int i = 0; i < _entities.Count; i++)
        {
            UiEntity entity = _entities[i];
            if (!world.Entities.IsAlive(entity) || !world.Components.TryGet(entity, out StyleClassSet classes))
                continue;

            bool affected = sheetChanged;
            for (int token = 0; !affected && token < _changedTokens.Count; token++)
                affected = _styles.DependsOn(in classes, _changedTokens[token]);
            if (affected)
                world.Dirty.Mark(entity, UiDirtyFlags.Style);
        }
    }

    private void ResolveSubtree(UiWorld world, UiEntity entity)
    {
        ResolveEntity(world, entity);
        if (!world.Hierarchy.TryGetNode(entity, out HierarchyNode node))
            return;
        UiEntity child = node.FirstChild;
        while (child != UiEntity.None)
        {
            UiEntity next = world.Hierarchy.TryGetNode(child, out HierarchyNode childNode)
                ? childNode.NextSibling
                : UiEntity.None;
            if (world.Entities.IsAlive(child))
                ResolveSubtree(world, child);
            child = next;
        }
    }

    private void ResolveEntity(UiWorld world, UiEntity entity)
    {
        if (!world.Entities.IsAlive(entity))
            return;

        ResolvedStyle previous = world.Components.TryGet(entity, out ResolvedStyle current)
            ? current
            : ResolvedStyle.Default;
        ResolvedStyle resolved = CreateInheritedBase(world, entity);
        InteractionState state = world.Components.TryGet(entity, out InteractionStateComponent interaction)
            ? interaction.Value
            : InteractionState.None;

        if (world.Components.TryGet(entity, out StyleClassSet classes))
        {
            IReadOnlyList<UiStyleRule> rules = _styles.Rules;
            for (int i = 0; i < rules.Count; i++)
            {
                UiStyleRule rule = rules[i];
                if (rule.Matches(in classes, state))
                {
                    UiStyleValues values = rule.Values;
                    Apply(ref resolved, in values);
                }
            }
        }

        world.Set(entity, resolved);
        world.Dirty.Clear(entity, UiDirtyFlags.Style);
        if (previous.Equals(resolved))
            return;

        bool textMetricsChanged = previous.FontSize != resolved.FontSize ||
                                  previous.FontWeight != resolved.FontWeight ||
                                  previous.FontFamilyId != resolved.FontFamilyId;
        bool layoutChanged = previous.Padding != resolved.Padding || textMetricsChanged;

        UiDirtyFlags dirty = UiDirtyFlags.Render;
        if (textMetricsChanged && world.Components.Has<TextContent>(entity))
            dirty |= UiDirtyFlags.TextMeasure;
        world.Dirty.Mark(entity, dirty);
        if (layoutChanged)
            LayoutInvalidation.MarkMeasure(world, entity, requestFrame: false);
    }

    private static ResolvedStyle CreateInheritedBase(UiWorld world, UiEntity entity)
    {
        ResolvedStyle resolved = ResolvedStyle.Default;
        if (!world.Hierarchy.TryGetNode(entity, out HierarchyNode node) ||
            node.Parent == UiEntity.None ||
            !world.Components.TryGet(node.Parent, out ResolvedStyle parent))
        {
            return resolved;
        }

        resolved.Foreground = parent.Foreground;
        resolved.FontSize = parent.FontSize;
        resolved.FontWeight = parent.FontWeight;
        resolved.FontFamilyId = parent.FontFamilyId;
        return resolved;
    }

    private static bool HasDirtyStyleParent(UiWorld world, UiEntity entity)
    {
        if (!world.Hierarchy.TryGetNode(entity, out HierarchyNode node) || node.Parent == UiEntity.None)
            return false;
        return (world.Dirty.GetFlags(node.Parent) & UiDirtyFlags.Style) != 0;
    }

    private void Apply(ref ResolvedStyle target, in UiStyleValues values)
    {
        UiStyleProperty defined = values.Defined;
        if ((defined & UiStyleProperty.Background) != 0) target.Background = values.Background.Resolve(_theme);
        if ((defined & UiStyleProperty.Foreground) != 0) target.Foreground = values.Foreground.Resolve(_theme);
        if ((defined & UiStyleProperty.Opacity) != 0) target.Opacity = values.Opacity.Resolve(_theme);
        if ((defined & UiStyleProperty.CornerRadius) != 0) target.CornerRadius = values.CornerRadius.Resolve(_theme);
        if ((defined & UiStyleProperty.Padding) != 0) target.Padding = values.Padding.Resolve(_theme);
        if ((defined & UiStyleProperty.FontSize) != 0) target.FontSize = values.FontSize.Resolve(_theme);
        if ((defined & UiStyleProperty.FontWeight) != 0) target.FontWeight = values.FontWeight.Resolve(_theme);
        if ((defined & UiStyleProperty.FontFamily) != 0) target.FontFamilyId = values.FontFamily.Resolve(_theme);
    }

    private void OnExternalStyleChanged() => _world.Scheduler.RequestReactiveFrame();

    private void OnExternalStyleChanged(int tokenId)
    {
        _ = tokenId;
        _world.Scheduler.RequestReactiveFrame();
    }
}
