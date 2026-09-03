using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using PCL.Services.Accounts;
using PCL.Services.Minecraft.Java;
using PCL.Services.Minecraft.Libraries;
using PCL.Services.Minecraft.ModLoaders;
using PCL.Services.Settings;
using PCL.Xsr;

namespace PCL.Services.Minecraft.Launch;

/// <summary>The concrete platform facts required by Mojang rules and native selection.</summary>
public readonly record struct MinecraftLaunchPlatform(
    MinecraftLibraryOperatingSystem OperatingSystem,
    string OperatingSystemVersion,
    bool Is64BitArchitecture,
    bool IsArm64Architecture)
{
    public static MinecraftLaunchPlatform Detect()
    {
        MinecraftLibraryOperatingSystem operatingSystem = System.OperatingSystem.IsWindows()
            ? MinecraftLibraryOperatingSystem.Win32
            : System.OperatingSystem.IsMacOS()
                ? MinecraftLibraryOperatingSystem.MacOs
                : System.OperatingSystem.IsLinux()
                    ? MinecraftLibraryOperatingSystem.Linux
                    : throw new PlatformNotSupportedException(
                        "Minecraft launch is not supported on this operating system.");
        Architecture architecture = RuntimeInformation.OSArchitecture;
        return new MinecraftLaunchPlatform(
            operatingSystem,
            Environment.OSVersion.Version.ToString(),
            architecture is Architecture.X64 or Architecture.Arm64,
            architecture == Architecture.Arm64);
    }
}

/// <summary>Prepared product launch input and the Java contract which selected its runtime.</summary>
public sealed record MinecraftLaunchPreparation(
    MinecraftInstanceDescriptor Instance,
    MinecraftLaunchRequest Request,
    JavaRequirementResolution JavaRequirement);

/// <summary>
/// Product-level launch orchestration. Callers identify an instance and account; this service
/// owns manifests, inheritance, platform, credentials, Java, settings, planning, and execution.
/// </summary>
public sealed class MinecraftLaunchCoordinator
{
    private readonly string _minecraftRootDirectory;
    private readonly string _javaRuntimeRootDirectory;
    private readonly MinecraftInstanceDiscovery _instances;
    private readonly AccountService _accounts;
    private readonly SettingsService _settings;
    private readonly JavaSelectionService _javaSelection;
    private readonly IJavaRuntimeInstaller _javaInstaller;
    private readonly MinecraftLaunchExecutor _executor;
    private readonly MinecraftLaunchPlatform _platform;

    public MinecraftLaunchCoordinator(
        string minecraftRootDirectory,
        string javaRuntimeRootDirectory,
        MinecraftInstanceDiscovery instances,
        AccountService accounts,
        SettingsService settings,
        JavaSelectionService javaSelection,
        IJavaRuntimeInstaller javaInstaller,
        MinecraftLaunchExecutor executor,
        MinecraftLaunchPlatform? platform = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftRootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(javaRuntimeRootDirectory);
        _minecraftRootDirectory = Path.GetFullPath(minecraftRootDirectory);
        _javaRuntimeRootDirectory = Path.GetFullPath(javaRuntimeRootDirectory);
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _javaSelection = javaSelection ?? throw new ArgumentNullException(nameof(javaSelection));
        _javaInstaller = javaInstaller ?? throw new ArgumentNullException(nameof(javaInstaller));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _platform = platform ?? MinecraftLaunchPlatform.Detect();
        if (_platform.OperatingSystem == MinecraftLibraryOperatingSystem.Unknown)
        {
            throw new ArgumentException(
                "The launch platform must identify a concrete Mojang operating system.",
                nameof(platform));
        }
    }

    public async ValueTask<XsrResult<MinecraftLaunchPreparation>> PrepareAsync(
        string instanceId,
        int accountIndex,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return XsrResult.Failure<MinecraftLaunchPreparation>(
                MinecraftErrors.InvalidRequest("an instance id is required."));
        }

        try
        {
            IReadOnlyList<MinecraftInstanceDescriptor> installed = await _instances
                .DiscoverAsync(_minecraftRootDirectory, cancellationToken)
                .ConfigureAwait(false);
            MinecraftInstanceDescriptor? instance = installed.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, instanceId, StringComparison.OrdinalIgnoreCase));
            if (instance is null)
            {
                return XsrResult.Failure<MinecraftLaunchPreparation>(
                    MinecraftErrors.InstanceNotFound(instanceId));
            }

            XsrResult<LaunchProfile> profileResult = _accounts.GetProfile(accountIndex);
            if (!profileResult.IsSuccess)
            {
                return XsrResult.Failure<MinecraftLaunchPreparation>(profileResult.Error!);
            }

            XsrResult<MinecraftLaunchIdentity> identityResult = ResolveIdentity(profileResult.Value);
            if (!identityResult.IsSuccess)
            {
                return XsrResult.Failure<MinecraftLaunchPreparation>(identityResult.Error!);
            }

            MinecraftResolvedVersionManifests manifests = await MinecraftVersionJsonReader
                .ResolveAsync(instance, _minecraftRootDirectory, cancellationToken)
                .ConfigureAwait(false);
            MinecraftModLoaderDescriptor loader = MinecraftModLoaderDetector.Detect(manifests.Current);
            MinecraftJavaRequirementRequest javaRequest = CreateJavaRequirement(
                instance,
                manifests,
                loader);
            JavaPreference preference = instance.Metadata.JavaSelectionMode == 2
                && !string.IsNullOrWhiteSpace(instance.Metadata.SelectedJavaPath)
                ? new ExistingJavaPreference(instance.Metadata.SelectedJavaPath)
                : new AutoSelectJavaPreference();
            JavaSelectionResult java = await _javaSelection
                .SelectAsync(javaRequest, preference, cancellationToken)
                .ConfigureAwait(false);
            XsrResult<ResolvedJava> resolvedJava = await ResolveJavaAsync(
                java,
                preference,
                loader.Kind is MinecraftModLoaderKind.Forge or MinecraftModLoaderKind.NeoForge,
                cancellationToken).ConfigureAwait(false);
            if (!resolvedJava.IsSuccess)
            {
                return XsrResult.Failure<MinecraftLaunchPreparation>(resolvedJava.Error!);
            }

            MinecraftLaunchRequest request = CreateRequest(
                instance,
                manifests,
                loader,
                identityResult.Value,
                resolvedJava.Value);
            return XsrResult.Success(new MinecraftLaunchPreparation(
                instance,
                request,
                java.Requirement));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or ArgumentException
            or HttpRequestException
            or PlatformNotSupportedException)
        {
            return XsrResult.Failure<MinecraftLaunchPreparation>(
                MinecraftErrors.LaunchPreparationFailed(exception.Message));
        }
    }

    public async ValueTask<XsrResult> StartAsync(
        string instanceId,
        int accountIndex,
        CancellationToken cancellationToken = default)
    {
        XsrResult<MinecraftLaunchPreparation> preparation = await PrepareAsync(
            instanceId,
            accountIndex,
            cancellationToken).ConfigureAwait(false);
        if (!preparation.IsSuccess)
        {
            return XsrResult.Failure(preparation.Error!);
        }

        try
        {
            MinecraftLaunchPlan plan = MinecraftLaunchPlanner.CreatePlan(preparation.Value.Request);
            await _executor.ExecuteAsync(plan, preparation.Value.Instance.Id, cancellationToken)
                .ConfigureAwait(false);
            return XsrResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or ArgumentException)
        {
            return XsrResult.Failure(MinecraftErrors.LaunchFailed(exception.Message));
        }
    }

    private async ValueTask<XsrResult<ResolvedJava>> ResolveJavaAsync(
        JavaSelectionResult selection,
        JavaPreference preference,
        bool hasForge,
        CancellationToken cancellationToken)
    {
        if (selection.Success && selection.SelectedJava is { } selected)
        {
            string installedExecutable = SelectExecutable(selected.Installation);
            return XsrResult.Success(new ResolvedJava(
                installedExecutable,
                selected.Installation.MajorVersion));
        }

        if (selection.FailureReason != JavaSelectionFailureReason.NoCompatibleRuntime)
        {
            return XsrResult.Failure<ResolvedJava>(MinecraftErrors.JavaUnavailable(
                selection.Detail ?? "the Java requirement could not be resolved."));
        }

        // An explicit, incompatible Java choice is a user-visible error. Never replace it with
        // a downloaded runtime behind the user's back.
        if (preference is ExistingJavaPreference)
        {
            return XsrResult.Failure<ResolvedJava>(MinecraftErrors.JavaUnavailable(
                "the selected Java executable is missing or incompatible with this instance."));
        }

        JavaRuntimeAcquisitionDecision acquisition = JavaRuntimeAcquisitionPlanner.Plan(
            selection.Requirement,
            hasForge);
        if (!acquisition.CanAutoDownload || string.IsNullOrWhiteSpace(acquisition.DownloadComponent))
        {
            return XsrResult.Failure<ResolvedJava>(MinecraftErrors.JavaUnavailable(
                $"no compatible Java runtime is installed and automatic acquisition is blocked ({acquisition.BlockReason})."));
        }

        string acquiredExecutable = await _javaInstaller.InstallAsync(
            acquisition.DownloadComponent,
            _javaRuntimeRootDirectory,
            progress: null,
            cancellationToken).ConfigureAwait(false);
        if (!File.Exists(acquiredExecutable))
        {
            return XsrResult.Failure<ResolvedJava>(MinecraftErrors.JavaUnavailable(
                "the acquired Java runtime did not provide an executable."));
        }

        int major = JavaMajor(selection.Requirement.Range.Minimum);
        return XsrResult.Success(new ResolvedJava(SelectWindowedSibling(acquiredExecutable), major));
    }

    private MinecraftLaunchRequest CreateRequest(
        MinecraftInstanceDescriptor instance,
        MinecraftResolvedVersionManifests manifests,
        MinecraftModLoaderDescriptor loader,
        MinecraftLaunchIdentity identity,
        ResolvedJava java)
    {
        MinecraftInstanceMetadata metadata = instance.Metadata;
        int width = GetSetting("LaunchArgumentWindowWidth", 854);
        int height = GetSetting("LaunchArgumentWindowHeight", 480);
        string customJvm = FirstNonEmpty(
            metadata.JvmArguments,
            GetSetting("LaunchAdvanceJvm", string.Empty));
        string customGame = FirstNonEmpty(
            metadata.GameArguments,
            GetSetting("LaunchAdvanceGame", string.Empty));
        string versionType = FirstNonEmpty(
            metadata.CustomInfo,
            GetSetting("LaunchArgumentInfo", "PCLN"),
            "PCL-N");
        DateTimeOffset? releaseTime = ReadReleaseTime(manifests) ?? instance.Version.ReleaseTime;

        return new MinecraftLaunchRequest
        {
            VersionJson = manifests.Current,
            InheritedVersionJsons = manifests.Inherited,
            VersionId = instance.VersionId,
            InstanceDirectory = instance.DirectoryPath,
            MinecraftRootDirectory = _minecraftRootDirectory,
            PlayerName = identity.PlayerName,
            PlayerUuid = identity.PlayerUuid,
            AccessToken = identity.AccessToken,
            IdentityMode = identity.Mode,
            JavaExecutablePath = java.ExecutablePath,
            JavaMajorVersion = java.MajorVersion,
            MemoryMegabytes = ResolveMemoryMegabytes(metadata, loader.Kind),
            Width = Math.Max(1, width),
            Height = Math.Max(1, height),
            Fullscreen = GetSetting("LaunchArgumentWindowType", 1) == 0,
            IsolatedGameDirectory = metadata.InstanceIsolation,
            CustomJvmArguments = string.IsNullOrWhiteSpace(customJvm) ? null : customJvm,
            CustomGameArguments = string.IsNullOrWhiteSpace(customGame) ? null : customGame,
            ClasspathHeadEntries = SplitClasspathHead(metadata.ClasspathHead),
            Server = string.IsNullOrWhiteSpace(metadata.ServerToEnter) ? null : metadata.ServerToEnter,
            ReleaseTime = releaseTime,
            LauncherName = "PCL-N",
            LauncherVersion = "2.0.0",
            VersionType = versionType,
            UseSystemGlfw = metadata.UseSystemGlfw || GetSetting("LaunchUseSystemGlfw", false),
            HasCleanroom = loader.Kind == MinecraftModLoaderKind.Cleanroom,
            OperatingSystem = _platform.OperatingSystem,
            OperatingSystemVersion = _platform.OperatingSystemVersion,
            Is64BitArchitecture = _platform.Is64BitArchitecture,
            IsArm64Architecture = _platform.IsArm64Architecture,
            Features = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["has_custom_resolution"] = true,
                ["is_demo_user"] = false,
            },
        };
    }

    private static MinecraftJavaRequirementRequest CreateJavaRequirement(
        MinecraftInstanceDescriptor instance,
        MinecraftResolvedVersionManifests manifests,
        MinecraftModLoaderDescriptor loader)
    {
        (int? major, string? component) = ReadManifestJava(manifests);
        MinecraftGameVersion? gameVersion = ResolveGameVersion(manifests);
        return new MinecraftJavaRequirementRequest
        {
            MinecraftVersion = gameVersion,
            HasReliableVanillaVersion = gameVersion is not null,
            ReleaseTime = ReadReleaseTime(manifests) ?? instance.Version.ReleaseTime,
            ManifestJavaMajorVersion = major,
            ManifestJavaComponent = component,
            HasOptiFine = loader.Kind == MinecraftModLoaderKind.OptiFine,
            HasForge = loader.Kind is MinecraftModLoaderKind.Forge or MinecraftModLoaderKind.NeoForge,
            ForgeVersion = loader.Version,
            HasCleanroom = loader.Kind == MinecraftModLoaderKind.Cleanroom,
            CleanroomVersion = loader.Version ?? ReadLoaderVersion(manifests.Current, "cleanroom"),
            HasFabric = loader.Kind is MinecraftModLoaderKind.Fabric or MinecraftModLoaderKind.Quilt,
            HasLiteLoader = loader.Kind == MinecraftModLoaderKind.LiteLoader,
            HasLabyMod = loader.Kind == MinecraftModLoaderKind.LabyMod,
        };
    }

    private static XsrResult<MinecraftLaunchIdentity> ResolveIdentity(LaunchProfile profile)
    {
        if (profile.Kind == LaunchProfileKind.Offline)
        {
            (string name, string uuid) = MinecraftOfflineIdentity.Resolve(
                profile.Username,
                profile.Uuid);
            return XsrResult.Success(new MinecraftLaunchIdentity(
                name,
                uuid,
                "0",
                MinecraftLaunchIdentityMode.Offline));
        }

        if (profile.Kind == LaunchProfileKind.Microsoft)
        {
            if (string.IsNullOrWhiteSpace(profile.Uuid)
                || string.IsNullOrWhiteSpace(profile.AccessToken))
            {
                return XsrResult.Failure<MinecraftLaunchIdentity>(
                    MinecraftErrors.UnsupportedAccount(
                        "the Microsoft profile has no launch UUID or access token; sign in again."));
            }

            return XsrResult.Success(new MinecraftLaunchIdentity(
                profile.Username,
                profile.Uuid,
                profile.AccessToken,
                MinecraftLaunchIdentityMode.Microsoft));
        }

        return XsrResult.Failure<MinecraftLaunchIdentity>(MinecraftErrors.UnsupportedAccount(
            $"the {profile.Kind} profile requires Authlib Injector preparation which is not yet available."));
    }

    private static (int? Major, string? Component) ReadManifestJava(
        MinecraftResolvedVersionManifests manifests)
    {
        foreach (JsonObject manifest in EnumerateEffectiveOrder(manifests))
        {
            if (manifest["javaVersion"] is not JsonObject java)
            {
                continue;
            }

            int? major = java["majorVersion"]?.GetValue<int>();
            string? component = java["component"]?.ToString();
            if (major is not null || !string.IsNullOrWhiteSpace(component))
            {
                return (major, component);
            }
        }

        return (null, null);
    }

    private static MinecraftGameVersion? ResolveGameVersion(
        MinecraftResolvedVersionManifests manifests)
    {
        foreach (JsonObject manifest in EnumerateEffectiveOrder(manifests))
        {
            if (MinecraftGameVersion.TryParse(manifest["id"]?.ToString(), out MinecraftGameVersion parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadReleaseTime(MinecraftResolvedVersionManifests manifests)
    {
        foreach (JsonObject manifest in EnumerateEffectiveOrder(manifests))
        {
            string? raw = manifest["releaseTime"]?.ToString() ?? manifest["time"]?.ToString();
            if (DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static IEnumerable<JsonObject> EnumerateEffectiveOrder(
        MinecraftResolvedVersionManifests manifests)
    {
        yield return manifests.Current;
        foreach (JsonObject inherited in manifests.Inherited)
        {
            yield return inherited;
        }
    }

    private static string? ReadLoaderVersion(JsonObject manifest, string marker)
    {
        string? id = manifest["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        int index = id.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        index += marker.Length;
        while (index < id.Length && id[index] is '-' or '_' or ' ')
        {
            index++;
        }

        int end = index;
        while (end < id.Length && (char.IsDigit(id[end]) || id[end] is '.'))
        {
            end++;
        }

        return end > index ? id[index..end] : null;
    }

    private int ResolveMemoryMegabytes(
        MinecraftInstanceMetadata metadata,
        MinecraftModLoaderKind loader)
    {
        if (metadata.MemorySolution == 1)
        {
            return SliderValueToMemoryMegabytes(metadata.CustomMemorySize);
        }

        if (GetSetting("LaunchRamType", 0) == 1)
        {
            return SliderValueToMemoryMegabytes(GetSetting("LaunchRamCustom", 15));
        }

        double availableGigabytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes > 0
            ? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024d / 1024d
            : 4d;
        double target = loader switch
        {
            MinecraftModLoaderKind.Forge or MinecraftModLoaderKind.NeoForge
                or MinecraftModLoaderKind.Fabric or MinecraftModLoaderKind.Quilt
                or MinecraftModLoaderKind.Cleanroom => 4.5d,
            MinecraftModLoaderKind.OptiFine => 3d,
            _ => 2.5d,
        };
        return Math.Max(512, (int)Math.Round(Math.Min(target, Math.Max(1.5d, availableGigabytes * 0.4d)) * 1024d));
    }

    private static int SliderValueToMemoryMegabytes(int value)
    {
        double gigabytes = value switch
        {
            <= 12 => value * 0.1d + 0.3d,
            <= 25 => (value - 12) * 0.5d + 1.5d,
            <= 33 => value - 25d + 8d,
            _ => (value - 33d) * 2d + 16d,
        };
        return Math.Max(256, (int)Math.Round(gigabytes * 1024d));
    }

    private T GetSetting<T>(string key, T fallback)
    {
        XsrResult<T> result = _settings.GetValue<T>(key);
        return result.IsSuccess ? result.Value : fallback;
    }

    private string SelectExecutable(JavaInstallation installation)
    {
        bool forceConsole = GetSetting("LaunchAdvanceNoJavaw", false);
        return !forceConsole && installation.WindowedJavaExecutablePath is { Length: > 0 } windowed
            ? windowed
            : installation.JavaExecutablePath;
    }

    private string SelectWindowedSibling(string executable)
    {
        if (!OperatingSystem.IsWindows() || GetSetting("LaunchAdvanceNoJavaw", false))
        {
            return executable;
        }

        string windowed = Path.Combine(Path.GetDirectoryName(executable)!, "javaw.exe");
        return File.Exists(windowed) ? windowed : executable;
    }

    private static string[] SplitClasspathHead(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split([';', Path.PathSeparator], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static int JavaMajor(Version version) => version.Major == 1 ? version.Minor : version.Major;

    private sealed record MinecraftLaunchIdentity(
        string PlayerName,
        string PlayerUuid,
        string AccessToken,
        MinecraftLaunchIdentityMode Mode);

    private sealed record ResolvedJava(string ExecutablePath, int MajorVersion);
}
