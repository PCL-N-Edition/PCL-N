// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using System.Text.Json.Serialization;
using PCL.Domain.Minecraft.Java;

namespace PCL.Desktop.Features.Launching;

internal sealed record JavaRuntimeCatalogLoadResult(
    IReadOnlyList<JavaRuntimeCandidate> Candidates,
    bool FromCache);

internal sealed class JavaRuntimeCatalogCache : IDisposable
{
    private const int SchemaVersion = 1;
    private static readonly TimeSpan Freshness = TimeSpan.FromHours(6);
    private static readonly TimeSpan EmptyCacheFreshness = TimeSpan.FromMinutes(10);

    private readonly string _cachePath;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private JavaRuntimeCacheDocument? _memoryDocument;
    private bool _readAttempted;

    public JavaRuntimeCatalogCache(string cachePath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
        _cachePath = Path.GetFullPath(cachePath);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<JavaRuntimeCatalogLoadResult> GetOrScanAsync(
        string fingerprint,
        bool forceRefresh,
        Func<CancellationToken, Task<IReadOnlyList<JavaRuntimeCandidate>>> scan,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentNullException.ThrowIfNull(scan);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            JavaRuntimeCacheDocument? document = await TryReadAsync(cancellationToken).ConfigureAwait(false);
            List<JavaRuntimeCandidate>? cached = TryRestore(document, fingerprint, requireFresh: true);
            if (!forceRefresh && cached is not null)
                return new JavaRuntimeCatalogLoadResult(cached, FromCache: true);

            try
            {
                IReadOnlyList<JavaRuntimeCandidate> scanned = await scan(cancellationToken).ConfigureAwait(false);
                await TryWriteAsync(fingerprint, scanned, cancellationToken).ConfigureAwait(false);
                return new JavaRuntimeCatalogLoadResult(scanned, FromCache: false);
            }
            catch (Exception ex) when (
                !cancellationToken.IsCancellationRequested &&
                ex is IOException or UnauthorizedAccessException or InvalidOperationException or
                    global::System.Security.SecurityException)
            {
                // A temporarily inaccessible registry key or drive must not make Java
                // selection fail when the last verified cache is still usable.
                cached = TryRestore(document, fingerprint, requireFresh: false);
                if (cached is not null)
                    return new JavaRuntimeCatalogLoadResult(cached, FromCache: true);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JavaRuntimeCacheDocument?> TryReadAsync(CancellationToken cancellationToken)
    {
        if (_readAttempted)
            return _memoryDocument;
        _readAttempted = true;

        if (!File.Exists(_cachePath))
            return null;

        try
        {
            await using FileStream stream = new(
                _cachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            _memoryDocument = await JsonSerializer.DeserializeAsync(
                    stream,
                    JavaRuntimeCacheJsonContext.Default.JavaRuntimeCacheDocument,
                    cancellationToken)
                .ConfigureAwait(false);
            return _memoryDocument;
        }
        catch (Exception ex) when (
            !cancellationToken.IsCancellationRequested &&
            ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or
                global::System.Security.SecurityException)
        {
            TryDelete(_cachePath);
            return null;
        }
    }

    private List<JavaRuntimeCandidate>? TryRestore(
        JavaRuntimeCacheDocument? document,
        string fingerprint,
        bool requireFresh)
    {
        if (document is null ||
            document.SchemaVersion != SchemaVersion ||
            !string.Equals(document.Fingerprint, fingerprint, StringComparison.Ordinal) ||
            document.Candidates is null)
        {
            return null;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        TimeSpan age = now - document.GeneratedAtUtc;
        TimeSpan freshness = document.Candidates.Count == 0 ? EmptyCacheFreshness : Freshness;
        if (age < TimeSpan.FromMinutes(-5) || (requireFresh && age > freshness))
            return null;

        List<JavaRuntimeCandidate> candidates = new(document.Candidates.Count);
        foreach (JavaRuntimeCacheCandidate snapshot in document.Candidates)
        {
            JavaRuntimeCandidate? candidate = TryRestoreCandidate(snapshot);
            if (candidate is null)
                return null;
            candidates.Add(candidate);
        }

        return candidates;
    }

    private static JavaRuntimeCandidate? TryRestoreCandidate(JavaRuntimeCacheCandidate snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.JavaHome) ||
            string.IsNullOrWhiteSpace(snapshot.JavaExecutablePath) ||
            !Version.TryParse(snapshot.Version, out Version? version) ||
            !Enum.IsDefined(snapshot.Brand) ||
            !Enum.IsDefined(snapshot.Architecture) ||
            !Enum.IsDefined(snapshot.Source))
        {
            return null;
        }

        try
        {
            FileInfo executable = new(snapshot.JavaExecutablePath);
            if (!Matches(executable, snapshot.Executable))
                return null;

            string releasePath = Path.Combine(snapshot.JavaHome, "release");
            FileInfo release = new(releasePath);
            if (snapshot.ReleaseFile is null ? release.Exists : !Matches(release, snapshot.ReleaseFile))
                return null;

            string? windowed = !string.IsNullOrWhiteSpace(snapshot.WindowedJavaExecutablePath) &&
                               File.Exists(snapshot.WindowedJavaExecutablePath)
                ? Path.GetFullPath(snapshot.WindowedJavaExecutablePath)
                : null;
            JavaInstallation installation = new(
                Path.GetFullPath(snapshot.JavaHome),
                executable.FullName,
                windowed,
                version,
                snapshot.Brand,
                snapshot.Architecture,
                snapshot.Is64Bit,
                snapshot.IsJre);
            return new JavaRuntimeCandidate(
                installation,
                IsEnabled: true,
                IsAvailable: snapshot.IsAvailable,
                Source: snapshot.Source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private async Task TryWriteAsync(
        string fingerprint,
        IReadOnlyList<JavaRuntimeCandidate> candidates,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_cachePath);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        string temporaryPath = _cachePath + "." + Environment.ProcessId + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            JavaRuntimeCacheDocument document = new(
                SchemaVersion,
                fingerprint,
                _timeProvider.GetUtcNow(),
                candidates.Select(CreateSnapshot).ToArray());
            // Keep the verified result in memory even if the cache directory is
            // temporarily read-only; disk persistence remains best effort.
            _memoryDocument = document;
            _readAttempted = true;
            await using (FileStream stream = new(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        document,
                        JavaRuntimeCacheJsonContext.Default.JavaRuntimeCacheDocument,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (
            !cancellationToken.IsCancellationRequested &&
            ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or
                global::System.Security.SecurityException)
        {
            TryDelete(temporaryPath);
        }
    }

    private static JavaRuntimeCacheCandidate CreateSnapshot(JavaRuntimeCandidate candidate)
    {
        JavaInstallation java = candidate.Installation;
        return new JavaRuntimeCacheCandidate(
            java.JavaHome,
            java.JavaExecutablePath,
            java.WindowedJavaExecutablePath,
            java.Version.ToString(),
            java.Brand,
            java.Architecture,
            java.Is64Bit,
            java.IsJre,
            candidate.IsAvailable,
            candidate.Source,
            CreateSignature(java.JavaExecutablePath),
            CreateSignature(Path.Combine(java.JavaHome, "release")));
    }

    private static JavaRuntimeCacheFileSignature? CreateSignature(string path)
    {
        FileInfo file = new(path);
        return file.Exists
            ? new JavaRuntimeCacheFileSignature(file.Length, file.LastWriteTimeUtc.Ticks)
            : null;
    }

    private static bool Matches(FileInfo file, JavaRuntimeCacheFileSignature? signature) =>
        signature is not null &&
        file.Exists &&
        file.Length == signature.Length &&
        file.LastWriteTimeUtc.Ticks == signature.LastWriteUtcTicks;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or global::System.Security.SecurityException)
        {
            // Cache cleanup is best effort.
        }
    }

    public void Dispose() => _gate.Dispose();
}

internal sealed record JavaRuntimeCacheDocument(
    int SchemaVersion,
    string Fingerprint,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<JavaRuntimeCacheCandidate>? Candidates);

internal sealed record JavaRuntimeCacheCandidate(
    string JavaHome,
    string JavaExecutablePath,
    string? WindowedJavaExecutablePath,
    string Version,
    JavaBrand Brand,
    JavaArchitecture Architecture,
    bool Is64Bit,
    bool IsJre,
    bool IsAvailable,
    JavaSource Source,
    JavaRuntimeCacheFileSignature? Executable,
    JavaRuntimeCacheFileSignature? ReleaseFile);

internal sealed record JavaRuntimeCacheFileSignature(long Length, long LastWriteUtcTicks);

[JsonSerializable(typeof(JavaRuntimeCacheDocument))]
internal sealed partial class JavaRuntimeCacheJsonContext : JsonSerializerContext;
