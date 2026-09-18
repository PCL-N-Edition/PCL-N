namespace Nexa.Services.Minecraft.Install;

public static partial class InstallCompatibility
{
    public static bool MatchesForge(InstallCatalogVersion optiFine, string forge)
    {
        string? requirement = optiFine.ForgeRequirement?.Trim().TrimStart('#');
        if (string.IsNullOrWhiteSpace(requirement) || requirement == "N/A") return false;
        string version = forge.Split('-', 2)[0];
        return requirement.Contains('.', StringComparison.Ordinal) ? version == requirement : version.EndsWith("." + requirement, StringComparison.Ordinal);
    }
    public static string? BuildConflict(InstallLoader loader, InstallCatalogVersion candidate,
        IReadOnlyDictionary<InstallLoader, InstallCatalogVersion> selected)
    {
        if (loader == InstallLoader.FabricApi && !selected.ContainsKey(InstallLoader.Fabric)) return "请先选择 Fabric";
        if (loader == InstallLoader.Qsl && !selected.ContainsKey(InstallLoader.Quilt)) return "请先选择 Quilt";
        InstallCatalogVersion? optiFine = loader == InstallLoader.OptiFine ? candidate : selected.GetValueOrDefault(InstallLoader.OptiFine);
        InstallCatalogVersion? forge = loader == InstallLoader.Forge ? candidate : selected.GetValueOrDefault(InstallLoader.Forge);
        if (optiFine is not null && forge is not null && !MatchesForge(optiFine, forge.Id))
            return optiFine.ForgeRequirement == "N/A" ? "此 OptiFine 不支持 Forge" : optiFine.ForgeRequirement is { } required
                ? "官方标注适配 Forge " + required : "尚未确认此 OptiFine 的 Forge 兼容性";
        InstallCatalogVersion? fabric = loader == InstallLoader.Fabric ? candidate : selected.GetValueOrDefault(InstallLoader.Fabric);
        InstallCatalogVersion? bridge = loader == InstallLoader.OptiFabric ? candidate : selected.GetValueOrDefault(InstallLoader.OptiFabric);
        if (loader == InstallLoader.OptiFabric && fabric is null) return "请先选择 Fabric";
        if (fabric is not null && optiFine is not null && bridge is null) return "需要先选择 OptiFabric";
        if (bridge is not null && fabric is not null && !MatchesPredicate(fabric.Id, bridge.FabricRequirement))
            return bridge.FabricRequirement is { } predicate ? "需要 Fabric " + predicate : "尚未确认 Fabric Loader 依赖";
        return null;
    }
    public static string? BuildNotice(InstallLoader loader, InstallCatalogVersion candidate,
        IReadOnlyDictionary<InstallLoader, InstallCatalogVersion> selected)
    {
        InstallCatalogVersion? cleanroom = loader == InstallLoader.Cleanroom ? candidate : selected.GetValueOrDefault(InstallLoader.Cleanroom);
        InstallCatalogVersion? optiFine = loader == InstallLoader.OptiFine ? candidate : selected.GetValueOrDefault(InstallLoader.OptiFine);
        if (cleanroom is not null && optiFine is not null)
        {
            if (cleanroom.Id.TrimStart('v') is "0.6.9-alpha" or "0.6.10-alpha" or "0.6.10")
                return "此 Cleanroom 版本有 OptiFine 崩溃报告，可能需要 OptiRefine 补丁。";
            return "Cleanroom 与 OptiFine 为部分兼容，可能需要额外补丁；请验证所选组合。";
        }
        if (loader == InstallLoader.OptiFabric || selected.ContainsKey(InstallLoader.OptiFabric))
            return "OptiFabric 需另选同一 Minecraft 版本的 OptiFine；不保证其他模组的兼容性。";
        return null;
    }
    /// <summary>Conservative evaluator for published exact/wildcard/comparison dependency predicates.</summary>
    public static bool MatchesPredicate(string version, string? predicate)
    {
        if (string.IsNullOrWhiteSpace(predicate)) return false;
        if (predicate == "*") return true;
        if (predicate == version) return true;
        if (predicate.EndsWith(".*", StringComparison.Ordinal)) return version.StartsWith(predicate[..^1], StringComparison.Ordinal);
        if (!Version.TryParse(version.Split('-', 2)[0], out Version? actual)) return false;
        foreach (string part in predicate.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int offset = part.StartsWith(">=", StringComparison.Ordinal) || part.StartsWith("<=", StringComparison.Ordinal) ? 2
                : part.StartsWith('>') || part.StartsWith('<') || part.StartsWith('=') ? 1 : 0;
            if (!Version.TryParse(part[offset..], out Version? expected)) return false;
            int comparison = actual.CompareTo(expected);
            bool matches = part[..offset] switch { ">=" => comparison >= 0, "<=" => comparison <= 0, ">" => comparison > 0, "<" => comparison < 0, _ => comparison == 0 };
            if (!matches) return false;
        }
        return true;
    }
}
