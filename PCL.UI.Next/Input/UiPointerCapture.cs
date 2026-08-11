// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Generation-safe pointer capture owned by the Runtime.</summary>
public sealed class UiPointerCapture : IDisposable
{
    private readonly UiWorld _world;
    private readonly Dictionary<int, UiEntity> _captures = [];
    private bool _disposed;

    public UiPointerCapture(UiWorld world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _world.EntityDestroying += OnEntityDestroying;
    }

    public bool Capture(int pointerId, UiEntity entity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pointerId < 0)
            throw new ArgumentOutOfRangeException(nameof(pointerId));
        if (!_world.Entities.IsAlive(entity))
            return false;
        _captures[pointerId] = entity;
        return true;
    }

    public UiEntity GetCaptured(int pointerId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_captures.TryGetValue(pointerId, out UiEntity entity))
            return UiEntity.None;
        if (_world.Entities.IsAlive(entity))
            return entity;
        _captures.Remove(pointerId);
        return UiEntity.None;
    }

    public bool Release(int pointerId) => _captures.Remove(pointerId);

    public void Dispose()
    {
        if (_disposed)
            return;
        _world.EntityDestroying -= OnEntityDestroying;
        _captures.Clear();
        _disposed = true;
    }

    private void OnEntityDestroying(UiEntity entity)
    {
        foreach (int pointerId in _captures
                     .Where(pair => pair.Value == entity)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _captures.Remove(pointerId);
        }
    }
}
