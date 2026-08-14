// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next.Benchmarks;

internal static class Program
{
    public static int Main(string[] args)
    {
        bool verify = args.Any(static argument =>
            string.Equals(argument, "--verify", StringComparison.OrdinalIgnoreCase));
        try
        {
            IReadOnlyList<UiBenchmarkResult> results = UiBenchmarkSuite.RunAll(verify);
            Console.WriteLine("PCL.UI.Next benchmark results");
            for (int i = 0; i < results.Count; i++)
            {
                UiBenchmarkResult result = results[i];
                Console.WriteLine(
                    $"{result.Name,-24} {result.ElapsedMilliseconds,9:0.###} ms  " +
                    $"alloc={result.AllocatedBytes,10} B  ops={result.Operations,8}  {result.Detail}");
            }
            Console.WriteLine(verify ? "Benchmark gate passed." : "Benchmark run completed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Benchmark gate failed: " + exception.Message);
            return 1;
        }
    }
}
