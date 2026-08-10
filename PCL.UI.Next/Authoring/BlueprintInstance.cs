// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Live instantiation of a <see cref="UiBlueprint"/> inside a <see cref="UiWorld"/>.
/// Owned by a <see cref="UiScopeId"/> — dispose of the scope unregisters the instance immediately.
/// </summary>
public sealed class BlueprintInstance
{
    private IDisposable? _scopeRegistration;

    internal BlueprintInstance(
        int instanceId,
        UiBlueprint blueprint,
        UiScopeId scope,
        UiEntity[] entitiesByNode,
        BindingStamp[] bindingStamps,
        Dictionary<int, ulong> sliceVersions)
    {
        InstanceId = instanceId;
        Blueprint = blueprint;
        Scope = scope;
        EntitiesByNode = entitiesByNode;
        BindingStamps = bindingStamps;
        SliceVersions = sliceVersions;
    }

    public int InstanceId { get; }

    public UiBlueprint Blueprint { get; }

    public UiScopeId Scope { get; }

    public bool IsAlive { get; internal set; } = true;

    public UiEntity RootEntity =>
        EntitiesByNode.Length > Blueprint.RootIndex
            ? EntitiesByNode[Blueprint.RootIndex]
            : UiEntity.None;

    internal UiEntity[] EntitiesByNode { get; }

    internal BindingStamp[] BindingStamps { get; }

    /// <summary>Last observed presentation version per dependency slice for dispatch.</summary>
    internal Dictionary<int, ulong> SliceVersions { get; }

    public UiEntity EntityAt(int nodeIndex)
    {
        if ((uint)nodeIndex >= (uint)EntitiesByNode.Length)
            return UiEntity.None;
        return EntitiesByNode[nodeIndex];
    }

    internal void AttachScopeRegistration(IDisposable registration) =>
        _scopeRegistration = registration;

    internal void DetachScopeRegistration()
    {
        _scopeRegistration?.Dispose();
        _scopeRegistration = null;
    }
}
