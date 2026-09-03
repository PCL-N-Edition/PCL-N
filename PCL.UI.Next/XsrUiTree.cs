using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.UI.Next;

/// <summary>
/// One entity of the UI tree: identity, hierarchy, and an open component set. Trees are
/// single-threaded (render-thread owned) and deterministic: children keep attach order and
/// dirty enumeration runs in ascending entity-index order.
/// </summary>
public sealed class XsrUiTree
{
    private const XsrUiDirtyKinds LayoutRelevantKinds =
        XsrUiDirtyKinds.Structure | XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.State;

    private readonly Dictionary<int, XsrUiEntity> _entities = [];
    private readonly Dictionary<int, uint> _generations = [];
    private readonly Stack<int> _freeIndexes = [];
    private readonly Dictionary<XsrStateId, List<(int Entity, XsrUiStateDependency Dependency)>> _stateDependencies = [];
    private readonly Dictionary<int, List<XsrUiStateDependency>> _entityDependencies = [];
    private readonly SortedSet<int> _dirtyEntities = [];
    private int _nextIndex = 1;

    /// <summary>
    /// Raised when a renderable mutation marks the tree dirty. The event is notification-only:
    /// consumers must never inspect or mutate the tree from a foreign thread. Backends use it to
    /// schedule a render-thread frame after navigation, style, or template changes.
    /// </summary>
    public event EventHandler? RenderInvalidated;

    /// <summary>
    /// Gets the number of live entities.
    /// </summary>
    public int Count => _entities.Count;

    /// <summary>
    /// Creates one entity. The new entity has no parent and no components. Recycled indexes get
    /// a fresh generation, so stale handles stay dead.
    /// </summary>
    public XsrUiEntityId Create(string? name = null)
    {
        if (_freeIndexes.TryPop(out int index))
        {
            _generations[index]++;
        }
        else
        {
            index = _nextIndex++;
            _generations[index] = 1;
        }

        _entities[index] = new XsrUiEntity(name);
        return new XsrUiEntityId(index, _generations[index]);
    }

    /// <summary>
    /// Destroys one entity and its whole subtree: components, state bindings, dirty records,
    /// and hierarchy entries are released; the index is recycled with an advanced generation.
    /// </summary>
    public void Destroy(XsrUiEntityId entity)
    {
        XsrUiEntity destroyed = Require(entity);
        if (destroyed.Parent.IsAssigned)
        {
            XsrUiEntity parent = Require(destroyed.Parent);
            _ = parent.Children.Remove(entity.Index);
            MarkDirty(destroyed.Parent, XsrUiDirtyKinds.Structure);
        }

        DestroyDescendants(entity.Index);
        RemoveEntity(entity.Index, destroyed);
    }

    /// <summary>
    /// Attaches one entity as the last child of a parent. Attaching detaches the child from its
    /// previous parent first; cycles are rejected.
    /// </summary>
    public void Attach(XsrUiEntityId child, XsrUiEntityId parent)
    {
        XsrUiEntity childEntity = Require(child);
        XsrUiEntity parentEntity = Require(parent);

        if (child.Equals(parent))
        {
            throw new InvalidOperationException("An entity cannot be attached to itself.");
        }

        XsrUiEntityId ancestor = parent;
        while (ancestor.IsAssigned)
        {
            if (ancestor.Equals(child))
            {
                throw new InvalidOperationException("Attaching would create a hierarchy cycle.");
            }

            ancestor = Require(ancestor).Parent;
        }

        if (childEntity.Parent.IsAssigned)
        {
            Detach(child);
        }

        childEntity.Parent = parent;
        parentEntity.Children.Add(child.Index);
        MarkDirty(parent, XsrUiDirtyKinds.Structure);
        MarkDirty(child, XsrUiDirtyKinds.Structure);
    }

    /// <summary>
    /// Detaches one entity from its parent. The entity and its subtree stay alive.
    /// </summary>
    public void Detach(XsrUiEntityId entity)
    {
        XsrUiEntity detached = Require(entity);
        if (!detached.Parent.IsAssigned)
        {
            return;
        }

        XsrUiEntity parent = Require(detached.Parent);
        _ = parent.Children.Remove(entity.Index);
        XsrUiEntityId previousParent = detached.Parent;
        detached.Parent = default;
        MarkDirty(previousParent, XsrUiDirtyKinds.Structure);
        MarkDirty(entity, XsrUiDirtyKinds.Structure);
    }

    /// <summary>
    /// Gets the parent, or an unassigned handle for a root entity.
    /// </summary>
    public XsrUiEntityId Parent(XsrUiEntityId entity) => Require(entity).Parent;

    /// <summary>
    /// Gets the children in attach order.
    /// </summary>
    public IReadOnlyList<XsrUiEntityId> Children(XsrUiEntityId entity)
    {
        XsrUiEntity value = Require(entity);
        return [.. value.Children.Select(IndexToHandle)];
    }

    /// <summary>
    /// Gets a value indicating whether the handle refers to a live entity. A stale handle —
    /// same index as a recycled entity, older generation — is not alive.
    /// </summary>
    public bool IsAlive(XsrUiEntityId entity) =>
        entity.IsAssigned
        && _entities.ContainsKey(entity.Index)
        && _generations.TryGetValue(entity.Index, out uint generation)
        && generation == entity.Generation;

    /// <summary>
    /// Gets a value indicating whether an index currently hosts a live entity. Used for cache
    /// maintenance by index-keyed renderer caches.
    /// </summary>
    public bool IsIndexAlive(int index) => _entities.ContainsKey(index);

    /// <summary>
    /// Gets the diagnostic name assigned at creation.
    /// </summary>
    public string Name(XsrUiEntityId entity) => Require(entity).Name;

    /// <summary>
    /// Gets one component, or null when the entity does not carry it.
    /// </summary>
    public T? GetComponent<T>(XsrUiEntityId entity)
        where T : class
    {
        XsrUiEntity value = Require(entity);
        return value.Components.TryGetValue(typeof(T), out object? component) ? (T)component : null;
    }

    /// <summary>
    /// Sets or replaces one component; null removes it. Structure is marked dirty. State-carrying
    /// components maintain their binding records automatically, replacing their own previous record.
    /// </summary>
    public void SetComponent<T>(XsrUiEntityId entity, T? component)
        where T : class
    {
        XsrUiEntity value = Require(entity);

        if (component is null)
        {
            if (value.Components.Remove(typeof(T)))
            {
                UnbindComponent<T>(entity, value);
                MarkDirty(entity, XsrUiDirtyKinds.Structure);
            }

            return;
        }

        UnbindComponent<T>(entity, value);
        value.Components[typeof(T)] = component;
        if (component is XsrUiStateBinding binding)
        {
            BindState(entity, binding.Dependency);
        }
        else if (component is XsrUiText text)
        {
            BindState(entity, TextDependency(text.BoundState));
        }
        else if (component is XsrUiElement element)
        {
            BindState(entity, VisibilityDependency(element.BoundVisibility));
        }

        MarkDirty(entity, XsrUiDirtyKinds.Structure);
    }

    /// <summary>
    /// Marks one entity dirty and bubbles the subtree flags to every ancestor.
    /// </summary>
    public void MarkDirty(XsrUiEntityId entity, XsrUiDirtyKinds kinds)
    {
        if (kinds == XsrUiDirtyKinds.None)
        {
            return;
        }

        XsrUiEntity value = Require(entity);
        value.OwnDirty |= kinds;
        value.SubtreeDirty = true;
        bool layoutRelevant = (kinds & LayoutRelevantKinds) != 0;
        value.SubtreeLayoutDirty |= layoutRelevant;
        _dirtyEntities.Add(entity.Index);

        // Bubble each subtree flag until it reaches an ancestor that already carries it, so
        // paint-only dirt never triggers ancestor relayouts.
        bool anyPending = true;
        bool layoutPending = layoutRelevant;
        XsrUiEntityId ancestor = value.Parent;
        while (ancestor.IsAssigned && (anyPending || layoutPending))
        {
            XsrUiEntity current = Require(ancestor);
            if (anyPending)
            {
                anyPending = !current.SubtreeDirty;
                current.SubtreeDirty = true;
            }

            if (layoutPending)
            {
                layoutPending = !current.SubtreeLayoutDirty;
                current.SubtreeLayoutDirty = true;
            }

            ancestor = current.Parent;
        }

        RenderInvalidated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Clears one entity's own dirty flags and recomputes ancestor subtree flags.
    /// </summary>
    public void ClearDirty(XsrUiEntityId entity)
    {
        XsrUiEntity value = Require(entity);
        value.OwnDirty = XsrUiDirtyKinds.None;
        _ = _dirtyEntities.Remove(entity.Index);
        RecomputeSubtreeUpwards(entity.Index);
    }

    /// <summary>
    /// Gets a value indicating whether self or any descendant carries a dirty flag.
    /// </summary>
    public bool HasDirtySubtree(XsrUiEntityId entity) => Require(entity).SubtreeDirty;

    /// <summary>
    /// Gets a value indicating whether self or any descendant carries layout-relevant dirt.
    /// Paint-only dirt does not count.
    /// </summary>
    public bool HasDirtyLayoutSubtree(XsrUiEntityId entity) => Require(entity).SubtreeLayoutDirty;

    /// <summary>
    /// Gets the dirty kinds of one entity's own flags.
    /// </summary>
    public XsrUiDirtyKinds DirtyKinds(XsrUiEntityId entity) => Require(entity).OwnDirty;

    /// <summary>
    /// Enumerates dirty entities in ascending entity-index order.
    /// </summary>
    public IReadOnlyList<XsrUiEntityId> DirtyEntities()
    {
        List<XsrUiEntityId> entities = [];
        foreach (int index in _dirtyEntities)
        {
            entities.Add(IndexToHandle(index));
        }

        return entities;
    }

    /// <summary>
    /// Walks the subtree rooted at one entity in deterministic depth-first pre-order. The walk
    /// stops descending when <paramref name="descend"/> returns false.
    /// </summary>
    public void Walk(XsrUiEntityId root, Func<XsrUiEntityId, bool> visit, Func<XsrUiEntityId, bool>? descend = null)
    {
        ArgumentNullException.ThrowIfNull(visit);

        if (!IsAlive(root))
        {
            return;
        }

        if (!visit(root))
        {
            return;
        }

        if (descend is not null && !descend(root))
        {
            return;
        }

        foreach (XsrUiEntityId child in Children(root))
        {
            Walk(child, visit, descend);
        }
    }

    /// <summary>
    /// Binds one entity to one state entry for one property slot. Entities carry as many
    /// bindings as they need; duplicate records are ignored.
    /// </summary>
    public void BindState(XsrUiEntityId entity, XsrUiStateDependency dependency)
    {
        if (!dependency.IsValid || !IsAlive(entity))
        {
            return;
        }

        if (!_stateDependencies.TryGetValue(dependency.State, out List<(int Entity, XsrUiStateDependency Dependency)>? dependents))
        {
            dependents = [];
            _stateDependencies[dependency.State] = dependents;
        }

        if (!dependents.Contains((entity.Index, dependency)))
        {
            dependents.Add((entity.Index, dependency));
        }

        if (!_entityDependencies.TryGetValue(entity.Index, out List<XsrUiStateDependency>? own))
        {
            own = [];
            _entityDependencies[entity.Index] = own;
        }

        if (!own.Contains(dependency))
        {
            own.Add(dependency);
        }
    }

    /// <summary>
    /// Removes one exact binding record from an entity.
    /// </summary>
    public void UnbindState(XsrUiEntityId entity, XsrUiStateDependency dependency)
    {
        if (_entityDependencies.TryGetValue(entity.Index, out List<XsrUiStateDependency>? own)
            && own.Remove(dependency)
            && own.Count == 0)
        {
            _ = _entityDependencies.Remove(entity.Index);
        }

        if (_stateDependencies.TryGetValue(dependency.State, out List<(int Entity, XsrUiStateDependency Dependency)>? dependents)
            && dependents.Remove((entity.Index, dependency))
            && dependents.Count == 0)
        {
            _ = _stateDependencies.Remove(dependency.State);
        }
    }

    /// <summary>
    /// Removes every state binding of one entity.
    /// </summary>
    public void UnbindAllStates(XsrUiEntityId entity)
    {
        if (!_entityDependencies.Remove(entity.Index, out List<XsrUiStateDependency>? own))
        {
            return;
        }

        foreach (XsrUiStateDependency dependency in own)
        {
            if (_stateDependencies.TryGetValue(dependency.State, out List<(int Entity, XsrUiStateDependency Dependency)>? dependents))
            {
                _ = dependents.Remove((entity.Index, dependency));
                if (dependents.Count == 0)
                {
                    _ = _stateDependencies.Remove(dependency.State);
                }
            }
        }
    }

    /// <summary>
    /// Marks every entity bound to one state entry dirty, each with the dirty kinds its binding
    /// declared.
    /// </summary>
    public void MarkStateDirty(XsrStateId state)
    {
        if (!_stateDependencies.TryGetValue(state, out List<(int Entity, XsrUiStateDependency Dependency)>? dependents))
        {
            return;
        }

        foreach ((int entity, XsrUiStateDependency dependency) in dependents)
        {
            MarkDirty(IndexToHandle(entity), dependency.DirtyKinds);
        }
    }

    /// <summary>
    /// Gets the distinct entities bound to one state entry.
    /// </summary>
    public IReadOnlyList<XsrUiEntityId> StateDependents(XsrStateId state)
    {
        if (!_stateDependencies.TryGetValue(state, out List<(int Entity, XsrUiStateDependency Dependency)>? dependents))
        {
            return [];
        }

        return [.. dependents
            .Select(dependent => IndexToHandle(dependent.Entity))
            .Distinct()];
    }

    /// <summary>
    /// Gets the full binding records of one state entry.
    /// </summary>
    public IReadOnlyList<(XsrUiEntityId Entity, XsrUiStateDependency Dependency)> StateDependencyRecords(
        XsrStateId state)
    {
        if (!_stateDependencies.TryGetValue(state, out List<(int Entity, XsrUiStateDependency Dependency)>? dependents))
        {
            return [];
        }

        return [.. dependents.Select(dependent => (IndexToHandle(dependent.Entity), dependent.Dependency))];
    }

    private void UnbindComponent<T>(XsrUiEntityId entity, XsrUiEntity value)
        where T : class
    {
        if (typeof(T) == typeof(XsrUiStateBinding)
            && value.Components.TryGetValue(typeof(T), out object? previousBinding))
        {
            UnbindState(entity, ((XsrUiStateBinding)previousBinding).Dependency);
        }
        else if (typeof(T) == typeof(XsrUiText)
            && value.Components.TryGetValue(typeof(T), out object? previousText)
            && previousText is XsrUiText text)
        {
            UnbindState(entity, TextDependency(text.BoundState));
        }
        else if (typeof(T) == typeof(XsrUiElement)
            && value.Components.TryGetValue(typeof(T), out object? previousElement)
            && previousElement is XsrUiElement element)
        {
            UnbindState(entity, VisibilityDependency(element.BoundVisibility));
        }
    }

    private static XsrUiStateDependency TextDependency(XsrStateId state) =>
        new(state, XsrUiStateProperty.Text, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);

    private static XsrUiStateDependency VisibilityDependency(XsrStateId state) =>
        new(state, XsrUiStateProperty.Visibility, XsrUiDirtyKinds.State);

    private XsrUiEntityId IndexToHandle(int index) =>
        new(index, _generations.TryGetValue(index, out uint generation) ? generation : 0);

    private void DestroyDescendants(int index)
    {
        XsrUiEntity entity = _entities[index];
        while (entity.Children.Count > 0)
        {
            int childIndex = entity.Children[0];
            XsrUiEntity child = _entities[childIndex];
            child.Parent = default;
            entity.Children.RemoveAt(0);
            DestroyDescendants(childIndex);
            RemoveEntity(childIndex, child);
        }
    }

    private void RemoveEntity(int index, XsrUiEntity entity)
    {
        UnbindAllStates(IndexToHandle(index));
        _ = _entities.Remove(index);
        _generations[index]++;
        _ = _dirtyEntities.Remove(index);
        _freeIndexes.Push(index);
    }

    private void RecomputeSubtreeUpwards(int index)
    {
        XsrUiEntity current = _entities[index];
        RecomputeFlags(current);

        XsrUiEntityId ancestor = current.Parent;
        while (ancestor.IsAssigned)
        {
            XsrUiEntity parent = Require(ancestor);
            bool anyWas = parent.SubtreeDirty;
            bool layoutWas = parent.SubtreeLayoutDirty;
            RecomputeFlags(parent);
            if (parent.SubtreeDirty == anyWas && parent.SubtreeLayoutDirty == layoutWas)
            {
                break;
            }

            ancestor = parent.Parent;
        }
    }

    private void RecomputeFlags(XsrUiEntity entity)
    {
        entity.SubtreeDirty = entity.OwnDirty != XsrUiDirtyKinds.None
            || entity.Children.Any(child => _entities[child].SubtreeDirty);
        entity.SubtreeLayoutDirty = (entity.OwnDirty & LayoutRelevantKinds) != 0
            || entity.Children.Any(child => _entities[child].SubtreeLayoutDirty);
    }

    private XsrUiEntity Require(XsrUiEntityId entity)
    {
        if (!IsAlive(entity))
        {
            throw new InvalidOperationException($"The UI entity '{entity}' is not alive.");
        }

        return _entities[entity.Index];
    }

    private sealed class XsrUiEntity(string? name)
    {
        public string Name { get; } = name ?? string.Empty;

        public XsrUiEntityId Parent { get; set; }

        public List<int> Children { get; } = [];

        public Dictionary<Type, object> Components { get; } = [];

        public XsrUiDirtyKinds OwnDirty { get; set; }

        public bool SubtreeDirty { get; set; }

        public bool SubtreeLayoutDirty { get; set; }
    }
}
