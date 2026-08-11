// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Generation-safe pointer capture owned by the Runtime.</summary>
public sealed class UiPointerCapture : IDisposable
{
    private readonly UiWorld _world;
    private readonly UiInputRootRegistry _inputRoots;
    private readonly Dictionary<UiPointerKey, UiEntity> _captures = [];
    private bool _disposed;

    public UiPointerCapture(UiWorld world, UiInputRootRegistry inputRoots)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _inputRoots = inputRoots ?? throw new ArgumentNullException(nameof(inputRoots));
        _world.EntityDestroying += OnEntityDestroying;
        _inputRoots.InputRootDestroying += OnInputRootDestroying;
    }

    public bool Capture(UiInputRootId inputRoot, int pointerId, UiEntity entity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pointerId < 0)
            throw new ArgumentOutOfRangeException(nameof(pointerId));
        if (!_world.Entities.IsAlive(entity) || !_inputRoots.Contains(inputRoot, entity))
            return false;
        _captures[new UiPointerKey(inputRoot, pointerId)] = entity;
        return true;
    }

    public UiEntity GetCaptured(UiInputRootId inputRoot, int pointerId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UiPointerKey key = new(inputRoot, pointerId);
        if (!_captures.TryGetValue(key, out UiEntity entity))
            return UiEntity.None;
        if (_world.Entities.IsAlive(entity) && _inputRoots.Contains(inputRoot, entity))
            return entity;
        _captures.Remove(key);
        return UiEntity.None;
    }

    public bool Release(UiInputRootId inputRoot, int pointerId) =>
        _captures.Remove(new UiPointerKey(inputRoot, pointerId));

    public void Dispose()
    {
        if (_disposed)
            return;
        _world.EntityDestroying -= OnEntityDestroying;
        _inputRoots.InputRootDestroying -= OnInputRootDestroying;
        _captures.Clear();
        _disposed = true;
    }

    private void OnEntityDestroying(UiEntity entity)
    {
        foreach (UiPointerKey key in _captures
                     .Where(pair => pair.Value == entity)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _captures.Remove(key);
        }
    }

    private void OnInputRootDestroying(UiInputRootId inputRoot)
    {
        foreach (UiPointerKey key in _captures.Keys
                     .Where(key => key.InputRoot == inputRoot)
                     .ToArray())
        {
            _captures.Remove(key);
        }
    }
}
