using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.ModLoaders;
using Nexa.Services.Minecraft.Process;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void JvmHostDescribesTheProcessBoundary()
    {
        MinecraftLaunchPlan plan = new("java", "root", ["-Xmx2g", "-cp", "a;b", "example.Main", "--demo"],
            ["a", "b"], [], new MinecraftModLoaderDescriptor(MinecraftModLoaderKind.Vanilla, null, "example.Main", []))
        { NativesDirectory = "native" };
        JvmHostService host = new(new MinecraftProcessService());
        JvmHostEnvironment environment = host.Describe(plan);
        AssertEqual("example.Main", plan.Arguments[3]);
        AssertEqual(3, environment.JvmArguments.Count);
        AssertEqual("--demo", environment.GameArguments.Single());
        AssertEqual("native", environment.NativePath);
        AssertTrue(JvmHostService.DescribeCapabilities(plan).Any(static item => item.Id == "jvmhost.process.spawn"));
    }
}
