using System.Text.Json.Nodes;
using Nexa.Services.Capabilities;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.ModLoaders;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask InstalledLoaderCompatibilityRequiresExplicitEvidence()
    {
        var source = new InstalledCompatibilitySource();
        var resolver = new InstalledLoaderCompatibility(source);
        var manifests = new MinecraftResolvedVersionManifests(new JsonObject { ["_minecraftVersion"] = "1.20.1" }, []);
        var loader = new MinecraftModLoaderDescriptor(MinecraftModLoaderKind.Fabric, "0.16.0", null, []);
        AssertEqual<bool?>(true, await resolver.EvaluateAsync(manifests, loader, default));
        AssertEqual("1.20.1", source.Game);
        AssertEqual<bool?>(null, await resolver.EvaluateAsync(manifests, loader with { Version = "private-build" }, default));
        source.Offline = true;
        AssertEqual<bool?>(null, await resolver.EvaluateAsync(manifests, loader, default));
        manifests.Current["libraries"] = new JsonArray(new JsonObject { ["name"] = "net.minecraftforge:forge:1.19.4-45.0.0" });
        AssertEqual<bool?>(false, await resolver.EvaluateAsync(manifests, loader with { Kind = MinecraftModLoaderKind.Forge, Version = "45.0.0" }, default));
        AssertEqual<bool?>(true, await resolver.EvaluateAsync(manifests, loader with { Kind = MinecraftModLoaderKind.Vanilla }, default));
        AssertEqual<bool?>(null, await resolver.EvaluateAsync(manifests, loader with { Kind = MinecraftModLoaderKind.Unknown }, default));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        source.Offline = false;
        bool cancelled = false;
        try { await resolver.EvaluateAsync(manifests, loader, stop.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        AssertTrue(cancelled);
    }

    private sealed class InstalledCompatibilitySource : IInstallCatalogSource
    {
        internal bool Offline;
        internal string? Game;
        public Task<IReadOnlyList<InstallCatalogVersion>> GetGamesAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<InstallCatalogVersion>> GetLoadersAsync(InstallLoader loader, string game, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Game = game;
            if (Offline) throw new HttpRequestException("offline");
            return Task.FromResult<IReadOnlyList<InstallCatalogVersion>>([new("0.16.0", "Fabric")]);
        }
    }
}
