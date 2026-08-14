// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Fenwick-tree-backed variable item extent index. Both index-to-offset and
/// offset-to-index operations are O(log N).
/// </summary>
public sealed class VariableExtentIndex
{
    private float[] _extents = [];
    private float[] _tree = [0f];
    private bool[] _measured = [];

    public VariableExtentIndex(int count, float estimatedExtent)
    {
        Reset(count, estimatedExtent);
    }

    public int Count => _extents.Length;

    public float EstimatedExtent { get; private set; }

    public float TotalExtent => PrefixSum(Count);

    public void Reset(int count, float estimatedExtent)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (!float.IsFinite(estimatedExtent) || estimatedExtent <= 0f)
            throw new ArgumentOutOfRangeException(nameof(estimatedExtent));

        EstimatedExtent = estimatedExtent;
        _extents = new float[count];
        _measured = new bool[count];
        _tree = new float[count + 1];
        Array.Fill(_extents, estimatedExtent);
        for (int i = 1; i <= count; i++)
        {
            _tree[i] += estimatedExtent;
            int parent = i + (i & -i);
            if (parent <= count)
                _tree[parent] += _tree[i];
        }
    }

    public float GetExtent(int index)
    {
        ValidateIndex(index);
        return _extents[index];
    }

    public bool IsMeasured(int index)
    {
        ValidateIndex(index);
        return _measured[index];
    }

    public bool SetMeasuredExtent(int index, float extent)
    {
        ValidateIndex(index);
        if (!float.IsFinite(extent) || extent <= 0f)
            throw new ArgumentOutOfRangeException(nameof(extent));
        float previous = _extents[index];
        _measured[index] = true;
        if (MathF.Abs(previous - extent) <= 0.01f)
            return false;
        _extents[index] = extent;
        Add(index + 1, extent - previous);
        return true;
    }

    public float GetOffset(int index)
    {
        if ((uint)index > (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return PrefixSum(index);
    }

    public int FindIndexAtOffset(float offset)
    {
        if (Count == 0)
            return -1;
        if (!float.IsFinite(offset))
            offset = offset > 0f ? TotalExtent : 0f;
        offset = Math.Clamp(offset, 0f, Math.Max(0f, TotalExtent - 0.0001f));

        int index = 0;
        float prefix = 0f;
        int bit = HighestPowerOfTwoAtMost(Count);
        while (bit != 0)
        {
            int next = index + bit;
            if (next <= Count && prefix + _tree[next] <= offset)
            {
                index = next;
                prefix += _tree[next];
            }
            bit >>= 1;
        }
        return Math.Min(index, Count - 1);
    }

    private void Add(int oneBasedIndex, float delta)
    {
        for (int i = oneBasedIndex; i <= Count; i += i & -i)
            _tree[i] += delta;
    }

    private float PrefixSum(int count)
    {
        float sum = 0f;
        for (int i = count; i > 0; i -= i & -i)
            sum += _tree[i];
        return sum;
    }

    private void ValidateIndex(int index)
    {
        if ((uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));
    }

    private static int HighestPowerOfTwoAtMost(int value)
    {
        int power = 1;
        while (power <= value / 2)
            power <<= 1;
        return power;
    }
}
