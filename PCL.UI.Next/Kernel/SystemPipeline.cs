// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Fixed-order system pipeline (architecture §34). Systems within the same phase
/// run in registration order.
/// </summary>
public sealed class SystemPipeline
{
    private readonly List<IUiSystem> _systems = [];
    private bool _sorted = true;

    public int Count => _systems.Count;

    public IReadOnlyList<IUiSystem> Systems => _systems;

    public void Register(IUiSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        _systems.Add(system);
        _sorted = false;
    }

    public bool Unregister(IUiSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        return _systems.Remove(system);
    }

    public void Clear()
    {
        _systems.Clear();
        _sorted = true;
    }

    public void Run(UiWorld world, in UiFrameContext frame)
    {
        ArgumentNullException.ThrowIfNull(world);
        EnsureSorted();
        for (int i = 0; i < _systems.Count; i++)
            _systems[i].Update(world, in frame);
    }

    /// <summary>Runs only systems whose phase is in <paramref name="phases"/>.</summary>
    public void RunPhases(UiWorld world, in UiFrameContext frame, ReadOnlySpan<UiSystemPhase> phases)
    {
        ArgumentNullException.ThrowIfNull(world);
        EnsureSorted();
        for (int i = 0; i < _systems.Count; i++)
        {
            IUiSystem system = _systems[i];
            if (!ContainsPhase(phases, system.Phase))
                continue;
            system.Update(world, in frame);
        }
    }

    private void EnsureSorted()
    {
        if (_sorted)
            return;
        _systems.Sort(static (a, b) =>
        {
            int phase = a.Phase.CompareTo(b.Phase);
            return phase != 0 ? phase : string.CompareOrdinal(a.Name, b.Name);
        });
        _sorted = true;
    }

    private static bool ContainsPhase(ReadOnlySpan<UiSystemPhase> phases, UiSystemPhase phase)
    {
        for (int i = 0; i < phases.Length; i++)
        {
            if (phases[i] == phase)
                return true;
        }

        return false;
    }
}
