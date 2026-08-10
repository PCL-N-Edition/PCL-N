// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Logical entity hierarchy with O(1) attach/detach of sibling links.
/// Structural mutations bump <see cref="StructuralVersion"/>.
/// </summary>
public sealed class HierarchyStore
{
    private readonly EntityRegistry _entities;
    private readonly ComponentPool<HierarchyNode> _nodes = new();
    private uint _structuralVersion;

    public HierarchyStore(EntityRegistry entities)
    {
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
    }

    public uint StructuralVersion => _structuralVersion;

    public bool HasNode(UiEntity entity) => _nodes.Has(entity);

    public ref HierarchyNode GetNode(UiEntity entity) => ref _nodes.Get(entity);

    public bool TryGetNode(UiEntity entity, out HierarchyNode node) => _nodes.TryGet(entity, out node);

    /// <summary>Ensures a root (parentless) hierarchy node exists for the entity.</summary>
    public void EnsureRoot(UiEntity entity)
    {
        EnsureAlive(entity);
        if (_nodes.Has(entity))
            return;
        _nodes.Add(entity, new HierarchyNode { Depth = 0 });
        BumpStructure();
    }

    public void AttachChild(UiEntity parent, UiEntity child)
    {
        EnsureAlive(parent);
        EnsureAlive(child);
        if (parent == child)
            throw new InvalidOperationException("Cannot attach entity as its own child.");
        if (IsAncestorOf(child, parent))
            throw new InvalidOperationException("Cannot attach a node under its own descendant (cycle).");

        if (_nodes.Has(child) && _nodes.Get(child).Parent != UiEntity.None)
            Detach(child);

        EnsureRoot(parent);
        if (!_nodes.Has(child))
            _nodes.Add(child, default);

        ref HierarchyNode parentNode = ref _nodes.Get(parent);
        ref HierarchyNode childNode = ref _nodes.Get(child);

        childNode.Parent = parent;
        childNode.PreviousSibling = parentNode.LastChild;
        childNode.NextSibling = UiEntity.None;
        childNode.Depth = unchecked((ushort)(parentNode.Depth + 1));

        if (parentNode.LastChild == UiEntity.None)
        {
            parentNode.FirstChild = child;
            parentNode.LastChild = child;
        }
        else
        {
            ref HierarchyNode prev = ref _nodes.Get(parentNode.LastChild);
            prev.NextSibling = child;
            parentNode.LastChild = child;
        }

        UpdateDepthRecursive(child);
        BumpStructure();
    }

    public void Detach(UiEntity entity)
    {
        if (!_nodes.Has(entity))
            return;

        ref HierarchyNode node = ref _nodes.Get(entity);
        UiEntity parent = node.Parent;
        if (parent != UiEntity.None && _nodes.Has(parent))
        {
            ref HierarchyNode parentNode = ref _nodes.Get(parent);
            if (node.PreviousSibling != UiEntity.None)
                _nodes.Get(node.PreviousSibling).NextSibling = node.NextSibling;
            else
                parentNode.FirstChild = node.NextSibling;

            if (node.NextSibling != UiEntity.None)
                _nodes.Get(node.NextSibling).PreviousSibling = node.PreviousSibling;
            else
                parentNode.LastChild = node.PreviousSibling;
        }

        node.Parent = UiEntity.None;
        node.PreviousSibling = UiEntity.None;
        node.NextSibling = UiEntity.None;
        node.Depth = 0;
        UpdateDepthRecursive(entity);
        BumpStructure();
    }

    /// <summary>
    /// Detaches entity from parent and destroys the entire subtree (deepest-first),
    /// invoking <paramref name="destroyEntity"/> for each node including root.
    /// </summary>
    public void DestroySubtree(UiEntity root, Action<UiEntity> destroyEntity)
    {
        ArgumentNullException.ThrowIfNull(destroyEntity);
        if (!_nodes.Has(root) && !_entities.IsAlive(root))
            return;

        Detach(root);

        // Post-order: children first.
        List<UiEntity> stack = [root];
        List<UiEntity> order = [];
        while (stack.Count > 0)
        {
            UiEntity current = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            order.Add(current);
            if (_nodes.Has(current))
            {
                UiEntity child = _nodes.Get(current).FirstChild;
                while (child != UiEntity.None)
                {
                    stack.Add(child);
                    child = _nodes.Has(child) ? _nodes.Get(child).NextSibling : UiEntity.None;
                }
            }
        }

        // deepest-first; destroyEntity is responsible for pool / registry / node cleanup
        for (int i = order.Count - 1; i >= 0; i--)
            destroyEntity(order[i]);

        BumpStructure();
    }

    public void EnumerateChildren(UiEntity parent, List<UiEntity> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!_nodes.Has(parent))
            return;
        UiEntity child = _nodes.Get(parent).FirstChild;
        while (child != UiEntity.None)
        {
            destination.Add(child);
            child = _nodes.Has(child) ? _nodes.Get(child).NextSibling : UiEntity.None;
        }
    }

    public void EnumerateAncestors(UiEntity entity, List<UiEntity> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        UiEntity current = entity;
        while (_nodes.Has(current))
        {
            UiEntity parent = _nodes.Get(current).Parent;
            if (parent == UiEntity.None)
                break;
            destination.Add(parent);
            current = parent;
        }
    }

    public void RemoveNode(UiEntity entity)
    {
        if (!_nodes.Has(entity))
            return;
        Detach(entity);
        // Also detach children links without destroying entities.
        if (_nodes.Has(entity))
        {
            List<UiEntity> children = [];
            EnumerateChildren(entity, children);
            foreach (UiEntity child in children)
                Detach(child);
            _nodes.Remove(entity);
            BumpStructure();
        }
    }

    private void UpdateDepthRecursive(UiEntity entity)
    {
        if (!_nodes.Has(entity))
            return;
        ref HierarchyNode node = ref _nodes.Get(entity);
        ushort depth = 0;
        if (node.Parent != UiEntity.None && _nodes.Has(node.Parent))
            depth = unchecked((ushort)(_nodes.Get(node.Parent).Depth + 1));
        node.Depth = depth;

        UiEntity child = node.FirstChild;
        while (child != UiEntity.None)
        {
            UpdateDepthRecursive(child);
            child = _nodes.Has(child) ? _nodes.Get(child).NextSibling : UiEntity.None;
        }
    }

    /// <summary>True when <paramref name="ancestor"/> is a strict ancestor of <paramref name="node"/>.</summary>
    private bool IsAncestorOf(UiEntity ancestor, UiEntity node)
    {
        UiEntity current = node;
        int guard = 0;
        while (_nodes.Has(current) && guard++ < 1_000_000)
        {
            UiEntity parent = _nodes.Get(current).Parent;
            if (parent == UiEntity.None)
                return false;
            if (parent == ancestor)
                return true;
            current = parent;
        }

        return false;
    }

    private void EnsureAlive(UiEntity entity)
    {
        if (!_entities.IsAlive(entity))
            throw new InvalidOperationException("Entity is not alive: " + entity);
    }

    private void BumpStructure()
    {
        unchecked
        {
            _structuralVersion++;
        }
    }
}
