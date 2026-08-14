// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Slice → affected binding indices for O(changed) dispatch (not O(all bindings)).
/// Built at compile time; source generators can emit the same tables.
/// </summary>
internal sealed class BlueprintDependencyIndex
{
    private readonly Dictionary<int, int[]> _propertyBindingsBySlice;
    private readonly Dictionary<int, int[]> _structuralBindingsBySlice;
    private readonly int[] _allSlices;

    internal BlueprintDependencyIndex(
        Dictionary<int, int[]> propertyBindingsBySlice,
        Dictionary<int, int[]> structuralBindingsBySlice)
    {
        _propertyBindingsBySlice = propertyBindingsBySlice;
        _structuralBindingsBySlice = structuralBindingsBySlice;

        HashSet<int> slices = [];
        foreach (int s in propertyBindingsBySlice.Keys)
            slices.Add(s);
        foreach (int s in structuralBindingsBySlice.Keys)
            slices.Add(s);
        _allSlices = slices.OrderBy(static x => x).ToArray();
    }

    internal ReadOnlySpan<int> AllSlices => _allSlices;

    internal bool TryGetPropertyBindings(int sliceId, out ReadOnlySpan<int> bindingIndices)
    {
        if (_propertyBindingsBySlice.TryGetValue(sliceId, out int[]? arr))
        {
            bindingIndices = arr;
            return true;
        }

        bindingIndices = default;
        return false;
    }

    internal bool TryGetStructuralBindings(int sliceId, out ReadOnlySpan<int> bindingIndices)
    {
        if (_structuralBindingsBySlice.TryGetValue(sliceId, out int[]? arr))
        {
            bindingIndices = arr;
            return true;
        }

        bindingIndices = default;
        return false;
    }

    internal static BlueprintDependencyIndex Build(IReadOnlyList<BlueprintBinding> bindings)
    {
        Dictionary<int, List<int>> property = new();
        Dictionary<int, List<int>> structural = new();

        for (int i = 0; i < bindings.Count; i++)
        {
            BlueprintBinding binding = bindings[i];
            ReadOnlySpan<int> deps = binding.DependencySlices;
            for (int d = 0; d < deps.Length; d++)
            {
                int slice = deps[d];
                if (binding.Kind == BlueprintBindingKind.Condition)
                    Add(structural, slice, i);
                else if (binding.Kind != BlueprintBindingKind.None)
                    Add(property, slice, i);
            }
        }

        return new BlueprintDependencyIndex(ToArrays(property), ToArrays(structural));
    }

    private static void Add(Dictionary<int, List<int>> map, int slice, int bindingIndex)
    {
        if (!map.TryGetValue(slice, out List<int>? list))
        {
            list = [];
            map[slice] = list;
        }

        if (!list.Contains(bindingIndex))
            list.Add(bindingIndex);
    }

    private static Dictionary<int, int[]> ToArrays(Dictionary<int, List<int>> map)
    {
        Dictionary<int, int[]> result = new(map.Count);
        foreach ((int slice, List<int> list) in map)
            result[slice] = list.ToArray();
        return result;
    }
}
