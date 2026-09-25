using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Nexa.Services.Downloads;
using Nexa.Services.Minecraft.Assets;
using Nexa.Services.Minecraft.Downloads;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Libraries;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Settings;
using Nexa.Services.Tasks;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Install;

public sealed record MinecraftInstallAddon(InstallLoader Kind, string Version, IReadOnlyList<InstallDownload>? Downloads = null);

public sealed record MinecraftInstallCommand(
    string RootDirectory,
    string GameVersion,
    InstallLoader? Loader = null,
    string? LoaderBuild = null,
    IReadOnlyList<MinecraftInstallAddon>? Addons = null,
    string? InstanceName = null,
    string? EditFingerprint = null)
{
    internal string? ReuseRoot { get; init; }
    public LocalJarArtifact? LocalInstaller { get; init; }
    internal bool PreparingEdit { get; init; }
    internal string? ModsRelativeDirectory { get; init; }
    public bool? InheritVanilla { get; init; }
    public string? NewInstanceName { get; init; }
}

public sealed record MinecraftInstallResult(string InstanceId, string InstanceDirectory);

public static class MinecraftInstallRoutes
{
    public static readonly XsrSemanticId Run = XsrSemanticId.Parse("minecraft.install.run");
    public static readonly XsrSemanticId Recover = XsrSemanticId.Parse("minecraft.install.recovery");
}

/// <summary>Resolves the remote version documents an install needs; tests inject in-memory fakes.</summary>
public interface IMinecraftInstallMetadataSource
{
    Task<JsonObject> FetchVanillaVersionJsonAsync(string gameVersion, CancellationToken cancellationToken);

    Task<JsonObject> FetchLoaderProfileJsonAsync(InstallLoader loader, string gameVersion, string build, CancellationToken cancellationToken);

    Task<JsonObject> FetchAssetIndexJsonAsync(string indexUrl, CancellationToken cancellationToken);
}

/// <summary>
/// Runs real installs into a Minecraft root: resolves the vanilla version JSON (and, for the
/// Fabric family, the loader profile JSON), plans every file through the shared download
/// planners with bmclapi failover ordering, writes the version directory, and reports the
/// whole run as one task-center task with byte-accurate progress. Installer-based loaders
/// execute in an isolated root and publish their version only after validation.
/// </summary>
public sealed partial class MinecraftInstallService : IDisposable
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private static readonly string[] StagePlan = ["版本信息", "游戏文件", "加载器", "附加组件", "完成"];

    private readonly TaskCenterService _tasks;
    private readonly DownloadService _downloads;
    private readonly IInstallCatalogSource? _catalog;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly IMinecraftInstallMetadataSource _metadata;
    private readonly Func<string, IDownloadConnection>? _connectionFactory;
    private readonly IMinecraftLoaderInstaller _loaderInstaller;
    private readonly Func<bool>? _inheritVanilla;
    private readonly SettingsPolicyService? _settingsPolicy;
    private readonly XsrStateStore? _hostStore;

    public MinecraftInstallService(
        TaskCenterService tasks,
        DownloadService downloads,
        IInstallCatalogSource? catalog = null,
        HttpClient? http = null,
        IMinecraftInstallMetadataSource? metadata = null,
        Func<string, IDownloadConnection>? connectionFactory = null,
        IMinecraftLoaderInstaller? loaderInstaller = null,
        Func<bool>? inheritVanilla = null,
        SettingsPolicyService? settingsPolicy = null,
        XsrStateStore? hostStore = null)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _catalog = catalog;
        _ownsHttp = http is null && metadata is null;
        _http = http ?? new HttpClient();
        _metadata = metadata ?? new HttpMinecraftInstallMetadataSource(_http);
        _connectionFactory = connectionFactory;
        _loaderInstaller = loaderInstaller ?? new ForgeInstallService(_downloads, _http, connectionFactory);
        _inheritVanilla = inheritVanilla;
        _settingsPolicy = settingsPolicy; _hostStore = hostStore;
    }

    /// <summary>Raised with the Minecraft root once an install commits, so the version library can rescan.</summary>
    public event Action<string>? Installed;
    public event Action<string, string, string>? Renamed;

    public async Task<XsrResult<MinecraftInstallResult>> InstallAsync(
        MinecraftInstallCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.RootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.GameVersion);

        string loaderName = command.Loader is { } loader ? LoaderDisplayName(loader) : null!;
        string title = command.Loader is null
            ? $"安装 Minecraft {command.GameVersion}"
            : $"安装 Minecraft {command.GameVersion} · {loaderName}";
        if (command.EditFingerprint is not null) title = "修改版本 " + command.InstanceName;
        string taskId = "install:" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        using ITaskCenterTask task = _tasks.Begin(new TaskCenterStart(taskId, title, StagePlan));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, task.CancellationToken);
        try
        {
            using var recoveryOperation = await InstanceRecoveryOperationGate.EnterOperationAsync(
                Path.GetFullPath(command.RootDirectory), linked.Token).ConfigureAwait(false);
            command = command with { InheritVanilla = command.InheritVanilla ?? _inheritVanilla?.Invoke() ?? false };
            bool renaming = command.NewInstanceName is { } requested && requested != command.InstanceName;
            if (renaming && (command.EditFingerprint is null || _settingsPolicy is null || _hostStore is null
                || !MinecraftVersionPaths.IsSafeReference(command.NewInstanceName)
                || Path.Exists(Path.Combine(command.RootDirectory, "versions", command.NewInstanceName!))
                    && !MinecraftLibraryService.PathComparer.Equals(command.NewInstanceName, command.InstanceName)))
                throw new InvalidDataException("无法改名：请检查名称冲突或重新打开修改页。");
            if (command.EditFingerprint is not null && _hostStore is not null)
                MinecraftInstanceRenamer.EnsureIdle(command.RootDirectory, _hostStore, linked.Token);
            MinecraftInstallResult result = command.EditFingerprint is null
                ? await InstallNewAsync(command, task, linked.Token).ConfigureAwait(false)
                : await ReinstallAsync(command, task, linked.Token).ConfigureAwait(false);
            if (renaming)
            {
                var current = await MinecraftInstallEditService.ReadAsync(new(command.RootDirectory, result.InstanceId), linked.Token).ConfigureAwait(false);
                await MinecraftInstanceRenamer.RenameAsync(command.RootDirectory, result.InstanceId, command.NewInstanceName!,
                    current.GameVersion, current.Fingerprint, _settingsPolicy!, _hostStore!, linked.Token).ConfigureAwait(false);
                string old = result.InstanceId;
                result = new(command.NewInstanceName!, Path.Combine(command.RootDirectory, "versions", command.NewInstanceName!));
                task.Complete("已修改 " + result.InstanceId);
                Renamed?.Invoke(command.RootDirectory, old, result.InstanceId);
                Installed?.Invoke(command.RootDirectory);
            }
            return XsrResult.Success(result);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // Either the task card or the dispatching caller stopped the run; both leave a
            // canceled task card and a cancelled result — never a raw escape to the caller.
            task.Canceled();
            return XsrResult.Failure<MinecraftInstallResult>(XsrRuntimeErrors.Cancelled());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            task.Fail(exception.Message);
            return XsrResult.Failure<MinecraftInstallResult>(XsrRuntimeErrors.HandlerFaulted());
        }
    }

    private async Task<MinecraftInstallResult> RunAsync(
        MinecraftInstallCommand command,
        ITaskCenterTask task,
        CancellationToken token,
        IMinecraftInstallMetadataSource? metadataSource = null,
        bool deferCompletion = false)
    {
        IMinecraftInstallMetadataSource metadata = metadataSource ?? _metadata;
        bool processorLoader = ValidatePrimaryLoader(command);
        string game = command.GameVersion;
        string gameName = SafeName(game);
        string instanceId = InstallInstanceId(command);
        if (command.Loader is not null && instanceId == gameName && !command.PreparingEdit)
            throw new InvalidOperationException("加载器实例名称不能与原版版本目录相同。");
        string root = Path.GetFullPath(command.RootDirectory);
        string versionsRoot = Path.Combine(root, "versions");
        string gameDirectory = Path.Combine(versionsRoot, gameName);
        string instanceDirectory = Path.Combine(versionsRoot, instanceId);
        Directory.CreateDirectory(gameDirectory);
        Directory.CreateDirectory(instanceDirectory);

        // ── 版本信息: resolve the vanilla manifest entry and the loader profile. Both version
        // documents COMMIT LAST: discovery lists instances by their version json, so a run that
        // dies mid-transfer leaves an invisible, resumable directory instead of a launchable
        // half-install whose missing libraries would kill the JVM before its window appears.
        task.Report(StagePlan[0], "正在获取版本清单", 0.02, 0, 0, 0);
        JsonObject vanillaJson = await metadata.FetchVanillaVersionJsonAsync(game, token).ConfigureAwait(false);
        JsonObject? loaderJson = !processorLoader && command.Loader is { } profileLoader && command.LoaderBuild is { } build
            ? await metadata.FetchLoaderProfileJsonAsync(profileLoader, game, build, token).ConfigureAwait(false)
            : null;
        if (!processorLoader && loaderJson is null && instanceId != gameName)
            loaderJson = new JsonObject { ["id"] = instanceId, ["inheritsFrom"] = gameName };
        if (loaderJson is not null)
        {
            loaderJson["id"] = instanceId;
            SetLoaderParent(loaderJson, game);
        }

        // A stage boundary is not the whole task: reporting 1 here made the card flash 100%
        // before the first transfer dropped it back near zero.
        task.Report(StagePlan[0], "版本信息就绪", 0.02, 0, 0, 0);

        // ── Plan every transfer once so file counts and the byte budget are known upfront.
        MinecraftLaunchPlatform platform = MinecraftLaunchPlatform.Detect();
        List<PlannedFile> gameFiles = [];
        List<PlannedFile> loaderFiles = [];
        List<PlannedFile> addonFiles = [];

        MinecraftAssetIndexDownloadPlan assetIndexPlan = MinecraftClientDownloadPlanner.CreateAssetIndexPlan(
            new MinecraftAssetIndexDownloadPlanRequest
            {
                VersionJson = vanillaJson,
                MinecraftRootDirectory = root,
            });
        if (assetIndexPlan.HasDownload)
        {
            gameFiles.Add(new PlannedFile(
                MinecraftDownloadSourcePlanner.GetLauncherOrMetaSources(assetIndexPlan.Url!, true),
                assetIndexPlan.LocalPath!, null, 0));
        }

        MinecraftClientJarDownloadPlan clientPlan = MinecraftClientDownloadPlanner.CreateClientJarPlan(
            new MinecraftClientJarDownloadPlanRequest
            {
                VersionJson = vanillaJson,
                InstanceDirectory = gameDirectory,
                VersionName = game,
            });
        if (clientPlan.File is { } client)
        {
            gameFiles.Add(new PlannedFile(
                MinecraftDownloadSourcePlanner.GetLauncherOrMetaSources(client.Url, true),
                client.LocalPath, client.Sha1, client.ActualSize));
        }

        gameFiles.AddRange(LibraryFiles(MinecraftLibraryResolver.Resolve(
            new MinecraftLibraryResolutionRequest
            {
                VersionJson = vanillaJson,
                MinecraftRootDirectory = root,
                OperatingSystem = platform.OperatingSystem,
                Is64BitArchitecture = platform.Is64BitArchitecture,
                IsArm64Architecture = platform.IsArm64Architecture,
                OperatingSystemVersion = platform.OperatingSystemVersion,
            }), root));
        loaderFiles.AddRange(loaderJson is null
            ? []
            : LibraryFiles(MinecraftLibraryResolver.Resolve(
                new MinecraftLibraryResolutionRequest
                {
                    VersionJson = loaderJson,
                    MinecraftRootDirectory = root,
                    OperatingSystem = platform.OperatingSystem,
                    Is64BitArchitecture = platform.Is64BitArchitecture,
                    IsArm64Architecture = platform.IsArm64Architecture,
                    OperatingSystemVersion = platform.OperatingSystemVersion,
                }), root));

        // Assets are planned after the index document exists on disk.
        JsonObject assetIndexJson;
        if (assetIndexPlan.HasDownload)
        {
            assetIndexJson = await metadata.FetchAssetIndexJsonAsync(assetIndexPlan.Url!, token).ConfigureAwait(false);
        }
        else if (assetIndexPlan.IndexId is { } existingId
            && File.Exists(Path.Combine(root, "assets", "indexes", existingId + ".json")))
        {
            assetIndexJson = JsonNode.Parse(await File.ReadAllTextAsync(
                Path.Combine(root, "assets", "indexes", existingId + ".json"), token)
                .ConfigureAwait(false))!.AsObject();
        }
        else
        {
            throw new InvalidOperationException("版本缺少资源索引信息。");
        }
        if (assetIndexPlan.HasDownload && assetIndexPlan.LocalPath is { } indexPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
            await File.WriteAllTextAsync(indexPath, assetIndexJson.ToJsonString(JsonOptions), token)
                .ConfigureAwait(false);
        }

        IReadOnlyList<MinecraftAssetToken> assets = MinecraftAssetListResolver.GetAssetList(
            new MinecraftAssetListRequest
            {
                IndexJson = assetIndexJson,
                MinecraftRootDirectory = root,
                InstanceDirectory = instanceDirectory,
            });
        Dictionary<string, MinecraftAssetFileState> assetStates = [];
        foreach (MinecraftAssetToken asset in assets)
        {
            assetStates[asset.LocalPath] = new MinecraftAssetFileState(File.Exists(asset.LocalPath), 0);
        }

        MinecraftAssetDownloadPlan assetPlan = MinecraftAssetDownloadPlanner.CreatePlan(
            new MinecraftAssetDownloadPlanRequest
            {
                Assets = assets,
                CheckHash = false,
                ExistingFiles = assetStates,
            });
        foreach (MinecraftAssetDownloadFile file in assetPlan.Files)
        {
            gameFiles.Add(new PlannedFile(
                MinecraftDownloadSourcePlanner.GetAssetSources(
                    MinecraftAssetListResolver.GetObjectUrl(file.Hash), true),
                file.LocalPath, file.Hash, file.ActualSize));
        }

        if (loaderJson?["_nexaLabyAssets"] is JsonObject labyAssets)
            foreach (var asset in labyAssets)
            {
                if (!MinecraftVersionPaths.IsSafeReference(asset.Key)) throw new InvalidDataException("LabyMod 资源名称无效。");
                string url = asset.Value!.ToString();
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new InvalidDataException("LabyMod 资源地址无效。");
                loaderFiles.Add(new PlannedFile([url], Path.Combine(root, "labymod-neo", "assets", asset.Key + ".jar"), Path.GetFileNameWithoutExtension(uri.AbsolutePath), 0));
            }
        Dictionary<string, InstallLoader> managedModKinds = [];
        if (command.Addons is { Count: > 0 })
        {
            string modsDirectory = ForgeInstallService.Contained(root, command.ModsRelativeDirectory ?? "mods");
            foreach (MinecraftInstallAddon addon in command.Addons)
            {
                IReadOnlyList<InstallDownload> downloads = await ResolveAddonDownloadsAsync(
                    game, addon, token, metadata as PersistentInstallMetadataSource).ConfigureAwait(false);
                InstallDownload download = downloads[0];
                Directory.CreateDirectory(modsDirectory);
                managedModKinds[Path.Combine(modsDirectory, SafeName(download.FileName))] = addon.Kind;
                addonFiles.Add(new PlannedFile(
                    downloads.Where(candidate => string.IsNullOrEmpty(download.Sha1)
                        || string.Equals(candidate.Sha1, download.Sha1, StringComparison.OrdinalIgnoreCase))
                        .Select(candidate => candidate.Url.ToString()).Distinct(StringComparer.Ordinal).ToArray(),
                    Path.Combine(modsDirectory, SafeName(download.FileName)),
                    download.Sha1,
                    download.Size));
            }
        }

        // ── 游戏文件 / 加载器 / 附加组件: one shared file budget across all sections. The
        // transfer list is deduplicated by destination — the same library can appear in both
        // the vanilla and loader sections, and counting both would desync CompletedFiles from
        // TotalFiles (a destination downloaded once skips its duplicates without counting).
        List<(string Stage, PlannedFile File)> planned = [];
        foreach (List<PlannedFile> files in (List<PlannedFile>[])[gameFiles, loaderFiles, addonFiles])
        {
            string stage = files == gameFiles ? StagePlan[1] : files == loaderFiles ? StagePlan[2] : StagePlan[3];
            foreach (PlannedFile file in files)
            {
                planned.Add((stage, file));
            }
        }

        // A file is reusable only when it PASSES verification — existence-with-content used
        // to certify truncated or corrupted artifacts as complete installs.
        List<(string Stage, PlannedFile File)> allFiles = [];
        foreach (string destination in planned
            .Select(static pair => pair.File.Destination)
            .Distinct(MinecraftLibraryService.PathComparer))
        {
            (string Stage, PlannedFile File) candidate = planned.First(
                pair => MinecraftLibraryService.PathComparer.Equals(pair.File.Destination, destination));
            if (metadata is PersistentInstallMetadataSource && string.IsNullOrEmpty(candidate.File.Expected.Sha1))
            {
                // A prior process may have preallocated or partially filled a size-only file.
                // Until a completed-artifact receipt exists, restart rather than certify it by size.
                allFiles.Add(candidate);
                continue;
            }
            if (command.ReuseRoot is { } reuseRoot && !File.Exists(candidate.File.Destination))
            {
                string original = ForgeInstallService.Contained(reuseRoot, Path.GetRelativePath(root, candidate.File.Destination));
                if (await MinecraftFileVerifier.VerifyAsync(candidate.File.Expected with { Path = original }, token).ConfigureAwait(false))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(candidate.File.Destination)!);
                    File.Copy(original, candidate.File.Destination);
                }
            }
            if (!await MinecraftFileVerifier.VerifyAsync(candidate.File.Expected, token).ConfigureAwait(false))
            {
                allFiles.Add(candidate);
            }
        }
        int totalFiles = allFiles.Count;
        int doneFiles = 0;
        foreach ((string Stage, PlannedFile File) pair in allFiles)
        {
            string stage = pair.Stage;
            PlannedFile file = pair.File;
            {
                token.ThrowIfCancellationRequested();
                if ((metadata is not PersistentInstallMetadataSource || !string.IsNullOrEmpty(file.Expected.Sha1))
                    && await MinecraftFileVerifier.VerifyAsync(file.Expected, token).ConfigureAwait(false))
                {
                    doneFiles++;
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(file.Destination)!);
                string stageLabel = allFiles.Count == 0 ? stage : $"{stage} · {Path.GetFileName(file.Destination)}";
                DownloadRequest request = new()
                {
                    AllowResume = !string.IsNullOrEmpty(file.Expected.Sha1),
                    Sources = file.Sources,
                    DestinationPath = file.Destination,
                    ConnectionFactory = _connectionFactory is { } factory
                        ? source => factory(source)
                        : source => new HttpConnection(_http, source),
                };
                DownloadTransferResult transfer = await DownloadPlannedFileAsync(
                    request, task, stage, stageLabel, doneFiles, totalFiles, token).ConfigureAwait(false);
                bool verified = transfer.Success
                    && await MinecraftFileVerifier.VerifyAsync(file.Expected, token).ConfigureAwait(false);
                if (!verified)
                {
                    // Mirror rate limits are bursty, and some mirrors commit truncated
                    // bodies: one delayed retry — deleting the bad artifact first — has
                    // saved whole installs that died at 99% on a single 403.
                    if (transfer.Success)
                    {
                        TryDelete(file.Destination);
                    }

                    await Task.Delay(FileRetryDelay, token).ConfigureAwait(false);
                    transfer = await DownloadPlannedFileAsync(
                        request, task, stage, stageLabel, doneFiles, totalFiles, token).ConfigureAwait(false);
                    verified = transfer.Success
                        && await MinecraftFileVerifier.VerifyAsync(file.Expected, token).ConfigureAwait(false);
                }

                if (!verified)
                {
                    TryDelete(file.Destination);
                    throw new InvalidOperationException(
                        $"下载 {Path.GetFileName(file.Destination)} 失败：{(transfer.Errors.Count > 0 ? transfer.Errors[0].Message : (transfer.Success ? "校验未通过" : "未知错误"))}");
                }

                doneFiles++;
            }
        }

        // The last transfer's report lags one file (it fires mid-download); close the file
        // count before completion so the card never reads 3/4 at 100%.
        if (totalFiles > 0)
        {
            task.Report(StagePlan[3], "下载完成", 0.999, totalFiles, totalFiles, 0);
        }

        if (processorLoader)
        {
            task.Report(StagePlan[2], "正在准备加载器安装器", 0.99, totalFiles, totalFiles, 0);
            loaderJson = await _loaderInstaller.InstallAsync(
                new(root, game, instanceId, command.Loader!.Value, command.LoaderBuild!, vanillaJson) { LocalInstaller = command.LocalInstaller },
                new InstallerProgress(message => task.Report(StagePlan[2], message, 0.99, totalFiles, totalFiles, 0)), token).ConfigureAwait(false);
            loaderJson["id"] = instanceId;
            SetLoaderParent(loaderJson, game);
        }

        if (loaderJson is not null)
            await AdditionalLoaderProfiles.VerifyLiteLoaderAsync(loaderJson, root, token).ConfigureAwait(false);

        bool standalone = loaderJson is not null && (command.InheritVanilla != true || instanceId == game || command.PreparingEdit);
        if (standalone)
        {
            loaderJson = loaderJson!["inheritsFrom"] is not null
                ? MinecraftLaunchPlanner.MergeManifests(loaderJson, [vanillaJson]) : loaderJson;
            loaderJson.Remove("inheritsFrom");
            loaderJson.Remove("jar");
            loaderJson["_minecraftVersion"] = game;
            string sourceJar = Path.Combine(gameDirectory, gameName + ".jar");
            string targetJar = Path.Combine(instanceDirectory, instanceId + ".jar");
            if (!MinecraftLibraryService.PathComparer.Equals(sourceJar, targetJar))
            {
                string temporary = targetJar + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var input = File.OpenRead(sourceJar))
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                        await input.CopyToAsync(output, token).ConfigureAwait(false);
                    File.Move(temporary, targetJar, overwrite: true);
                }
                finally { TryDelete(temporary); }
            }
        }
        JsonObject receipt = new() { ["game"] = game, ["loader"] = command.Loader?.ToString(), ["build"] = command.LoaderBuild };
        receipt["addons"] = new JsonArray((command.Addons ?? []).Select(addon => (JsonNode?)new JsonObject { ["loader"] = addon.Kind.ToString(), ["build"] = addon.Version }).ToArray());
        JsonArray managedMods = [];
        foreach (var file in addonFiles)
        {
            await using var stream = File.OpenRead(file.Destination);
            string hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            managedMods.Add((JsonNode)new JsonObject { ["path"] = Path.GetRelativePath(root, file.Destination).Replace('\\', '/'), ["sha256"] = hash, ["loader"] = managedModKinds[file.Destination].ToString() });
        }
        receipt["managedMods"] = managedMods;
        (loaderJson ?? vanillaJson)["_nexaInstall"] = receipt;

        // The documents land only now: the instance becomes discoverable exactly when its
        // files are complete. Re-runs skip existing files, so this commit is cheap.
        if (!standalone)
            await File.WriteAllTextAsync(
                Path.Combine(gameDirectory, gameName + ".json"),
                vanillaJson.ToJsonString(JsonOptions), token).ConfigureAwait(false);
        if (loaderJson is not null)
        {
            await File.WriteAllTextAsync(
                Path.Combine(instanceDirectory, instanceId + ".json"),
                loaderJson.ToJsonString(JsonOptions), token).ConfigureAwait(false);
        }

        if (!command.PreparingEdit && !deferCompletion)
        {
            task.Complete($"已安装 {instanceId}");
            Installed?.Invoke(root);
        }
        return new MinecraftInstallResult(instanceId, instanceDirectory);
    }

    private sealed class InstallerProgress(Action<string> report) : IProgress<string>
    { public void Report(string value) => report(value); }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Cleanup must not kill the install; a leftover bad file fails the next
            // verification and is re-downloaded then.
        }
    }

    private static readonly TimeSpan FileRetryDelay = TimeSpan.FromSeconds(3);
    // Per-chunk callbacks arrive at line speed; one publish per chunk floods the render
    // thread on fast links. Reports step by at least this fraction of the whole install.
    private const double ReportStep = 0.002d;

    private Task<DownloadTransferResult> DownloadPlannedFileAsync(
        DownloadRequest request,
        ITaskCenterTask task,
        string stage,
        string stageLabel,
        int doneFiles,
        int totalFiles,
        CancellationToken token)
    {
        double lastReported = -1d;
        return _downloads.DownloadAsync(
            request,
            progress =>
            {
                double overall = totalFiles == 0
                    ? 0.5
                    : (doneFiles + (progress.TotalBytes > 0
                          ? Math.Clamp(progress.DownloadedBytes / (double)progress.TotalBytes, 0d, 1d)
                          : 0d)) / totalFiles;
                if (overall - lastReported < ReportStep && overall < 0.999d)
                {
                    return;
                }

                lastReported = overall;
                task.Report(
                    stage,
                    stageLabel,
                    Math.Clamp(overall, 0d, 0.999d),
                    doneFiles,
                    totalFiles,
                    progress.BytesPerSecond);
            },
            token);
    }

    private static IEnumerable<PlannedFile> LibraryFiles(IReadOnlyList<MinecraftLibraryToken> libraries, string root)
    {
        foreach (MinecraftLibraryToken library in libraries)
        {
            if (library.IsLocal || string.IsNullOrWhiteSpace(library.Url))
            {
                continue;
            }

            // The mirror-first order stands, but a rate-limited bmclapi must not strand a
            // third-party artifact: the canonical URL rides along as the last resort.
            string[] sources = MinecraftDownloadSourcePlanner.GetLibrarySources(library.Url, true);
            if (!sources.Contains(library.Url, StringComparer.Ordinal))
            {
                sources = [.. sources, library.Url];
            }

            yield return new PlannedFile(sources, library.LocalPath, library.Sha1, library.Size);
        }
    }


    private async Task<IReadOnlyList<InstallDownload>> ResolveAddonDownloadsAsync(
        string gameVersion, MinecraftInstallAddon addon, CancellationToken token, PersistentInstallMetadataSource? persistent = null)
    {
        if (persistent is not null)
            return await persistent.FetchAddonDownloadsAsync(gameVersion, addon,
                cancellation => ResolveAddonDownloadsAsync(gameVersion, addon, cancellation), token).ConfigureAwait(false);
        if (addon.Downloads is { Count: > 0 })
        {
            foreach (InstallDownload selected in addon.Downloads)
                if (selected.Url.Scheme != Uri.UriSchemeHttps || Path.GetFileName(selected.FileName) != selected.FileName)
                    throw new InvalidOperationException("附属 Mod 下载信息无效。");
            return addon.Downloads;
        }
        if (addon.Kind == InstallLoader.OptiFine)
            return [new InstallDownload("BMCLAPI", "OptiFine_" + SafeName(addon.Version) + ".jar",
                new Uri(ForgeInstallService.InstallerUrl(InstallLoader.OptiFine, gameVersion, addon.Version)), null, 0)];
        if (_catalog is null)
        {
            throw new InvalidOperationException("没有可用的安装目录源，无法解析附加组件。");
        }

        // Addon catalogs are per-game: without the game version the provider filter is
        // meaningless and the merged source returns the wrong artifact set.
        IReadOnlyList<InstallCatalogVersion> versions =
            await _catalog.GetLoadersAsync(addon.Kind, gameVersion, token).ConfigureAwait(false);
        foreach (InstallCatalogVersion version in versions)
        {
            if (!string.Equals(version.Id, addon.Version, StringComparison.Ordinal))
            {
                continue;
            }

            if (version.Downloads is { Count: > 0 })
            {
                return version.Downloads;
            }

            break;
        }

        throw new InvalidOperationException(
            $"附加组件 {LoaderDisplayName(addon.Kind)} {addon.Version} 没有可用的下载信息。");
    }

    /// <summary>Version directory names must stay safe references; reject anything else outright.</summary>
    private static string SafeName(string value)
    {
        if (MinecraftVersionPaths.IsSafeReference(value))
        {
            return value;
        }

        StringBuilder builder = new();
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character) || character is '.' or '_' or '-')
            {
                builder.Append(character);
            }
        }

        string sanitized = builder.ToString();
        return MinecraftVersionPaths.IsSafeReference(sanitized)
            ? sanitized
            : throw new InvalidOperationException($"版本名 {value} 不能用作目录名。");
    }

    private static void SetLoaderParent(JsonObject profile, string game)
    {
        if (profile["inheritsFrom"] is null && profile["downloads"]?["client"] is not null)
            profile["jar"] = game;
        else profile["inheritsFrom"] = game;
    }

    private static bool IsProfileJsonLoader(InstallLoader loader) =>
        loader is InstallLoader.Fabric or InstallLoader.LegacyFabric or InstallLoader.Quilt or InstallLoader.LiteLoader or InstallLoader.LabyMod;

    private static string LoaderDisplayName(InstallLoader loader) => loader switch
    {
        InstallLoader.Fabric => "Fabric",
        InstallLoader.LegacyFabric => "Legacy Fabric",
        InstallLoader.Quilt => "Quilt",
        InstallLoader.Forge => "Forge",
        InstallLoader.NeoForge => "NeoForge",
        InstallLoader.FabricApi => "Fabric API",
        InstallLoader.Qsl => "QSL",
        InstallLoader.OptiFine => "OptiFine",
        InstallLoader.OptiFabric => "OptiFabric",
        InstallLoader.Cleanroom => "Cleanroom",
        InstallLoader.LiteLoader => "LiteLoader",
        InstallLoader.LabyMod => "LabyMod",
        _ => loader.ToString(),
    };

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private sealed record PlannedFile(string[] Sources, string Destination, string? Sha1, long Size)
    {
        public MinecraftExpectedFile Expected => new(
            Destination,
            Size > 0 ? Size : null,
            string.IsNullOrWhiteSpace(Sha1) ? null : Sha1);
    }

    /// <summary>Production metadata port: manifest → per-version JSON over the shared client.</summary>
    private sealed class HttpMinecraftInstallMetadataSource(HttpClient http) : IMinecraftInstallMetadataSource
    {
        private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

        public async Task<JsonObject> FetchVanillaVersionJsonAsync(
            string gameVersion, CancellationToken cancellationToken)
        {
            JsonObject manifest = await FetchJsonAsync(
                MinecraftDownloadSourcePlanner.GetLauncherOrMetaSources(ManifestUrl, true), cancellationToken)
                .ConfigureAwait(false);
            string? entryUrl = (manifest["versions"]?.AsArray() ?? [])
                .Select(static node => node as JsonObject)
                .Where(entry => entry is not null
                    && string.Equals(entry["id"]?.ToString(), gameVersion, StringComparison.Ordinal))
                .Select(entry => entry!["url"]?.ToString())
                .FirstOrDefault(static url => !string.IsNullOrWhiteSpace(url))
                ?? throw new InvalidOperationException($"版本清单中没有 Minecraft {gameVersion}。");
            return await FetchJsonAsync(
                MinecraftDownloadSourcePlanner.GetLauncherOrMetaSources(entryUrl, true), cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<JsonObject> FetchLoaderProfileJsonAsync(
            InstallLoader loader, string gameVersion, string build, CancellationToken cancellationToken)
        {
            if (loader == InstallLoader.LabyMod)
            {
                string[] parts = build.Split('+');
                if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("LabyMod 版本标识无效。");
                var manifest = await FetchJsonAsync([$"https://releases.r2.labymod.net/api/v1/manifest/{Uri.EscapeDataString(parts[0])}/latest.json"], cancellationToken).ConfigureAwait(false);
                if (manifest["commitReference"]?.ToString() != parts[2] || manifest["labyModVersion"]?.ToString() != parts[1])
                    throw new InvalidDataException("LabyMod 发布已更新，请刷新版本列表后重新选择。");
                var profile = await FetchJsonAsync([$"https://releases.r2.labymod.net/api/v1/download/manifest/labymod4/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(gameVersion)}/{Uri.EscapeDataString(parts[2])}.json"], cancellationToken).ConfigureAwait(false);
                var libraries = await FetchJsonAsync([$"https://releases.r2.labymod.net/api/v1/libraries/{Uri.EscapeDataString(parts[0])}.json"], cancellationToken).ConfigureAwait(false);
                return AdditionalLoaderProfiles.LabyMod(profile, manifest, libraries, gameVersion, parts[0]);
            }
            if (loader == InstallLoader.LiteLoader)
                return AdditionalLoaderProfiles.LiteLoader(await FetchJsonAsync(["https://dl.liteloader.com/versions/versions.json"], cancellationToken).ConfigureAwait(false), gameVersion, build);
            string host = loader switch
            {
                InstallLoader.Fabric => "meta.fabricmc.net/v2",
                InstallLoader.LegacyFabric => "meta.legacyfabric.net/v2",
                InstallLoader.Quilt => "meta.quiltmc.org/v3",
                _ => throw new ArgumentOutOfRangeException(nameof(loader)),
            };
            string url = $"https://{host}/versions/loader/{Uri.EscapeDataString(gameVersion)}/{Uri.EscapeDataString(build)}/profile/json";
            return await FetchJsonAsync([url], cancellationToken).ConfigureAwait(false);
        }

        public Task<JsonObject> FetchAssetIndexJsonAsync(string indexUrl, CancellationToken cancellationToken) =>
            FetchJsonAsync(
                MinecraftDownloadSourcePlanner.GetLauncherOrMetaSources(indexUrl, true),
                cancellationToken);

        private async Task<JsonObject> FetchJsonAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken)
        {
            string? lastError = null;
            foreach (string url in urls)
            {
                try
                {
                    using HttpResponseMessage response = await http.GetAsync(url, cancellationToken)
                        .ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    string body = await response.Content.ReadAsStringAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return JsonNode.Parse(body)?.AsObject()
                        ?? throw new FormatException($"响应不是 JSON 对象：{url}");
                }
                catch (Exception exception) when (exception is not OutOfMemoryException
                    and not OperationCanceledException)
                {
                    lastError = exception.Message;
                }
            }

            throw new InvalidOperationException($"获取版本信息失败：{lastError ?? "没有可用下载源"}");
        }
    }

    /// <summary>Adapts one HttpClient GET to the download engine's connection port.</summary>
    internal sealed class HttpConnection(HttpClient client, string source) : IDownloadConnection
    {
        private HttpResponseMessage? _response;

        public async ValueTask<DownloadConnectionInfo> StartAsync(
            long beginOffset, CancellationToken cancellationToken = default)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, source);
            request.Headers.UserAgent.ParseAdd("Nexa/2.0");
            if (beginOffset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(beginOffset, null);
            }

            _response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            _response.EnsureSuccessStatusCode();
            long length = _response.Content.Headers.ContentLength ?? -1;
            return new DownloadConnectionInfo(length, beginOffset, length >= 0 ? beginOffset + length - 1 : -1, false);
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Stream stream = _response?.Content is null
                ? throw new InvalidOperationException("The connection was never started.")
                : await _response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            _response?.Dispose();
            _response = null;
            return ValueTask.CompletedTask;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
