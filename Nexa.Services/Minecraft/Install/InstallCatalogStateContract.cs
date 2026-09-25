using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Install;

public static class InstallCatalogStateContract
{
    public static readonly XsrSemanticId StateKey = XsrSemanticId.Parse("minecraft.install.catalog");
    public static void DeclareState(XsrStateStoreBuilder builder) => builder.Cell<InstallCatalogState>(StateKey, "Nexa.Services.Minecraft.Install");
}

public enum InstallLoader { Forge, Cleanroom, NeoForge, Fabric, LegacyFabric, Quilt, LabyMod, OptiFine, LiteLoader, FabricApi, Qsl, OptiFabric }
public sealed record InstallDownload(string Source, string FileName, Uri Url, string? Sha1, long Size);
public sealed record InstallCatalogVersion(string Id, string Detail, bool Stable = true,
    IReadOnlyList<InstallDownload>? Downloads = null, string? Warning = null, string? ForgeRequirement = null, string? FabricRequirement = null);
public sealed record InstallCatalogState(long Revision, string GameVersion, IReadOnlyList<InstallCatalogSnapshot> Catalogs);
public sealed record InstallCatalogSnapshot(long Revision, string GameVersion, InstallLoader? Loader,
    IReadOnlyList<InstallCatalogVersion> Versions, bool Loading, string? Error = null, string? Unsupported = null)
{
    public bool? CacheHit { get; init; }
    public int? InputCount { get; init; }
    public double? NormalizeMilliseconds { get; init; }
}
public interface IInstallCatalogSource
{
    Task<IReadOnlyList<InstallCatalogVersion>> GetGamesAsync(CancellationToken token);
    Task<IReadOnlyList<InstallCatalogVersion>> GetLoadersAsync(InstallLoader loader, string game, CancellationToken token);
}
public sealed record InstallCatalogReadCommand(string GameVersion = "", InstallLoader? Loader = null, bool Refresh = false);
public sealed record InstallCatalogPrefetchCommand(string GameVersion);
public static class InstallCatalogRoutes
{
    public static readonly XsrSemanticId Prefetch = XsrSemanticId.Parse("minecraft.install.catalog.prefetch");
    public static readonly XsrSemanticId Read = XsrSemanticId.Parse("minecraft.install.catalog.read");
}

