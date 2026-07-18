// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Text.Json;

namespace PCL.Application.Settings;

public sealed class LauncherSettingsStore : IDisposable
{
    private const int ReplaceAttemptCount = 5;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AccessLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim _accessLock;

    public LauncherSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        SettingsPath = Path.GetFullPath(settingsPath);
        _accessLock = AccessLocks.GetOrAdd(SettingsPath, static _ => new SemaphoreSlim(1, 1));
    }

    public string SettingsPath { get; }

    public async ValueTask<LauncherSettingsLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _accessLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(SettingsPath))
                return new(new LauncherSettings(), false, null);

            try
            {
                await using FileStream stream = new(
                    SettingsPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 16 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                LauncherSettings? settings = await JsonSerializer.DeserializeAsync(
                        stream,
                        LauncherSettingsJsonContext.Default.LauncherSettings,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (settings is null)
                    throw new InvalidDataException("The launcher settings file is empty.");
                if (settings.SchemaVersion is <= 0 or > LauncherSettings.CurrentSchemaVersion)
                {
                    throw new InvalidDataException(
                        $"Unsupported launcher settings schema: {settings.SchemaVersion}.");
                }

                return new(settings.NormalizeOptionDictionaries(), false, null);
            }
            catch (Exception exception)
                when (exception is JsonException or InvalidDataException)
            {
                string backupPath = SettingsPath + ".invalid";
                File.Copy(SettingsPath, backupPath, overwrite: true);
                return new(new LauncherSettings(), true, backupPath);
            }
        }
        finally
        {
            _accessLock.Release();
        }
    }

    public async ValueTask SaveAsync(
        LauncherSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SchemaVersion != LauncherSettings.CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                settings.SchemaVersion,
                "Only the current launcher settings schema can be saved.");
        }

        await _accessLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            string directory = Path.GetDirectoryName(SettingsPath)
                ?? throw new InvalidOperationException(
                    "The launcher settings path has no parent directory.");
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(SettingsPath)}.{Guid.NewGuid():N}.tmp");

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
                        settings,
                        LauncherSettingsJsonContext.Default.LauncherSettings,
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
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // Preserve the original save exception. A later save or OS cleanup can
                    // remove an externally locked temporary file.
                }
            }
            _accessLock.Release();
        }
    }

    private async Task ReplaceWithRetryAsync(string temporaryPath, CancellationToken cancellationToken)
    {
        IOException? lastException = null;
        for (int attempt = 1; attempt <= ReplaceAttemptCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporaryPath, SettingsPath, overwrite: true);
                return;
            }
            catch (IOException exception) when (attempt < ReplaceAttemptCount)
            {
                lastException = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                lastException = exception;
            }
        }

        throw new IOException(
            $"Unable to replace launcher settings file '{SettingsPath}' after {ReplaceAttemptCount} attempts.",
            lastException);
    }

    // Locks are shared by every store instance for the same normalized path and live for
    // the process lifetime. Disposing one short-lived store must not dispose the shared lock.
    public void Dispose()
    {
    }
}
