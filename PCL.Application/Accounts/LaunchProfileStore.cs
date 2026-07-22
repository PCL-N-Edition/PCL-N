// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Text.Json;

namespace PCL.Application.Accounts;

public sealed class LaunchProfileStore : IDisposable
{
    private const int ReplaceAttemptCount = 6;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AccessLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim _accessLock;

    public LaunchProfileStore(string profilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        ProfilePath = Path.GetFullPath(profilePath);
        _accessLock = AccessLocks.GetOrAdd(ProfilePath, static _ => new SemaphoreSlim(1, 1));
    }

    public string ProfilePath { get; }

    public async ValueTask<LaunchProfileLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _accessLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(ProfilePath))
                return new(new LaunchProfileSet(), false, null);

            try
            {
                await using FileStream stream = new(
                    ProfilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 16 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                LaunchProfileSet? profiles = await JsonSerializer.DeserializeAsync(
                        stream,
                        LaunchProfileJsonContext.Default.LaunchProfileSet,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (profiles is null)
                    throw new InvalidDataException("The launch profile file is empty.");
                if (profiles.SchemaVersion is <= 0 or > LaunchProfileSet.CurrentSchemaVersion)
                {
                    throw new InvalidDataException(
                        $"Unsupported launch profile schema: {profiles.SchemaVersion}.");
                }

                return new(profiles, false, null);
            }
            catch (Exception exception)
                when (exception is JsonException or InvalidDataException)
            {
                string backupPath = ProfilePath + ".invalid";
                File.Copy(ProfilePath, backupPath, overwrite: true);
                return new(new LaunchProfileSet(), true, backupPath);
            }
        }
        finally
        {
            _accessLock.Release();
        }
    }

    public async ValueTask SaveAsync(
        LaunchProfileSet profiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.SchemaVersion != LaunchProfileSet.CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(profiles),
                profiles.SchemaVersion,
                "Only the current launch profile schema can be saved.");
        }

        await _accessLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            string directory = Path.GetDirectoryName(ProfilePath)
                ?? throw new InvalidOperationException(
                    "The launch profile path has no parent directory.");
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(ProfilePath)}.{Guid.NewGuid():N}.tmp");

            await using (FileStream stream = new(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        profiles,
                        LaunchProfileJsonContext.Default.LaunchProfileSet,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await ReplaceWithRetryAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null)
                TryDeleteTemporaryFile(temporaryPath);
            _accessLock.Release();
        }
    }

    private async Task ReplaceWithRetryAsync(
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (int attempt = 1; attempt <= ReplaceAttemptCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporaryPath, ProfilePath, overwrite: true);
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                lastException = exception;
                if (attempt < ReplaceAttemptCount)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        throw new IOException(
            $"Unable to replace launch profile file '{ProfilePath}' after {ReplaceAttemptCount} attempts.",
            lastException);
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preserve the original save exception. A later save or OS cleanup can remove an
            // externally locked temporary file.
        }
    }

    // Locks are shared by every store instance for the same normalized path and live for the
    // process lifetime. Disposing one short-lived store must not dispose the shared lock.
    public void Dispose()
    {
    }
}
