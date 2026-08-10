// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Hierarchical UiScope allocator. Disposing a scope destroys owned entities via callback
/// and invalidates the scope generation for async safety.
/// </summary>
public sealed class ScopeRegistry
{
    private const int InitialCapacity = 16;

    private uint[] _generations = new uint[InitialCapacity];
    private bool[] _alive = new bool[InitialCapacity];
    private UiScopeId[] _parents = new UiScopeId[InitialCapacity];
    private List<int>?[] _children = new List<int>?[InitialCapacity];
    private readonly Stack<int> _free = new();
    private int _highWater = 1;
    private int _aliveCount;

    public int AliveCount => _aliveCount;

    public UiScopeId CreateRoot()
    {
        int index = AllocateSlot();
        uint generation = EnsureGeneration(index);
        _alive[index] = true;
        _parents[index] = UiScopeId.None;
        EnsureChildList(index).Clear();
        _aliveCount++;
        return new UiScopeId(index, generation);
    }

    public UiScopeId Create(UiScopeId parent)
    {
        if (!IsAlive(parent))
            throw new InvalidOperationException("Parent scope is not alive: " + parent);

        int index = AllocateSlot();
        uint generation = EnsureGeneration(index);
        _alive[index] = true;
        _parents[index] = parent;
        EnsureChildList(index).Clear();
        EnsureChildList(parent.Index).Add(index);
        _aliveCount++;
        return new UiScopeId(index, generation);
    }

    public bool IsAlive(UiScopeId scope)
    {
        if (scope.IsNone || scope.Index <= 0 || scope.Index >= _highWater)
            return false;
        return _alive[scope.Index] && _generations[scope.Index] == scope.Generation;
    }

    public bool TryGetParent(UiScopeId scope, out UiScopeId parent)
    {
        if (!IsAlive(scope))
        {
            parent = UiScopeId.None;
            return false;
        }

        parent = _parents[scope.Index];
        return true;
    }

    /// <summary>
    /// Disposes scope and descendants (children first). Invokes
    /// <paramref name="onDispose"/> once per disposed scope before invalidating it.
    /// </summary>
    public bool Dispose(UiScopeId scope, Action<UiScopeId>? onDispose = null)
    {
        if (!IsAlive(scope))
            return false;

        // Collect post-order list of scopes.
        List<UiScopeId> order = [];
        CollectSubtreePostOrder(scope, order);

        foreach (UiScopeId current in order)
        {
            if (!IsAlive(current))
                continue;
            onDispose?.Invoke(current);
            InvalidateSlot(current.Index);
        }

        return true;
    }

    public void AppendChildren(UiScopeId parent, List<UiScopeId> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!IsAlive(parent))
            return;
        List<int> children = EnsureChildList(parent.Index);
        foreach (int childIndex in children)
        {
            if (_alive[childIndex])
                destination.Add(new UiScopeId(childIndex, _generations[childIndex]));
        }
    }

    private void CollectSubtreePostOrder(UiScopeId root, List<UiScopeId> order)
    {
        if (!IsAlive(root))
            return;
        foreach (int childIndex in EnsureChildList(root.Index).ToArray())
        {
            if (_alive[childIndex])
                CollectSubtreePostOrder(new UiScopeId(childIndex, _generations[childIndex]), order);
        }

        order.Add(root);
    }

    private List<int> EnsureChildList(int index)
    {
        List<int>? list = _children[index];
        if (list is not null)
            return list;
        list = [];
        _children[index] = list;
        return list;
    }

    private void InvalidateSlot(int index)
    {
        UiScopeId parent = _parents[index];
        if (!parent.IsNone && parent.Index > 0 && parent.Index < _highWater && _children[parent.Index] is { } siblings)
            siblings.Remove(index);

        _alive[index] = false;
        _parents[index] = UiScopeId.None;
        _children[index]?.Clear();
        uint nextGen = unchecked(_generations[index] + 1);
        if (nextGen == 0)
            nextGen = 1;
        _generations[index] = nextGen;
        _free.Push(index);
        _aliveCount--;
    }

    private int AllocateSlot()
    {
        int index;
        if (_free.Count > 0)
        {
            index = _free.Pop();
        }
        else
        {
            if (_highWater >= _generations.Length)
                Grow(_generations.Length * 2);
            index = _highWater++;
        }

        return index;
    }

    private uint EnsureGeneration(int index)
    {
        uint generation = _generations[index];
        if (generation == 0)
            generation = 1;
        _generations[index] = generation;
        return generation;
    }

    private void Grow(int newCapacity)
    {
        Array.Resize(ref _generations, newCapacity);
        Array.Resize(ref _alive, newCapacity);
        Array.Resize(ref _parents, newCapacity);
        Array.Resize(ref _children, newCapacity);
    }
}
