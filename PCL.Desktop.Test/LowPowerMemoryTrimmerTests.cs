// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Desktop.Diagnostics;

namespace PCL.Desktop.Test;

[TestClass]
[DoNotParallelize]
public sealed class LowPowerMemoryTrimmerTests
{
    [TestMethod]
    public void Trim_CompactsManagedHeapAndRequestsNativePressureRelief()
    {
        LowPowerMemoryTrimResult result = LowPowerMemoryTrimmer.Trim();

        Assert.IsGreaterThan(0, result.WorkingSetBefore);
        Assert.IsGreaterThan(0, result.WorkingSetAfter);
        Assert.IsGreaterThanOrEqualTo(0, result.ManagedHeapAfter);
        Assert.IsGreaterThanOrEqualTo(TimeSpan.Zero, result.Elapsed);
        if (OperatingSystem.IsWindows())
            Assert.IsTrue(result.NativePressureRelieved);

        Console.WriteLine(
            $"working-set={ToMebibytes(result.WorkingSetBefore):0.0}->{ToMebibytes(result.WorkingSetAfter):0.0} MiB; " +
            $"managed={ToMebibytes(result.ManagedHeapBefore):0.0}->{ToMebibytes(result.ManagedHeapAfter):0.0} MiB; " +
            $"native={result.NativePressureRelieved}; elapsed={result.Elapsed.TotalMilliseconds:0}ms");
    }

    private static double ToMebibytes(long bytes) => bytes / 1024d / 1024d;
}
