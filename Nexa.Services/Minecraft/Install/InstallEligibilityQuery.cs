using System.Collections.ObjectModel;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Install;

public sealed record InstallBuildSelection(InstallLoader Loader, string Version);
public sealed record InstallEligibilityQuery(string GameVersion, IReadOnlyList<InstallBuildSelection> Selection,
    InstallLoader? PrimaryLoader = null, InstallLoader? Catalog = null, IReadOnlyList<string>? Candidates = null,
    string? ToggleVersion = null, IReadOnlyList<InstallBuildSelection>? InstalledSelection = null);
public sealed record InstallLoaderEligibility(InstallLoader Loader, bool IsAddon, bool Visible);
public sealed record InstallBuildEligibility(string? Conflict, string? Notice);
public sealed record InstallEligibilityResult(IReadOnlyList<InstallBuildSelection> Selection, InstallLoader? PrimaryLoader,
    IReadOnlyList<InstallLoaderEligibility> Loaders, IReadOnlyDictionary<string, InstallBuildEligibility> Builds,
    string? CommitError, string? Rejection);
public static class InstallEligibilityContract
{
    public static readonly XsrSemanticId Query = XsrSemanticId.Parse("minecraft.install.eligibility");
}

public sealed partial class InstallCatalogService
{
    /// <summary>Bounded, in-memory projection. Never invokes a provider or waits on network work.</summary>
    public InstallEligibilityResult Evaluate(InstallEligibilityQuery query)
    {
        lock (_gate)
        {
            Dictionary<InstallLoader, InstallCatalogVersion> selection = [];
            InstallCatalogVersion? Find(InstallLoader loader, string id) => _game == query.GameVersion
                && _loaders.TryGetValue(loader, out var catalog) ? catalog.Versions.FirstOrDefault(v => v.Id == id) : null;
            foreach (var item in query.Selection)
                selection[item.Loader] = Find(item.Loader, item.Version) ?? new(item.Version, "");
            InstallLoader? primary = query.PrimaryLoader;
            string? rejection = null;
            if (query.Catalog is { } toggled && query.ToggleVersion is { } version)
            {
                if (selection.GetValueOrDefault(toggled)?.Id == version)
                {
                    selection.Remove(toggled);
                    if (toggled == InstallLoader.Fabric)
                    { selection.Remove(InstallLoader.FabricApi); selection.Remove(InstallLoader.OptiFabric); selection.Remove(InstallLoader.OptiFine); }
                    if (toggled == InstallLoader.OptiFabric) selection.Remove(InstallLoader.OptiFine);
                    if (toggled == InstallLoader.Quilt) selection.Remove(InstallLoader.Qsl);
                    primary = selection.Keys.Where(kind => !InstallCompatibility.IsAddon(kind)).Cast<InstallLoader?>().FirstOrDefault();
                }
                else if (Find(toggled, version) is not { } candidate) rejection = "该版本尚未加载，请重试。";
                else if (InstallCompatibility.UnavailableReason(toggled, query.GameVersion) is { } unavailable) rejection = unavailable;
                else if (InstallCompatibility.BuildConflict(toggled, candidate, selection) is { } conflict) rejection = conflict;
                else
                {
                    foreach (var other in selection.Keys.Where(kind => !InstallCompatibility.CanCombine(toggled, kind, query.GameVersion)).ToArray()) selection.Remove(other);
                    selection[toggled] = candidate;
                    if (!InstallCompatibility.IsAddon(toggled) && !(toggled == InstallLoader.OptiFine && selection.Keys.Any(kind => kind != toggled && !InstallCompatibility.IsAddon(kind)))) primary = toggled;
                    if (selection.ContainsKey(InstallLoader.Fabric)) primary = InstallLoader.Fabric;
                }
            }
            var loaders = Enum.GetValues<InstallLoader>().Select(loader =>
            {
                bool visible = query.GameVersion.Length > 0 && InstallCompatibility.UnavailableReason(loader, query.GameVersion) is null;
                if (InstallCompatibility.IsAddon(loader)) visible &= loader == InstallLoader.Qsl ? primary == InstallLoader.Quilt : primary == InstallLoader.Fabric;
                else visible &= (primary is null || InstallCompatibility.CanCombine(primary.Value, loader, query.GameVersion))
                    && selection.Keys.Where(kind => !InstallCompatibility.IsAddon(kind)).All(kind => InstallCompatibility.CanCombine(kind, loader, query.GameVersion));
                if (loader == InstallLoader.OptiFine && primary == InstallLoader.Fabric && !selection.ContainsKey(InstallLoader.OptiFabric)) visible = false;
                if (visible && _game == query.GameVersion && _loaders.TryGetValue(loader, out var loaded)
                    && !loaded.Loading && loaded.Error is null && loaded.Revision > 0)
                    visible = loaded.Versions.Any(candidate => InstallCompatibility.BuildConflict(loader, candidate, selection) is null);
                return new InstallLoaderEligibility(loader, InstallCompatibility.IsAddon(loader) || loader == InstallLoader.OptiFine && primary is not null && primary != loader, visible || selection.ContainsKey(loader));
            }).ToArray();
            Dictionary<string, InstallBuildEligibility> builds = new(StringComparer.Ordinal);
            if (query.Catalog is { } active)
                foreach (string id in query.Candidates ?? [])
                {
                    var candidate = Find(active, id);
                    builds[id] = candidate is null ? new("该版本尚未加载，请重试。", null)
                        : new(InstallCompatibility.BuildConflict(active, candidate, selection), InstallCompatibility.BuildNotice(active, candidate, selection));
                }
            string? commitError = query.GameVersion.Length == 0 ? "请先选择 Minecraft 版本。" : null;
            foreach (var first in selection.Keys)
                foreach (var second in selection.Keys)
                    if (!InstallCompatibility.CanCombine(first, second, query.GameVersion)) commitError ??= "所选加载器不能组合安装。";
            foreach (var item in selection)
                commitError ??= Find(item.Key, item.Value.Id) is null && !(query.InstalledSelection?.Any(installed => installed.Loader == item.Key && installed.Version == item.Value.Id) == true) ? "所选版本信息已失效，请重新选择。"
                    : InstallCompatibility.BuildConflict(item.Key, item.Value, selection);
            if (selection.ContainsKey(InstallLoader.OptiFabric) && !selection.ContainsKey(InstallLoader.OptiFine)) commitError ??= "OptiFabric 需要另选 OptiFine 版本。";
            return new(Array.AsReadOnly(selection.Select(pair => new InstallBuildSelection(pair.Key, pair.Value.Id)).ToArray()), primary,
                Array.AsReadOnly(loaders), new ReadOnlyDictionary<string, InstallBuildEligibility>(builds), commitError, rejection);
        }
    }
}
