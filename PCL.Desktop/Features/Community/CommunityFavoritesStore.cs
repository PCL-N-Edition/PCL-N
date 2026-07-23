// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCL.Platform.Paths;

namespace PCL.Desktop.Features.Community;

public sealed record CommunityFavoriteEntry(
    CommunityResourceEntry Entry,
    CommunityResourceCategory Category,
    DateTimeOffset AddedAt);

public sealed class CommunityFavoritesStore
{
    private static readonly ConcurrentDictionary<string, StoreState> Stores = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _path;
    private readonly StoreState _state;

    public CommunityFavoritesStore()
        : this(CreateDefaultPath())
    {
    }

    public CommunityFavoritesStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _state = Stores.GetOrAdd(_path, static normalizedPath => new StoreState(Load(normalizedPath)));
    }

    public event EventHandler? Changed
    {
        add
        {
            lock (_state.Gate)
                _state.Changed += value;
        }
        remove
        {
            lock (_state.Gate)
                _state.Changed -= value;
        }
    }

    public IReadOnlyList<CommunityFavoriteEntry> Items
    {
        get
        {
            lock (_state.Gate)
                return _state.Items.OrderByDescending(static item => item.AddedAt).ToArray();
        }
    }

    public bool Contains(CommunityResourceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_state.Gate)
            return _state.Items.Any(item => IsSameProject(item.Entry, entry));
    }

    public bool Toggle(CommunityResourceEntry entry, CommunityResourceCategory category)
    {
        ArgumentNullException.ThrowIfNull(entry);
        bool added;
        EventHandler? changed;
        lock (_state.Gate)
        {
            List<CommunityFavoriteEntry> previous = [.. _state.Items];
            int index = _state.Items.FindIndex(item => IsSameProject(item.Entry, entry));
            if (index >= 0)
            {
                _state.Items.RemoveAt(index);
                added = false;
            }
            else
            {
                _state.Items.Add(new CommunityFavoriteEntry(entry, category, DateTimeOffset.UtcNow));
                added = true;
            }

            try
            {
                Save(_path, _state.Items);
            }
            catch
            {
                _state.Items = previous;
                throw;
            }

            changed = _state.Changed;
        }

        changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    private static bool IsSameProject(CommunityResourceEntry left, CommunityResourceEntry right) =>
        left.Source == right.Source &&
        string.Equals(left.ProjectId, right.ProjectId, StringComparison.OrdinalIgnoreCase);

    private static List<CommunityFavoriteEntry> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, CommunityFavoritesJsonContext.Default.ListCommunityFavoriteEntry) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void Save(string path, IReadOnlyList<CommunityFavoriteEntry> items)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        string temporary = Path.Combine(
            directory ?? AppContext.BaseDirectory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        List<CommunityFavoriteEntry> serializableItems = [.. items];
        try
        {
            using (FileStream stream = new(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16 * 1024,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(
                    stream,
                    serializableItems,
                    CommunityFavoritesJsonContext.Default.ListCommunityFavoriteEntry);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            temporary = string.Empty;
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Preserve the original save failure.
                }
            }
        }
    }

    private static string CreateDefaultPath()
    {
        DefaultPlatformPathProvider paths = new();
        return Path.Combine(paths.ApplicationDataDirectory, "PCL-N", "community-favorites.json");
    }

    private sealed class StoreState(List<CommunityFavoriteEntry> items)
    {
        public object Gate { get; } = new();

        public List<CommunityFavoriteEntry> Items { get; set; } = items;

        public EventHandler? Changed { get; set; }
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(List<CommunityFavoriteEntry>))]
internal sealed partial class CommunityFavoritesJsonContext : JsonSerializerContext;
