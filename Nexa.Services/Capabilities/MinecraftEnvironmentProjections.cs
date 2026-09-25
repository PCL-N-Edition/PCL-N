using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Nexa.Services.Accounts;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.ModLoaders;

namespace Nexa.Services.Capabilities;

/// <summary>
/// Explicit instance scope shared by environment projections. A caller must identify the
/// selected instance; an unscoped machine query never guesses from release time.
/// </summary>
public static class MinecraftPrimaryInstanceScope
{
    public sealed record PrimaryInstance(
        MinecraftInstanceDescriptor Instance,
        MinecraftResolvedVersionManifests Manifests,
        string GameDirectory,
        MinecraftOptionsSnapshot Options);

    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, PrimaryInstance? Value)> Cache = new(StringComparer.Ordinal);
    private static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(10);

    public static async ValueTask<PrimaryInstance?> ResolveAsync(string? minecraftRootDirectory,
        MachineCapabilityQuery query, CancellationToken cancellationToken)
    {
        if (!query.HasInstanceScope) return null;
        string? requestedRoot = string.IsNullOrWhiteSpace(query.MinecraftRootDirectory)
            ? minecraftRootDirectory
            : query.MinecraftRootDirectory;
        if (string.IsNullOrWhiteSpace(requestedRoot)) return null;
        string root = Path.GetFullPath(requestedRoot);
        string key = root + "\n" + query.InstanceId + "\n" + query.InstanceDirectory;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (Cache.TryGetValue(key, out var cached) && now - cached.At < CacheWindow)
        {
            return cached.Value;
        }

        PrimaryInstance? resolved = null;
        try
        {
            MinecraftInstanceDiscovery discovery = new();
            IReadOnlyList<MinecraftInstanceDescriptor> instances = await discovery
                .DiscoverAsync(root, cancellationToken).ConfigureAwait(false);
            string? selectedDirectory = string.IsNullOrWhiteSpace(query.InstanceDirectory)
                ? null : Path.GetFullPath(query.InstanceDirectory);
            MinecraftInstanceDescriptor? selected = instances.FirstOrDefault(instance =>
                selectedDirectory is not null
                    ? string.Equals(Path.GetFullPath(instance.DirectoryPath), selectedDirectory, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(instance.Id, query.InstanceId, StringComparison.Ordinal));
            if (selected is { } instance)
            {
                MinecraftResolvedVersionManifests manifests = await MinecraftVersionJsonReader
                    .ResolveAsync(instance, root, cancellationToken).ConfigureAwait(false);
                string gameDirectory = instance.Metadata.InstanceIsolation ? instance.DirectoryPath : root;
                MinecraftOptionsSnapshot options = await MinecraftOptionsReader
                    .ReadAsync(gameDirectory, cancellationToken).ConfigureAwait(false);
                resolved = new PrimaryInstance(instance, manifests, gameDirectory, options);
            }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException)
        {
            // Discovery races a running install or a broken version dir; projections answer
            // unavailable for this window instead of failing the whole snapshot.
            resolved = null;
        }

        Cache[key] = (now, resolved);
        return resolved;
    }
}

/// <summary>
/// Loader version fallback: Fabric-profile manifests carry no version string, but the loader
/// jar in mods/ names it exactly — the edit page reads the same fact from there.
/// </summary>
public static class LoaderVersionProjections
{
    public static string? ReadLoaderVersionFromMods(string gameDirectory)
    {
        try
        {
            string mods = Path.Combine(gameDirectory, "mods");
            if (!Directory.Exists(mods))
            {
                return null;
            }

            string[] prefixes = ["fabric-loader-", "forge-", "neoforge-", "quilt-loader-", "cleanroom-", "labymod-"];
            foreach (string jar in Directory.EnumerateFiles(mods, "*.jar").Order(StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(jar);
                foreach (string prefix in prefixes)
                {
                    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string tail = name[prefix.Length..];
                    int end = tail.IndexOf('-', StringComparison.OrdinalIgnoreCase);
                    if (Version.TryParse(end < 0 ? Path.GetFileNameWithoutExtension(tail) : tail[..end], out Version? parsed))
                    {
                        return parsed.ToString();
                    }
                }
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    /// <summary>options.txt resourcePacks is a raw JSON array; users see names, not brackets.</summary>
    public static IReadOnlyList<string> DescribeResourcePacks(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(raw) is not System.Text.Json.Nodes.JsonArray packs)
            {
                return [raw];
            }

            List<string> names = [];
            foreach (System.Text.Json.Nodes.JsonNode? node in packs)
            {
                string? value = node?.GetValue<string>();
                if (value is { Length: > 0 } && value != "vanilla")
                {
                    names.Add(value);
                }
            }

            return names.AsReadOnly();
        }
        catch (System.Text.Json.JsonException)
        {
            return [raw];
        }
    }
}

internal static class InstalledLoaderProjection
{
    public static async ValueTask<MinecraftModLoaderDescriptor> ReadAsync(
        MinecraftPrimaryInstanceScope.PrimaryInstance primary,
        string? configuredRoot,
        MachineCapabilityQuery query,
        CancellationToken cancellationToken)
    {
        string? root = string.IsNullOrWhiteSpace(query.MinecraftRootDirectory)
            ? configuredRoot
            : query.MinecraftRootDirectory;
        if (!string.IsNullOrWhiteSpace(root))
        {
            try
            {
                MinecraftInstallEditSnapshot edit = await MinecraftInstallEditService.ReadAsync(
                    new(Path.GetFullPath(root), primary.Instance.Id), cancellationToken).ConfigureAwait(false);
                InstallBuildSelection? selected = edit.Selection.FirstOrDefault(item => !InstallCompatibility.IsAddon(item.Loader));
                if (selected is not null)
                {
                    MinecraftModLoaderKind kind = selected.Loader switch
                    {
                        InstallLoader.Forge => MinecraftModLoaderKind.Forge,
                        InstallLoader.NeoForge => MinecraftModLoaderKind.NeoForge,
                        InstallLoader.Fabric or InstallLoader.LegacyFabric => MinecraftModLoaderKind.Fabric,
                        InstallLoader.Quilt => MinecraftModLoaderKind.Quilt,
                        InstallLoader.Cleanroom => MinecraftModLoaderKind.Cleanroom,
                        InstallLoader.OptiFine => MinecraftModLoaderKind.OptiFine,
                        InstallLoader.LiteLoader => MinecraftModLoaderKind.LiteLoader,
                        InstallLoader.LabyMod => MinecraftModLoaderKind.LabyMod,
                        _ => MinecraftModLoaderKind.Unknown,
                    };
                    return new(kind, selected.Version, primary.Manifests.Current["mainClass"]?.ToString(),
                        ["MinecraftInstallEditService"]);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException)
            {
                // A manually assembled or partially installed version can lack the edit receipt.
                // Fall through to the manifest detector instead of losing all loader facts.
            }
        }

        return MinecraftModLoaderDetector.Detect(primary.Manifests.Current);
    }
}

/// <summary>loader.* facts projected from the primary instance's resolved manifest chain.</summary>
public sealed class LoaderCapabilityProvider(string? minecraftRootDirectory) : IMachineCapabilityProvider
{
    public string Id => MachineInstanceCatalog.LoaderProviderId;

    public async ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
        => await CollectAsync(timestamp, new MachineCapabilityQuery(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, MachineCapabilityQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (await MinecraftPrimaryInstanceScope.ResolveAsync(minecraftRootDirectory, query, cancellationToken)
            .ConfigureAwait(false) is not { } primary)
        {
            return AllUnavailable(timestamp, "尚未发现 Minecraft 实例");
        }

        const string source = "MinecraftInstallEditService + MinecraftVersionJsonReader";
        MinecraftModLoaderDescriptor loader = await InstalledLoaderProjection.ReadAsync(
            primary, minecraftRootDirectory, query, cancellationToken).ConfigureAwait(false);
        string? heuristicVersion = loader.Version is null
            ? LoaderVersionProjections.ReadLoaderVersionFromMods(primary.GameDirectory)
            : null;
        string? loaderVersion = loader.Version ?? heuristicVersion;
        bool vanilla = loader.Kind is MinecraftModLoaderKind.Vanilla;
        // The version reader THROWS on a missing inheritsFrom parent, so reaching here means
        // the chain resolved. Self-contained loader jsons (no inheritsFrom) are complete too —
        // treating them as broken fired LOADER_INCOMPATIBLE on healthy installs.
        const bool chainComplete = true;
        return Array.AsReadOnly(new ICapability[]
        {
            MachineInstanceCatalog.LoaderPresent.Observe(!vanilla, timestamp, source),
            MachineInstanceCatalog.LoaderType.Observe(loader.Kind.ToString(), timestamp, source),
            loaderVersion is { Length: > 0 } version
                ? MachineInstanceCatalog.LoaderVersion.Observe(version, timestamp,
                    heuristicVersion is null ? source : "mods 文件名启发式（仅作兜底）",
                    heuristicVersion is null ? CapabilityConfidence.High : CapabilityConfidence.Low)
                : MachineInstanceCatalog.LoaderVersion.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "加载器版本未知"),
            MachineInstanceCatalog.LoaderComplete.Observe(chainComplete, timestamp, source),
            // A resolvable chain proves the manifest GRAPH is intact — it says nothing about
            // whether this loader version supports this Minecraft version. The explicit
            // range check is the Compatibility Resolver's job; until it exists the fact is
            // Unknown, never a silent true (Unknown ≠ true is a frozen Registry principle).
            MachineInstanceCatalog.LoaderMinecraftCompatible.Unavailable(
                CapabilityAvailability.DependencyMissing, timestamp, "尚未执行加载器↔Minecraft 显式兼容范围检查"),
            MachineInstanceCatalog.LoaderChainResolved.Observe(true, timestamp, source),
            MachineInstanceCatalog.LoaderMetadataValid.Observe(true, timestamp, source),
            // derived.* are this provider's own definitions — computing them in place is the
            // ownership-clean path (cross-provider derivations go through the broker pass).
            MachineInstanceCatalog.LoaderDerivedMissing.Observe(false, timestamp, source + "（原版无需加载器；已解析实例未发现缺失要求）"),
            MachineInstanceCatalog.LoaderDerivedIncompatible.Observe(!vanilla && !chainComplete, timestamp, source),
        });
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<ICapability> AllUnavailable(DateTimeOffset timestamp, string reason) => Array.AsReadOnly(
        MachineInstanceCatalog.LoaderScope.Select(id => MachineInstanceCatalog.LoaderDefinitions[id].Unavailable(
            CapabilityAvailability.DependencyMissing, timestamp, reason)).ToArray());
}

/// <summary>account.* launch-capability facts from the real roster.</summary>
public sealed class AccountCapabilityProvider(AccountService accounts) : IMachineCapabilityProvider
{
    public string Id => MachineInstanceCatalog.AccountProviderId;

    public ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string source = "AccountService 花名册";
        List<LaunchProfileView> views = [.. accounts.GetViews()];
        LaunchProfileView? selected = accounts.SelectedIndex >= 0 && accounts.SelectedIndex < views.Count
            ? views[accounts.SelectedIndex]
            : null;
        return ValueTask.FromResult<IReadOnlyList<ICapability>>(Array.AsReadOnly(new ICapability[]
        {
            MachineInstanceCatalog.AccountAvailable.Observe(views.Count > 0, timestamp, source),
            selected is { } chosen
                ? MachineInstanceCatalog.AccountSelected.Observe(chosen.Username, timestamp, source)
                : MachineInstanceCatalog.AccountSelected.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "未选择账户"),
            MachineInstanceCatalog.AccountAuthenticationProvider.Observe(
                (selected ?? (LaunchProfileView?)null) is { } profile ? profile.Kind.ToString() : string.Empty, timestamp, source),
            selected is { Kind: LaunchProfileKind.Offline } offline
                ? MachineInstanceCatalog.AccountAuthenticationRequired.Observe(false, timestamp, source)
                : MachineInstanceCatalog.AccountAuthenticationRequired.Observe(true, timestamp, source),
            selected is { Kind: LaunchProfileKind.Offline }
                ? MachineInstanceCatalog.AccountAuthenticationValid.Observe(true, timestamp, source + "（离线无需验证）")
                : MachineInstanceCatalog.AccountAuthenticationValid.Unavailable(
                    CapabilityAvailability.Unknown, timestamp, "在线验证延迟到启动时执行"),
            selected is { Kind: LaunchProfileKind.Microsoft or LaunchProfileKind.LittleSkin } online
                ? MachineInstanceCatalog.AccountAuthenticationRefreshable.Observe(true, timestamp, source + "（存在刷新令牌）")
                : MachineInstanceCatalog.AccountAuthenticationRefreshable.Observe(false, timestamp, source),
        }));
    }
}

/// <summary>
/// Instance-scoped java facts: the requirement resolved from the primary manifest (XSR-608
/// machinery) and whether an installed runtime satisfies it. Runs the real selection.
/// </summary>
public static class JavaCompatibilityProjection
{
    public static async ValueTask<IReadOnlyList<ICapability>> CollectAsync(
        IReadOnlyList<JavaRuntimeCandidate> runtimes,
        string? minecraftRootDirectory,
        MachineCapabilityQuery query,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        if (await MinecraftPrimaryInstanceScope.ResolveAsync(minecraftRootDirectory, query, cancellationToken)
            .ConfigureAwait(false) is not { } primary)
        {
            return
            [
                MachineInstanceCatalog.JavaRequirementMinimum.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "尚未发现 Minecraft 实例"),
                MachineInstanceCatalog.JavaRequirementRecommended.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "尚未发现 Minecraft 实例"),
                MachineInstanceCatalog.JavaCompatibilityMinecraft.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "尚未发现 Minecraft 实例"),
                MachineInstanceCatalog.JavaCompatibilityHard.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "尚未发现 Minecraft 实例"),
            ];
        }

        const string source = "MinecraftJavaRequirementResolver + JavaSelectionService";
        MinecraftModLoaderDescriptor loader = await InstalledLoaderProjection.ReadAsync(
            primary, minecraftRootDirectory, query, cancellationToken).ConfigureAwait(false);
        JsonObject manifest = primary.Manifests.Current;
        // javaVersion lives on the base (vanilla) manifest; walk the chain like the launch
        // coordinator does so a loader-only manifest still resolves the parent's requirement.
        (int? major, string? component) = ReadManifestJava(primary.Manifests);
        MinecraftJavaRequirementRequest request = new()
        {
            MinecraftVersion = MinecraftGameVersion.TryParse(manifest["id"]?.ToString(), out MinecraftGameVersion game) ? game : null,
            ManifestJavaMajorVersion = major,
            ManifestJavaComponent = component,
            ReleaseTime = primary.Instance.Version.ReleaseTime,
            HasFabric = loader.Kind is MinecraftModLoaderKind.Fabric,
            HasForge = loader.Kind is MinecraftModLoaderKind.Forge,
            ForgeVersion = loader.Version,
            HasCleanroom = loader.Kind is MinecraftModLoaderKind.Cleanroom,
            CleanroomVersion = loader.Version,
            HasOptiFine = loader.Kind is MinecraftModLoaderKind.OptiFine,
            HasLiteLoader = loader.Kind is MinecraftModLoaderKind.LiteLoader,
            HasLabyMod = loader.Kind is MinecraftModLoaderKind.LabyMod,
        };
        JavaRequirementResolution requirement = MinecraftJavaRequirementResolver.Resolve(request);
        JavaRuntimeCandidate? selected = requirement.Success
            ? runtimes.Where(candidate => candidate.IsEnabled && candidate.IsAvailable
                    && requirement.Range.Contains(candidate.Installation.Version))
                .OrderBy(candidate => candidate.Installation.MajorVersion)
                .ThenBy(candidate => candidate.Installation.IsJre ? 1 : 0)
                .ThenBy(candidate => candidate.Installation.Version)
                .ThenBy(candidate => candidate.Installation.JavaHome, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
            : null;

        return
        [
            requirement.Success && !requirement.Range.Minimum.Equals(new Version())
                ? MachineInstanceCatalog.JavaRequirementMinimum.Observe(
                    requirement.Range.Minimum.ToString(), timestamp, source)
                : MachineInstanceCatalog.JavaRequirementMinimum.Unavailable(
                    CapabilityAvailability.Unknown, timestamp, "要求区间无下限"),
            requirement.RecommendedComponent is { Length: > 0 } recommended
                ? MachineInstanceCatalog.JavaRequirementRecommended.Observe(recommended, timestamp, source)
                : MachineInstanceCatalog.JavaRequirementRecommended.Unavailable(
                    CapabilityAvailability.DependencyMissing, timestamp, "无推荐组件"),
            selected is not null
                ? MachineInstanceCatalog.JavaCompatibilityMinecraft.Observe(true, timestamp, source)
                : MachineInstanceCatalog.JavaCompatibilityMinecraft.Observe(false, timestamp, source + "（没有匹配的本地运行时）"),
            requirement.Success
                ? MachineInstanceCatalog.JavaCompatibilityHard.Observe(selected is not null, timestamp, source)
                : MachineInstanceCatalog.JavaCompatibilityHard.Observe(false, timestamp, source + $"（{requirement.FailureReason}: {requirement.Detail}）"),
        ];
    }

    private static (int? Major, string? Component) ReadManifestJava(MinecraftResolvedVersionManifests manifests)
    {
        foreach (JsonObject manifest in new[] { manifests.Current }.Concat(manifests.Inherited))
        {
            if (manifest["javaVersion"] is not JsonObject javaVersion)
            {
                continue;
            }

            int? major = javaVersion["majorVersion"] is { } majorNode && int.TryParse(
                majorNode.ToString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : null;
            if (major is not null)
            {
                return (major, javaVersion["component"]?.ToString());
            }
        }

        return (null, null);
    }
}
