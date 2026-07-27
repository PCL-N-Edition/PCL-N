// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Core.Logging;

namespace PCL.Desktop.Features.Community;

/// <summary>
/// Launcher-owned online resource services. These are core Minecraft resource
/// capabilities and must remain available as host-built-in services.
/// </summary>
internal static class CommunityOnlineProviderRegistry
{
    private static readonly HttpClient TranslationClient = new()
    {
        Timeout = TimeSpan.FromSeconds(35)
    };

    public static (ICommunityResourceCatalog Modrinth, ICommunityResourceCatalog CurseForge) CreateCatalogs() =>
        (new ModrinthCommunityResourceCatalog(), new CurseForgeCommunityResourceCatalog());

    public static ICommunityTranslationService CreateTranslationService() =>
        new McimTranslationService(TranslationClient);

    public static ICommunityArtifactDownloader CreateArtifactDownloader() =>
        new LauncherCommunityArtifactDownloader();

    private sealed class LauncherCommunityArtifactDownloader : ICommunityArtifactDownloader
    {
        public async Task DownloadAsync(
            IReadOnlyList<string> candidateUrls,
            string targetPath,
            Action<long, long?> reportProgress,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(candidateUrls);
            ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
            ArgumentNullException.ThrowIfNull(reportProgress);

            using HttpClient client = new() { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-N-Desktop/1.0");
            Exception? lastError = null;
            foreach (string candidateUrl in candidateUrls.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.Exists(targetPath))
                        File.Delete(targetPath);
                    using HttpResponseMessage response = await client.GetAsync(
                            candidateUrl,
                            HttpCompletionOption.ResponseHeadersRead,
                            cancellationToken)
                        .ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    long? total = response.Content.Headers.ContentLength;
                    await using Stream network = await response.Content
                        .ReadAsStreamAsync(cancellationToken)
                        .ConfigureAwait(false);
                    await using FileStream output = new(
                        targetPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        64 * 1024,
                        useAsync: true);
                    byte[] buffer = new byte[64 * 1024];
                    long written = 0;
                    int read;
                    while ((read = await network.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        written += read;
                        reportProgress(written, total);
                    }
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException)
                {
                    lastError = exception;
                    string host = Uri.TryCreate(candidateUrl, UriKind.Absolute, out Uri? uri)
                        ? uri.Host
                        : "(invalid)";
                    PortableLog.Warn(
                        exception,
                        "CommunityDownload",
                        $"下载候选失败，将尝试下一来源：{host}。");
                }
            }

            throw lastError ?? new HttpRequestException("所有下载候选均失败。");
        }
    }
}
