// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace PCL.Desktop.Diagnostics;

/// <summary>
/// Best-effort memory pressure relief for the opt-in ultra-low-power state.
/// This runs only after presentation resources have been detached and the window
/// has remained inactive, because a compacting collection and cold-page eviction
/// trade the next resume's page faults for a smaller idle working set.
/// </summary>
internal static class LowPowerMemoryTrimmer
{
    public static LowPowerMemoryTrimResult Trim()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long workingSetBefore = process.WorkingSet64;
        long managedBefore = GC.GetTotalMemory(forceFullCollection: false);
        Stopwatch stopwatch = Stopwatch.StartNew();

        // The large resource owners have already been detached on the UI thread.
        // Compact once so their now-unreachable managed wrappers and finalizers can
        // release Skia/media native allocations before asking the OS for pressure relief.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        bool nativePressureRelieved = TryRelieveNativePressure(process);
        process.Refresh();
        stopwatch.Stop();
        return new LowPowerMemoryTrimResult(
            WorkingSetBefore: workingSetBefore,
            WorkingSetAfter: process.WorkingSet64,
            ManagedHeapBefore: managedBefore,
            ManagedHeapAfter: GC.GetTotalMemory(forceFullCollection: false),
            NativePressureRelieved: nativePressureRelieved,
            Elapsed: stopwatch.Elapsed);
    }

    private static bool TryRelieveNativePressure(Process process)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return EmptyWorkingSet(process.Handle);
            if (OperatingSystem.IsLinux())
                return MallocTrim(0) != 0;
            if (OperatingSystem.IsMacOS())
                return MallocZonePressureRelief(0, 0) > 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
        {
            // musl and older macOS runtimes may not expose the optional allocator hook.
        }

        return false;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(nint process);

    [DllImport("libc", EntryPoint = "malloc_trim")]
    private static extern int MallocTrim(nuint padding);

    [DllImport("libSystem.B.dylib", EntryPoint = "malloc_zone_pressure_relief")]
    private static extern nuint MallocZonePressureRelief(nint zone, nuint goal);
}

internal readonly record struct LowPowerMemoryTrimResult(
    long WorkingSetBefore,
    long WorkingSetAfter,
    long ManagedHeapBefore,
    long ManagedHeapAfter,
    bool NativePressureRelieved,
    TimeSpan Elapsed);
