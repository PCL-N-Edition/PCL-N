using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Nexa.Services.Accounts;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.ModLoaders;

namespace Nexa.Services.Capabilities;

/// <summary>
/// The primary instance scope shared by the environment projections: discovery picks the
/// newest instance under the root once per collection window, then loader / java / settings
/// facts all answer for THAT instance — exactly the instance the user is about to launch.
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

    public static async ValueTask<PrimaryInstance?> ResolveAsync(string minecraftRootDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftRootDirectory);
        string root = Path.GetFullPath(minecraftRootDirectory);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (Cache.TryGetValue(root, out var cached) && now - cached.At < CacheWindow)
        {
            return cached.Value;
        }

        PrimaryInstance? resolved = null;
        try
        {
            MinecraftInstanceDiscovery discovery = new();
            IReadOnlyList<MinecraftInstanceDescriptor> instances = await discovery
                .DiscoverAsync(root, cancellationToken).ConfigureAwait(false);
            MinecraftInstanceDescriptor? newest = instances
                .OrderByDescending(static instance => instance.Version.ReleaseTime ?? DateTimeOffset.MinValue)
                .ThenBy(static instance => instance.Id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (newest is { } instance)
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

        Cache[root] = (now, resolved);
        return resolved;
    }
}

/// <summary>loader.* facts projected from the primary instance's resolved manifest chain.</summary>
public sealed class LoaderCapabilityProvider(string? minecraftRootDirectory) : IMachineCapabilityProvider
{
    public string Id => MachineInstanceCatalog.LoaderProviderId;

    public async ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(minecraftRootDirectory)
            || await MinecraftPrimaryInstanceScope.ResolveAsync(minecraftRootDirectory, cancellationToken).ConfigureAwait(false) is not { } primary)
        {
            return AllUnavailable(timestamp, "尚未发现 Minecraft 实例");
        }

        const string source = "MinecraftModLoaderDetector + MinecraftVersionJsonReader";
        MinecraftModLoaderDescriptor loader = MinecraftModLoaderDetector.Detect(primary.Manifests.Current);
        bool vanilla = loader.Kind is MinecraftModLoaderKind.Vanilla;
        bool chainComplete = primary.Manifests.Inherited.Count > 0
            || vanilla; // vanilla has no parent to lose
        return Array.AsReadOnly(new ICapability[]
        {
            MachineInstanceCatalog.LoaderPresent.Observe(!vanilla, timestamp, source),
            MachineInstanceCatalog.LoaderType.Observe(loader.Kind.ToString(), timestamp, source),
            loader.Version is { Length: > 0 } version
                ? MachineInstanceCatalog.LoaderVersion.Observe(version, timestamp, source)
                : MachineInstanceCatalog.LoaderVersion.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "加载器版本未知"),
            MachineInstanceCatalog.LoaderComplete.Observe(chainComplete, timestamp, source),
            MachineInstanceCatalog.LoaderMinecraftCompatible.Observe(chainComplete, timestamp, source),
            MachineInstanceCatalog.LoaderMetadataValid.Observe(true, timestamp, source),
            // derived.* are this provider's own definitions — computing them in place is the
            // ownership-clean path (cross-provider derivations go through the broker pass).
            MachineInstanceCatalog.LoaderDerivedMissing.Observe(vanilla, timestamp, source),
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
        IJavaRuntimeLocator locator,
        string? minecraftRootDirectory,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(minecraftRootDirectory)
            || await MinecraftPrimaryInstanceScope.ResolveAsync(minecraftRootDirectory, cancellationToken).ConfigureAwait(false) is not { } primary)
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
        MinecraftModLoaderDescriptor loader = MinecraftModLoaderDetector.Detect(primary.Manifests.Current);
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
        JavaSelectionResult selection = await new JavaSelectionService(locator)
            .SelectAsync(request, new AutoSelectJavaPreference(), cancellationToken).ConfigureAwait(false);

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
            selection.Success
                ? MachineInstanceCatalog.JavaCompatibilityMinecraft.Observe(true, timestamp, source)
                : MachineInstanceCatalog.JavaCompatibilityMinecraft.Observe(false, timestamp, source + $"（{selection.FailureReason}）"),
            requirement.Success
                ? MachineInstanceCatalog.JavaCompatibilityHard.Observe(selection.Success, timestamp, source)
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
