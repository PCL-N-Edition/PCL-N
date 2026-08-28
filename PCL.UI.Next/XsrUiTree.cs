using PCL.Xsr;

using System.Collections.ObjectModel;

namespace PCL.UI.Next;

/// <summary>
/// One entity of the UI tree: identity, hierarchy, and an open component set. Trees are
/// single-threaded (render-thread owned) and deterministic: children keep attach order and
/// dirty enumeration runs in ascending entity-ID order.
/// </summary>
public sealed class XsrUiTree
{
    private readonly Dictionary<int, XsrUiEntity> _entities = [];
    private readonly Stack<int> _freeIds = [];
    private readonly Dictionary<XsrStateId, List<int>> _stateDependencies = [];
    private readonly SortedSet<int> _dirtyEntities = [];
    private int _nextId = 1;

    /// <summary>
    /// Gets the number of live entities.
    /// </summary>
    public int Count => _entities.Count;

    /// <summary>
    /// Creates one entity. The new entity has no parent and no components.
    /// </summary>
    public XsrUiEntityId Create(string? name = null)
    {
        int id = _freeIds.TryPop(out int reused) ? reused : _nextId++;
        _entities[id] = new XsrUiEntity(name);
        return new XsrUiEntityId(id);
    }

    /// <summary>
    /// Destroys one entity and its whole subtree: components, state bindings, dirty records,
    /// and hierarchy entries are released, and the identity is recycled.
    /// </summary>
    public void Destroy(XsrUiEntityId entity)
    {
        XsrUiEntity destroyed = Require(entity);
        if (destroyed.Parent.IsAssigned)
        {
            XsrUiEntity parent = Require(destroyed.Parent);
            _ = parent.Children.Remove(entity.Value);
            MarkDirty(destroyed.Parent, XsrUiDirtyKinds.Structure);
        }

        DestroyDescendants(entity.Value);
        RemoveEntity(entity.Value, destroyed);
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
        parentEntity.Children.Add(child.Value);
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
        _ = parent.Children.Remove(entity.Value);
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
        return new ReadOnlyCollection<XsrUiEntityId>(
            [.. value.Children.Select(id => new XsrUiEntityId(id))]);
    }

    /// <summary>
    /// Gets a value indicating whether the handle refers to a live entity.
    /// </summary>
    public bool IsAlive(XsrUiEntityId entity) =>
        entity.IsAssigned && _entities.ContainsKey(entity.Value);

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
    /// Sets or replaces one component; null removes it. Structure is marked dirty. A state
    /// binding component automatically maintains the state dependency table.
    /// </summary>
    public void SetComponent<T>(XsrUiEntityId entity, T? component)
        where T : class
    {
        XsrUiEntity value = Require(entity);

        if (component is null)
        {
            if (value.Components.Remove(typeof(T)))
            {
                if (typeof(T) == typeof(XsrUiStateBinding))
                {
                    UnbindState(entity);
                }

                MarkDirty(entity, XsrUiDirtyKinds.Structure);
            }

            return;
        }

        value.Components[typeof(T)] = component;
        if (typeof(T) == typeof(XsrUiStateBinding))
        {
            BindState(entity, ((XsrUiStateBinding)(object)component).State);
        }

        MarkDirty(entity, XsrUiDirtyKinds.Structure);
    }

    /// <summary>
    /// Marks one entity dirty and bubbles the subtree flag to every ancestor.
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
        _dirtyEntities.Add(entity.Value);

        XsrUiEntityId ancestor = value.Parent;
        while (ancestor.IsAssigned)
        {
            XsrUiEntity current = Require(ancestor);
            if (current.SubtreeDirty)
            {
                break;
            }

            current.SubtreeDirty = true;
            ancestor = current.Parent;
        }
    }

    /// <summary>
    /// Clears one entity's own dirty flags and recomputes ancestor subtree flags.
    /// </summary>
    public void ClearDirty(XsrUiEntityId entity)
    {
        XsrUiEntity value = Require(entity);
        value.OwnDirty = XsrUiDirtyKinds.None;
        _ = _dirtyEntities.Remove(entity.Value);
        RecomputeSubtreeUpwards(entity.Value);
    }

    /// <summary>
    /// Gets a value indicating whether self or any descendant carries a dirty flag.
    /// </summary>
    public bool HasDirtySubtree(XsrUiEntityId entity) => Require(entity).SubtreeDirty;

    /// <summary>
    /// Gets the dirty kinds of one entity's own flags.
    /// </summary>
    public XsrUiDirtyKinds DirtyKinds(XsrUiEntityId entity) => Require(entity).OwnDirty;

    /// <summary>
    /// Enumerates dirty entities in ascending entity-ID order.
    /// </summary>
    public IReadOnlyList<XsrUiEntityId> DirtyEntities()
    {
        List<XsrUiEntityId> entities = [];
        foreach (int id in _dirtyEntities)
        {
            entities.Add(new XsrUiEntityId(id));
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

    private void BindState(XsrUiEntityId entity, XsrStateId state)
    {
        if (!state.IsAssigned)
        {
            return;
        }

        if (!_stateDependencies.TryGetValue(state, out List<int>? dependents))
        {
            dependents = [];
            _stateDependencies[state] = dependents;
        }

        if (!dependents.Contains(entity.Value))
        {
            dependents.Add(entity.Value);
        }
    }

    private void UnbindState(XsrUiEntityId entity)
    {
        foreach (KeyValuePair<XsrStateId, List<int>> dependency in _stateDependencies)
        {
            _ = dependency.Value.Remove(entity.Value);
        }
    }

    /// <summary>
    /// Gets the entities bound to one state entry, in binding order.
    /// </summary>
    public IReadOnlyList<XsrUiEntityId> StateDependents(XsrStateId state)
    {
        if (!_stateDependencies.TryGetValue(state, out List<int>? dependents))
        {
            return [];
        }

        return [.. dependents.Select(id => new XsrUiEntityId(id))];
    }

    private void DestroyDescendants(int id)
    {
        XsrUiEntity entity = _entities[id];
        while (entity.Children.Count > 0)
        {
            int childId = entity.Children[0];
            XsrUiEntity child = _entities[childId];
            child.Parent = default;
            entity.Children.RemoveAt(0);
            DestroyDescendants(childId);
            RemoveEntity(childId, child);
        }
    }

    private void RemoveEntity(int id, XsrUiEntity entity)
    {
        foreach (Type componentType in entity.Components.Keys)
        {
            if (componentType == typeof(XsrUiStateBinding))
            {
                UnbindState(new XsrUiEntityId(id));
            }
        }

        _ = _entities.Remove(id);
        _ = _dirtyEntities.Remove(id);
        _freeIds.Push(id);
    }

    private void RecomputeSubtreeUpwards(int id)
    {
        XsrUiEntity current = _entities[id];
        current.SubtreeDirty = current.OwnDirty != XsrUiDirtyKinds.None
            || current.Children.Any(child => _entities[child].SubtreeDirty);

        XsrUiEntityId ancestor = current.Parent;
        while (ancestor.IsAssigned)
        {
            XsrUiEntity parent = Require(ancestor);
            bool dirty = parent.OwnDirty != XsrUiDirtyKinds.None
                || parent.Children.Any(child => _entities[child].SubtreeDirty);
            if (parent.SubtreeDirty == dirty)
            {
                break;
            }

            parent.SubtreeDirty = dirty;
            ancestor = parent.Parent;
        }
    }

    private XsrUiEntity Require(XsrUiEntityId entity)
    {
        if (!entity.IsAssigned || !_entities.TryGetValue(entity.Value, out XsrUiEntity? value))
        {
            throw new InvalidOperationException($"The UI entity '{entity}' is not alive.");
        }

        return value;
    }

    private sealed class XsrUiEntity(string? name)
    {
        public string Name { get; } = name ?? string.Empty;

        public XsrUiEntityId Parent { get; set; }

        public List<int> Children { get; } = [];

        public Dictionary<Type, object> Components { get; } = [];

        public XsrUiDirtyKinds OwnDirty { get; set; }

        public bool SubtreeDirty { get; set; }
    }
}
