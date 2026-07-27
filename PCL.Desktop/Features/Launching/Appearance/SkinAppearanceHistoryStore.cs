// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCL.Desktop.Features.Launching.Appearance;

public enum AppearanceTextureKind
{
    Skin,
    Cape
}

public sealed record SkinAppearanceHistoryEntry(
    string ProfileKey,
    string DisplayName,
    AppearanceTextureKind Kind,
    string Address,
    bool IsSlim,
    DateTimeOffset LastUsedUtc);

/// <summary>
/// Small local MRU used by the appearance page. It stores texture locations only;
/// account tokens and profile secrets never enter this file.
/// </summary>
public sealed class SkinAppearanceHistoryStore
{
    private const int MaximumEntries = 80;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _path;

    public SkinAppearanceHistoryStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async Task<IReadOnlyList<SkinAppearanceHistoryEntry>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
                return [];

            await using FileStream stream = new(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            List<SkinAppearanceHistoryEntry>? entries = await JsonSerializer
                .DeserializeAsync<List<SkinAppearanceHistoryEntry>>(
                    stream,
                    SkinAppearanceHistoryJsonContext.Default.ListSkinAppearanceHistoryEntry,
                    cancellationToken)
                .ConfigureAwait(false);
            return Normalize(entries ?? []);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException)
        {
            return [];
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task RecordAsync(
        IEnumerable<SkinAppearanceHistoryEntry> newEntries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newEntries);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<SkinAppearanceHistoryEntry> existing = [];
            if (File.Exists(_path))
            {
                try
                {
                    await using FileStream readStream = new(
                        _path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        16 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    existing = await JsonSerializer
                        .DeserializeAsync<List<SkinAppearanceHistoryEntry>>(
                            readStream,
                            SkinAppearanceHistoryJsonContext.Default.ListSkinAppearanceHistoryEntry,
                            cancellationToken)
                        .ConfigureAwait(false) ?? [];
                }
                catch (Exception exception) when (
                    exception is IOException or
                    UnauthorizedAccessException or
                    JsonException)
                {
                    existing = [];
                }
            }

            IReadOnlyList<SkinAppearanceHistoryEntry> normalized = Normalize(
                newEntries.Concat(existing));
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string temporaryPath = _path + "." + Environment.ProcessId + "." +
                                   Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (FileStream writeStream = new(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 16 * 1024,
                                 FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(
                            writeStream,
                            normalized.ToList(),
                            SkinAppearanceHistoryJsonContext.Default.ListSkinAppearanceHistoryEntry,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await writeStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, _path, overwrite: true);
                temporaryPath = string.Empty;
            }
            finally
            {
                if (!string.IsNullOrEmpty(temporaryPath) && File.Exists(temporaryPath))
                {
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static IReadOnlyList<SkinAppearanceHistoryEntry> Normalize(
        IEnumerable<SkinAppearanceHistoryEntry> entries)
    {
        return entries
            .Where(static entry =>
                !string.IsNullOrWhiteSpace(entry.Address) &&
                !string.IsNullOrWhiteSpace(entry.ProfileKey))
            .OrderByDescending(static entry => entry.LastUsedUtc)
            .DistinctBy(
                static entry => (entry.Kind, entry.Address.Trim()),
                AppearanceHistoryKeyComparer.Instance)
            .Take(MaximumEntries)
            .ToArray();
    }

    private sealed class AppearanceHistoryKeyComparer :
        IEqualityComparer<(AppearanceTextureKind Kind, string Address)>
    {
        public static AppearanceHistoryKeyComparer Instance { get; } = new();

        public bool Equals(
            (AppearanceTextureKind Kind, string Address) left,
            (AppearanceTextureKind Kind, string Address) right) =>
            left.Kind == right.Kind &&
            string.Equals(left.Address, right.Address, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((AppearanceTextureKind Kind, string Address) value) =>
            HashCode.Combine(
                value.Kind,
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Address));
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<SkinAppearanceHistoryEntry>))]
internal sealed partial class SkinAppearanceHistoryJsonContext : JsonSerializerContext;
