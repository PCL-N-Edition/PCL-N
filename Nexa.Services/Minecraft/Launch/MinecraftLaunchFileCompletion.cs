using System.Text.Json.Nodes;
using Nexa.Services.Downloads;
using Nexa.Services.Logging;
using Nexa.Services.Minecraft.Assets;
using Nexa.Services.Minecraft.Downloads;
using Nexa.Services.Minecraft.Libraries;

namespace Nexa.Services.Minecraft.Launch;

/// <summary>
/// The legacy 补全文件 stage: before the JVM starts, every file the launch plan references is
/// verified on disk and repaired through the shared download service — client jar, asset
/// index, asset objects, and the full inheritance chain's libraries (natives included). A
/// missing library would otherwise kill the JVM before its window appears, which reported as
/// an opaque launch failure. Files that exist with content are skipped, so completion after a
/// partial install resumes instead of re-downloading.
/// </summary>
public sealed class MinecraftLaunchFileCompletion : IDisposable
{
    private static readonly TimeSpan FileRetryDelay = TimeSpan.FromSeconds(3);

    private readonly DownloadService _downloads;
    private readonly LogService? _log;
    private readonly HttpClient _http = new();
    private readonly Func<string, IDownloadConnection>? _connectionFactory;

    public MinecraftLaunchFileCompletion(
        DownloadService downloads,
        LogService? log = null,
        Func<string, IDownloadConnection>? connectionFactory = null)
    {
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _log = log;
        _connectionFactory = connectionFactory;
    }

    public async ValueTask CompleteAsync(
        string minecraftRootDirectory,
        MinecraftInstanceDescriptor instance,
        MinecraftResolvedVersionManifests manifests,
        MinecraftLaunchPlatform platform,
        string method,
        MinecraftLaunchProgressPublisher? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftRootDirectory);

        string root = Path.GetFullPath(minecraftRootDirectory);
        // The chain reads current → nearest parent → … → root; the base (vanilla) manifest
        // owns the client jar and the asset index.
        JsonObject baseManifest = manifests.Inherited.Count > 0 ? manifests.Inherited[^1] : manifests.Current;
        string baseId = baseManifest["id"]?.ToString() ?? instance.VersionId;

        List<PendingFile> missing = [];

        MinecraftClientJarDownloadPlan clientPlan = MinecraftClientDownloadPlanner.CreateClientJarPlan(
            new MinecraftClientJarDownloadPlanRequest
            {
                VersionJson = baseManifest,
                InstanceDirectory = Path.Combine(root, "versions", baseId),
                VersionName = baseId,
            });
        if (clientPlan.File is { } client && !await HasVerifiedCorePatchAsync(instance, client.LocalPath, cancellationToken).ConfigureAwait(false))
        {
            AddIfMissing(missing,
                MinecraftDownloadSourcePlanner.GetLauncherOrMetaSources(client.Url, true),
                client.LocalPath, client.ActualSize, client.Sha1);
        }

        MinecraftAssetIndexDownloadPlan indexPlan = MinecraftClientDownloadPlanner.CreateAssetIndexPlan(
            new MinecraftAssetIndexDownloadPlanRequest
            {
                VersionJson = baseManifest,
                InheritedVersionJsons = manifests.Inherited,
                MinecraftRootDirectory = root,
            });
        if (indexPlan.HasDownload && indexPlan.LocalPath is { } indexPath)
        {
            AddIfMissing(missing,
                MinecraftDownloadSourcePlanner.GetLauncherOrMetaSources(indexPlan.Url!, true),
                indexPath, null, null);
        }

        // Libraries accumulate across the whole chain: the child's overrides win.
        Dictionary<string, (string Url, string? Sha1, long Size)> libraries = new(MinecraftLibraryService.PathComparer);
        foreach (JsonObject manifest in new[] { manifests.Current }.Concat(manifests.Inherited))
        {
            foreach (MinecraftLibraryToken library in MinecraftLibraryResolver.Resolve(
                new MinecraftLibraryResolutionRequest
                {
                    VersionJson = manifest,
                    MinecraftRootDirectory = root,
                    OperatingSystem = platform.OperatingSystem,
                    Is64BitArchitecture = platform.Is64BitArchitecture,
                    IsArm64Architecture = platform.IsArm64Architecture,
                    OperatingSystemVersion = platform.OperatingSystemVersion,
                }))
            {
                if (library.IsLocal || string.IsNullOrWhiteSpace(library.Url))
                {
                    continue;
                }

                libraries[library.LocalPath] = (library.Url, library.Sha1, library.Size);
            }
        }

        foreach ((string localPath, (string url, string? sha1, long size)) in libraries)
        {
            string[] sources = MinecraftDownloadSourcePlanner.GetLibrarySources(url, true);
            if (!sources.Contains(url, StringComparer.Ordinal))
            {
                sources = [.. sources, url];
            }

            AddIfMissing(missing, sources, localPath, size > 0 ? size : null, sha1);
        }

        // Assets are planned from the index document, so fetch the index first when missing,
        // then plan the objects against what exists on disk.
        string? indexDiskPath = indexPlan.LocalPath;
        if (indexDiskPath is null && indexPlan.IndexId is { Length: > 0 } indexId)
        {
            indexDiskPath = Path.Combine(root, "assets", "indexes", indexId + ".json");
        }

        if (indexDiskPath is not null)
        {
            await EnsureAssetIndexAsync(indexDiskPath, indexPlan, cancellationToken).ConfigureAwait(false);
            if (File.Exists(indexDiskPath))
            {
                JsonObject indexJson = await MinecraftVersionJsonReader.ReadAsync(indexDiskPath, cancellationToken)
                    .ConfigureAwait(false);
                IReadOnlyList<MinecraftAssetToken> assets = MinecraftAssetListResolver.GetAssetList(
                    new MinecraftAssetListRequest
                    {
                        IndexJson = indexJson,
                        MinecraftRootDirectory = root,
                        InstanceDirectory = instance.DirectoryPath,
                    });

                // Every object is a candidate; the shared verifier decides reuse by hash.
                foreach (MinecraftAssetToken asset in assets)
                {
                    AddIfMissing(missing,
                        MinecraftDownloadSourcePlanner.GetAssetSources(
                            MinecraftAssetListResolver.GetObjectUrl(asset.Hash), true),
                        asset.LocalPath,
                        asset.Size > 0 ? asset.Size : null,
                        asset.Hash);
                }
            }
        }

        if (missing.Count == 0)
        {
            _log?.Debug("Launch", "File completion found nothing missing; continuing.");
            return;
        }

        _log?.Info("Launch", $"File completion repairing {missing.Count} missing file(s).");
        int done = 0;
        foreach (PendingFile file in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await MinecraftFileVerifier.VerifyAsync(file.Expected, cancellationToken).ConfigureAwait(false))
            {
                done++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file.Destination)!);
            int filesBefore = done;
            DownloadTransferResult transfer = await TransferAsync(
                file.Sources, file.Destination, cancellationToken,
                progress: bytes => ReportProgress(progress, method, filesBefore, missing.Count, bytes))
                .ConfigureAwait(false);
            bool verified = transfer.Success
                && await MinecraftFileVerifier.VerifyAsync(file.Expected, cancellationToken).ConfigureAwait(false);
            if (!verified)
            {
                // Bursty mirror rate limits and truncated commits: delete the bad artifact
                // and retry once before failing the launch.
                if (transfer.Success)
                {
                    TryDelete(file.Destination);
                }

                await Task.Delay(FileRetryDelay, cancellationToken).ConfigureAwait(false);
                transfer = await TransferAsync(file.Sources, file.Destination, cancellationToken)
                    .ConfigureAwait(false);
                verified = transfer.Success
                    && await MinecraftFileVerifier.VerifyAsync(file.Expected, cancellationToken).ConfigureAwait(false);
            }

            if (!verified)
            {
                TryDelete(file.Destination);
                throw new InvalidOperationException(
                    $"补全文件失败：{Path.GetFileName(file.Destination)}（{(transfer.Errors.Count > 0 ? transfer.Errors[0].Message : (transfer.Success ? "校验未通过" : "未知错误"))}");
            }

            done++;
            ReportProgress(progress, method, done, missing.Count, null);
        }

        ReportProgress(progress, method, missing.Count, missing.Count, null);
    }

    internal static async Task<bool> HasVerifiedCorePatchAsync(MinecraftInstanceDescriptor instance, string clientPath, CancellationToken token)
    {
        string expected = instance.Metadata.CorePatchSha256;
        string ownJar = Path.Combine(instance.DirectoryPath, instance.Id + ".jar");
        if (expected.Length != 64 || !MinecraftLibraryService.PathComparer.Equals(Path.GetFullPath(clientPath), Path.GetFullPath(ownJar))
            || !File.Exists(clientPath)) return false;
        await using var stream = File.OpenRead(clientPath);
        string actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One coherent progress line for the whole repair: (files done + this file's byte
    /// fraction) over all missing files, mapped through ProgressAt into the complete_files
    /// band. ProgressAt divides by Total — publishing raw weights would clamp past 1.0 and
    /// flash 100% mid-repair.
    /// </summary>
    // Per-chunk callbacks arrive at line speed; one publish per chunk floods the render
    // thread on fast links. Reports step by at least this fraction of the repair.
    private const double ReportStep = 0.002d;
    private double _lastReportedFraction = -1d;

    private void ReportProgress(
        MinecraftLaunchProgressPublisher? progress,
        string method,
        int filesDone,
        int totalFiles,
        DownloadProgress? bytes)
    {
        if (progress is null)
        {
            return;
        }

        double intra = bytes is { } progress1 && progress1.TotalBytes > 0
            ? Math.Clamp(progress1.DownloadedBytes / (double)progress1.TotalBytes, 0d, 1d)
            : 0d;
        double fraction = totalFiles == 0
            ? 1d
            : Math.Clamp((filesDone + intra) / totalFiles, 0d, 1d);
        if (fraction - _lastReportedFraction < ReportStep && fraction < 1d)
        {
            return;
        }

        _lastReportedFraction = fraction;
        progress.Report(new MinecraftLaunchStageReport(
            MinecraftLaunchStages.CompleteFiles,
            MinecraftLaunchStages.ProgressAt(
                MinecraftLaunchStages.LoginWeight
                    + (MinecraftLaunchStages.CompleteFilesWeight * fraction)),
            Method: method,
            DownloadSpeed: bytes is { } transfer && transfer.BytesPerSecond > 0
                ? FormatSpeed(transfer.BytesPerSecond)
                : null));
    }

    private static string FormatSpeed(long bytesPerSecond)
    {
        double value = bytesPerSecond;
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        int unit = 0;
        while (value >= 1024d && unit < units.Length - 1)
        {
            value /= 1024d;
            unit++;
        }

        return string.Create(
            System.Globalization.CultureInfo.CurrentCulture,
            $"{value.ToString(unit == 0 ? "F0" : "F1", System.Globalization.CultureInfo.CurrentCulture)} {units[unit]}");
    }

    private async ValueTask EnsureAssetIndexAsync(
        string indexDiskPath,
        MinecraftAssetIndexDownloadPlan plan,
        CancellationToken cancellationToken)
    {
        if (File.Exists(indexDiskPath) || !plan.HasDownload)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(indexDiskPath)!);
        DownloadTransferResult transfer = await TransferAsync(
            MinecraftDownloadSourcePlanner.GetLauncherOrMetaSources(plan.Url!, true),
            indexDiskPath,
            cancellationToken).ConfigureAwait(false);
        if (!transfer.Success)
        {
            await Task.Delay(FileRetryDelay, cancellationToken).ConfigureAwait(false);
            transfer = await TransferAsync(
                MinecraftDownloadSourcePlanner.GetLauncherOrMetaSources(plan.Url!, true),
                indexDiskPath,
                cancellationToken).ConfigureAwait(false);
        }

        if (!transfer.Success)
        {
            throw new InvalidOperationException("补全文件失败：资源索引下载失败。");
        }
    }

    private Task<DownloadTransferResult> TransferAsync(
        IReadOnlyList<string> sources,
        string destination,
        CancellationToken cancellationToken,
        Action<DownloadProgress>? progress = null) =>
        _downloads.DownloadAsync(
            new DownloadRequest
            {
                Sources = sources,
                DestinationPath = destination,
                ConnectionFactory = _connectionFactory is { } factory
                    ? source => factory(source)
                    : source => new HttpConnectionAdapter(_http, source),
            },
            progress,
            cancellationToken);

    private sealed record PendingFile(string[] Sources, string Destination, long? Size, string? Sha1)
    {
        public MinecraftExpectedFile Expected => new(Destination, Size, Sha1);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Cleanup must not kill the launch; a leftover bad file fails the next
            // verification and is re-downloaded then.
        }
    }

    private static void AddIfMissing(
        List<PendingFile> missing, string[] sources, string destination, long? size, string? sha1)
    {
        for (int index = 0; index < missing.Count; index++)
        {
            if (MinecraftLibraryService.PathComparer.Equals(missing[index].Destination, destination))
            {
                missing[index] = new(sources, destination, size, sha1);
                return;
            }
        }

        missing.Add(new(sources, destination, size, sha1));
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Adapts one HttpClient GET to the download engine's connection port.</summary>
    private sealed class HttpConnectionAdapter(HttpClient client, string source) : IDownloadConnection
    {
        private HttpResponseMessage? _response;

        public async ValueTask<DownloadConnectionInfo> StartAsync(
            long beginOffset, CancellationToken cancellationToken = default)
        {
            HttpRequestMessage request = new(HttpMethod.Get, source);
            if (beginOffset > 0)
            {
                request.Headers.Range = new(beginOffset, null);
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
}
