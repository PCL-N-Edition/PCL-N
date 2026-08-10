// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Live instantiation of a <see cref="UiBlueprint"/> inside a <see cref="UiWorld"/>.
/// Owned by a <see cref="UiScopeId"/> — when the scope dies, the instance is invalid.
/// </summary>
public sealed class BlueprintInstance
{
    internal BlueprintInstance(
        int instanceId,
        UiBlueprint blueprint,
        UiScopeId scope,
        UiEntity[] entitiesByNode,
        BindingStamp[] bindingStamps)
    {
        InstanceId = instanceId;
        Blueprint = blueprint;
        Scope = scope;
        EntitiesByNode = entitiesByNode;
        BindingStamps = bindingStamps;
    }

    public int InstanceId { get; }

    public UiBlueprint Blueprint { get; }

    public UiScopeId Scope { get; }

    public bool IsAlive { get; internal set; } = true;

    public UiEntity RootEntity =>
        EntitiesByNode.Length > Blueprint.RootIndex
            ? EntitiesByNode[Blueprint.RootIndex]
            : UiEntity.None;

    /// <summary>Entity for each blueprint node index; <see cref="UiEntity.None"/> if not mounted.</summary>
    internal UiEntity[] EntitiesByNode { get; }

    /// <summary>Last successfully applied stamp per binding slot (version + entity).</summary>
    internal BindingStamp[] BindingStamps { get; }

    public UiEntity EntityAt(int nodeIndex)
    {
        if ((uint)nodeIndex >= (uint)EntitiesByNode.Length)
            return UiEntity.None;
        return EntitiesByNode[nodeIndex];
    }
}
