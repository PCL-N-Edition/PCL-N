// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Fixed-order system pipeline (architecture §34).
/// Phase ordinal decides major order; registration order decides order within a phase.
/// </summary>
public sealed class SystemPipeline
{
    private readonly List<Entry> _entries = [];
    private int _nextRegistrationIndex;
    private bool _sorted = true;

    public int Count => _entries.Count;

    public IReadOnlyList<IUiSystem> Systems
    {
        get
        {
            EnsureSorted();
            return _entries.ConvertAll(static e => e.System);
        }
    }

    public void Register(IUiSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        _entries.Add(new Entry(_nextRegistrationIndex++, system));
        _sorted = false;
    }

    public bool Unregister(IUiSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        for (int i = 0; i < _entries.Count; i++)
        {
            if (!ReferenceEquals(_entries[i].System, system))
                continue;
            _entries.RemoveAt(i);
            _sorted = false;
            return true;
        }

        return false;
    }

    public void Clear()
    {
        _entries.Clear();
        _nextRegistrationIndex = 0;
        _sorted = true;
    }

    public void Run(UiWorld world, in UiFrameContext frame)
    {
        ArgumentNullException.ThrowIfNull(world);
        EnsureSorted();
        for (int i = 0; i < _entries.Count; i++)
        {
            IUiSystem system = _entries[i].System;
            long started = world.Diagnostics.BeginSystem();
            try
            {
                system.Update(world, in frame);
            }
            finally
            {
                world.Diagnostics.EndSystem(system, started);
            }
        }
    }

    /// <summary>Runs only systems whose phase is in <paramref name="phases"/>.</summary>
    public void RunPhases(UiWorld world, in UiFrameContext frame, ReadOnlySpan<UiSystemPhase> phases)
    {
        ArgumentNullException.ThrowIfNull(world);
        EnsureSorted();
        for (int i = 0; i < _entries.Count; i++)
        {
            IUiSystem system = _entries[i].System;
            if (!ContainsPhase(phases, system.Phase))
                continue;
            long started = world.Diagnostics.BeginSystem();
            try
            {
                system.Update(world, in frame);
            }
            finally
            {
                world.Diagnostics.EndSystem(system, started);
            }
        }
    }

    private void EnsureSorted()
    {
        if (_sorted)
            return;
        _entries.Sort(static (a, b) =>
        {
            int phase = a.System.Phase.CompareTo(b.System.Phase);
            return phase != 0 ? phase : a.RegistrationIndex.CompareTo(b.RegistrationIndex);
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

    private readonly struct Entry(int registrationIndex, IUiSystem system)
    {
        public int RegistrationIndex { get; } = registrationIndex;
        public IUiSystem System { get; } = system;
    }
}
