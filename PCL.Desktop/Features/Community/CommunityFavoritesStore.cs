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

public sealed record CommunityFavoriteFolder(
    string Id,
    string Name,
    IReadOnlyList<CommunityFavoriteEntry> Items,
    IReadOnlyDictionary<string, string> Notes);

public sealed record CommunityFavoritesExportSnapshot(
    string NativeJson,
    string CeFoldersJson);

public sealed class CommunityFavoritesStore
{
    public const string DefaultFolderId = "pcln-default";
    public const string DefaultFolderName = "默认收藏夹";
    internal const string ImportedEntryDescription = "从 PCL CE 收藏夹导入";

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
        _state = Stores.GetOrAdd(_path, static normalizedPath =>
        {
            LoadResult loaded = Load(normalizedPath);
            if (loaded.ShouldSave)
            {
                try
                {
                    Save(normalizedPath, loaded.Document);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Keep migrated data available in memory. The next mutation retries the atomic save.
                }
            }

            return new StoreState(loaded.Document);
        });
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

    public IReadOnlyList<CommunityFavoriteFolder> Folders
    {
        get
        {
            lock (_state.Gate)
                return _state.Document.Folders.Select(CreateSnapshot).ToArray();
        }
    }

    public string SelectedFolderId
    {
        get
        {
            lock (_state.Gate)
                return _state.Document.SelectedFolderId;
        }
    }

    public CommunityFavoriteFolder SelectedFolder
    {
        get
        {
            lock (_state.Gate)
                return CreateSnapshot(GetSelectedFolder(_state.Document));
        }
    }

    public IReadOnlyList<CommunityFavoriteEntry> Items
    {
        get
        {
            lock (_state.Gate)
                return SortItems(GetSelectedFolder(_state.Document).Items);
        }
    }

    public bool Contains(CommunityResourceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_state.Gate)
        {
            return _state.Document.Folders.Any(folder =>
                folder.Items.Any(item => IsSameProject(item.Entry, entry)));
        }
    }

    public bool Contains(CommunityResourceEntry entry, string folderId)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);
        lock (_state.Gate)
        {
            CommunityFavoriteFolderData? folder = FindFolder(_state.Document, folderId);
            return folder is not null && folder.Items.Any(item => IsSameProject(item.Entry, entry));
        }
    }

    public bool Toggle(
        CommunityResourceEntry entry,
        CommunityResourceCategory category) =>
        Toggle(entry, category, folderId: null);

    public bool Toggle(
        CommunityResourceEntry entry,
        CommunityResourceCategory category,
        string? folderId)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return ApplyMutation(document =>
        {
            CommunityFavoriteFolderData folder = ResolveFolder(document, folderId);
            int index = folder.Items.FindIndex(item => IsSameProject(item.Entry, entry));
            if (index >= 0)
            {
                folder.Items.RemoveAt(index);
                return new MutationResult<bool>(true, false);
            }

            folder.Items.Add(new CommunityFavoriteEntry(entry, category, DateTimeOffset.UtcNow));
            return new MutationResult<bool>(true, true);
        });
    }

    public bool SelectFolder(string folderId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);
        return ApplyMutation(document =>
        {
            CommunityFavoriteFolderData folder = ResolveFolder(document, folderId);
            if (string.Equals(document.SelectedFolderId, folder.Id, StringComparison.OrdinalIgnoreCase))
                return new MutationResult<bool>(false, false);
            document.SelectedFolderId = folder.Id;
            return new MutationResult<bool>(true, true);
        });
    }

    public CommunityFavoriteFolder CreateFolder(string name)
    {
        string normalizedName = NormalizeNewFolderName(name);
        return ApplyMutation(document =>
        {
            EnsureUniqueFolderName(document, normalizedName);
            CommunityFavoriteFolderData folder = new()
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = normalizedName
            };
            document.Folders.Add(folder);
            document.SelectedFolderId = folder.Id;
            return new MutationResult<CommunityFavoriteFolder>(true, CreateSnapshot(folder));
        });
    }

    public bool RenameFolder(string folderId, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);
        string normalizedName = NormalizeNewFolderName(name);
        return ApplyMutation(document =>
        {
            CommunityFavoriteFolderData folder = ResolveFolder(document, folderId);
            if (string.Equals(folder.Name, normalizedName, StringComparison.Ordinal))
                return new MutationResult<bool>(false, false);
            EnsureUniqueFolderName(document, normalizedName, folder.Id);
            folder.Name = normalizedName;
            return new MutationResult<bool>(true, true);
        });
    }

    public bool DeleteFolder(string folderId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);
        return ApplyMutation(document =>
        {
            if (document.Folders.Count == 1)
                throw new InvalidOperationException("至少需要保留一个收藏夹。");

            int index = document.Folders.FindIndex(folder =>
                string.Equals(folder.Id, folderId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                throw new KeyNotFoundException($"找不到收藏夹：{folderId}");

            document.Folders.RemoveAt(index);
            if (string.Equals(document.SelectedFolderId, folderId, StringComparison.OrdinalIgnoreCase))
            {
                int nextIndex = Math.Min(index, document.Folders.Count - 1);
                document.SelectedFolderId = document.Folders[nextIndex].Id;
            }

            return new MutationResult<bool>(true, true);
        });
    }

    public string ExportShareJson(string? folderId = null)
    {
        lock (_state.Gate)
        {
            CommunityFavoriteFolderData folder = ResolveFolder(_state.Document, folderId);
            List<string> ids = folder.Items
                .OrderByDescending(static item => item.AddedAt)
                .Select(static item => GetShareProjectId(item.Entry))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return JsonSerializer.Serialize(ids, CommunityFavoritesJsonContext.Default.ListString);
        }
    }

    public int ImportShareJson(string json, string? folderId = null)
    {
        List<string> ids = ParseShareIds(json);
        return ApplyMutation(document =>
        {
            CommunityFavoriteFolderData folder = ResolveFolder(document, folderId);
            int added = AddSharedIds(document, folder, ids);
            return new MutationResult<int>(added > 0, added);
        });
    }

    public CommunityFavoriteFolder CreateFolderFromShare(string name, string json)
    {
        string normalizedName = NormalizeNewFolderName(name);
        List<string> ids = ParseShareIds(json);
        return ApplyMutation(document =>
        {
            EnsureUniqueFolderName(document, normalizedName);
            CommunityFavoriteFolderData folder = new()
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = normalizedName
            };
            AddSharedIds(document, folder, ids);
            document.Folders.Add(folder);
            document.SelectedFolderId = folder.Id;
            return new MutationResult<CommunityFavoriteFolder>(true, CreateSnapshot(folder));
        });
    }

    public string ExportJson()
    {
        lock (_state.Gate)
            return SerializeNativeDocument(_state.Document);
    }

    public string ExportCeFoldersJson()
    {
        lock (_state.Gate)
            return SerializeCeFolders(_state.Document);
    }

    public CommunityFavoritesExportSnapshot ExportSnapshot()
    {
        lock (_state.Gate)
        {
            return new CommunityFavoritesExportSnapshot(
                SerializeNativeDocument(_state.Document),
                SerializeCeFolders(_state.Document));
        }
    }

    public void ReplaceFromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        (CommunityFavoritesDocument replacement, _) = ParseNativeDocument(json);
        NormalizeDocument(replacement);
        ReplaceDocument(replacement);
    }

    public void ReplaceFromCeFoldersJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        List<CommunityFavoriteCeFolderData> imported = JsonSerializer.Deserialize(
                json,
                CommunityFavoritesJsonContext.Default.ListCommunityFavoriteCeFolderData)
            ?? throw new InvalidDataException("云端收藏夹数据为空。");

        ApplyMutation(document =>
        {
            CommunityFavoritesDocument replacement = new();
            HashSet<string> usedIds = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> usedNames = new(StringComparer.OrdinalIgnoreCase);
            foreach (CommunityFavoriteCeFolderData source in imported)
            {
                string id = string.IsNullOrWhiteSpace(source.Id) || !usedIds.Add(source.Id.Trim())
                    ? CreateUniqueId(usedIds)
                    : source.Id.Trim();
                string name = CreateUniqueImportedName(source.Name, usedNames);
                CommunityFavoriteFolderData folder = new()
                {
                    Id = id,
                    Name = name,
                    Notes = (source.Notes ?? [])
                        .Where(static note => !string.IsNullOrWhiteSpace(note.Value))
                        .ToDictionary(
                            static note => note.Key,
                            static note => note.Value,
                            StringComparer.OrdinalIgnoreCase)
                };
                AddSharedIds(document, folder, NormalizeSharedIds(source.Favs));
                replacement.Folders.Add(folder);
            }

            if (replacement.Folders.Count == 0)
                replacement.Folders.Add(CreateDefaultFolder());
            replacement.SelectedFolderId = replacement.Folders[0].Id;
            _state.Document = replacement;
            return new MutationResult<bool>(true, true);
        });
    }

    internal int ApplyResolvedMetadata(
        string folderId,
        IReadOnlyList<CommunityResourceEntry> resolvedEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);
        ArgumentNullException.ThrowIfNull(resolvedEntries);
        Dictionary<string, CommunityResourceEntry> resolvedById = resolvedEntries
            .GroupBy(static entry => CreateProviderKey(entry.Source, entry.ProjectId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        return ApplyMutation(document =>
        {
            CommunityFavoriteFolderData folder = ResolveFolder(document, folderId);
            int updated = 0;
            for (int index = 0; index < folder.Items.Count; index++)
            {
                CommunityFavoriteEntry favorite = folder.Items[index];
                if (!IsImportedPlaceholder(favorite.Entry) ||
                    !resolvedById.TryGetValue(
                        CreateProviderKey(favorite.Entry.Source, favorite.Entry.ProjectId),
                        out CommunityResourceEntry? resolved))
                {
                    continue;
                }

                folder.Items[index] = favorite with
                {
                    Entry = resolved,
                    Category = GetCategory(resolved.ProjectType, favorite.Category)
                };
                updated++;
            }

            return new MutationResult<int>(updated > 0, updated);
        });
    }

    internal static bool IsImportedPlaceholder(CommunityResourceEntry entry)
    {
        if (!string.Equals(entry.Title, entry.ProjectId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(entry.Slug, entry.ProjectId, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(entry.IconUrl) ||
            entry.Downloads != 0)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(entry.Description) ||
               string.Equals(entry.Description, ImportedEntryDescription, StringComparison.Ordinal) ||
               string.Equals(entry.Description, "从旧版收藏夹迁移", StringComparison.Ordinal);
    }

    private static string SerializeNativeDocument(CommunityFavoritesDocument document) =>
        JsonSerializer.Serialize(
            document,
            CommunityFavoritesJsonContext.Default.CommunityFavoritesDocument);

    private static string SerializeCeFolders(CommunityFavoritesDocument document)
    {
        List<CommunityFavoriteCeFolderData> folders = document.Folders
            .Select(static folder => new CommunityFavoriteCeFolderData
            {
                Name = folder.Name,
                Id = folder.Id,
                Favs = folder.Items
                    .OrderByDescending(static item => item.AddedAt)
                    .Select(static item => GetShareProjectId(item.Entry))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Notes = new Dictionary<string, string>(folder.Notes, StringComparer.OrdinalIgnoreCase)
            })
            .ToList();
        return JsonSerializer.Serialize(
            folders,
            CommunityFavoritesJsonContext.Default.ListCommunityFavoriteCeFolderData);
    }

    private TResult ApplyMutation<TResult>(
        Func<CommunityFavoritesDocument, MutationResult<TResult>> mutation)
    {
        EventHandler? changed;
        TResult result;
        lock (_state.Gate)
        {
            CommunityFavoritesDocument previous = CloneDocument(_state.Document);
            MutationResult<TResult> outcome = mutation(_state.Document);
            result = outcome.Value;
            if (!outcome.Changed)
                return result;

            try
            {
                Save(_path, _state.Document);
            }
            catch
            {
                _state.Document = previous;
                throw;
            }

            changed = _state.Changed;
        }

        changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private void ReplaceDocument(CommunityFavoritesDocument replacement)
    {
        EventHandler? changed;
        lock (_state.Gate)
        {
            CommunityFavoritesDocument previous = _state.Document;
            _state.Document = replacement;
            try
            {
                Save(_path, _state.Document);
            }
            catch
            {
                _state.Document = previous;
                throw;
            }

            changed = _state.Changed;
        }

        changed?.Invoke(this, EventArgs.Empty);
    }

    private static List<string> ParseShareIds(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            List<string>? ids = JsonSerializer.Deserialize(
                json,
                CommunityFavoritesJsonContext.Default.ListString);
            List<string> normalized = NormalizeSharedIds(ids);
            return normalized.Count > 0
                ? normalized
                : throw new InvalidDataException("分享内容中没有项目 ID。");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("分享内容不是有效的 CE 收藏夹 JSON ID 数组。", ex);
        }
    }

    private static List<string> NormalizeSharedIds(IEnumerable<string?>? ids) =>
        ids?
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

    private static int AddSharedIds(
        CommunityFavoritesDocument metadataSource,
        CommunityFavoriteFolderData target,
        IReadOnlyList<string> ids)
    {
        int added = 0;
        DateTimeOffset addedAt = DateTimeOffset.UtcNow;
        foreach (string id in ids)
        {
            if (target.Items.Any(item => MatchesProjectId(item.Entry, id)))
                continue;

            CommunityFavoriteEntry? known = FindKnownEntry(metadataSource, id);
            target.Items.Add(known is null
                ? CreateImportedEntry(id, addedAt.AddTicks(-added))
                : known with { AddedAt = addedAt.AddTicks(-added) });
            added++;
        }

        return added;
    }

    private static CommunityFavoriteEntry? FindKnownEntry(
        CommunityFavoritesDocument document,
        string projectId) =>
        document.Folders
            .SelectMany(static folder => folder.Items)
            .FirstOrDefault(item => MatchesProjectId(item.Entry, projectId));

    private static CommunityFavoriteEntry CreateImportedEntry(string projectId, DateTimeOffset addedAt)
    {
        CommunityResourceSource source = long.TryParse(projectId, out _)
            ? CommunityResourceSource.CurseForge
            : CommunityResourceSource.Modrinth;
        CommunityResourceEntry entry = new(
            projectId,
            projectId,
            projectId,
            ImportedEntryDescription,
            "mod",
            null,
            0,
            null)
        {
            Source = source
        };
        return new CommunityFavoriteEntry(entry, CommunityResourceCategory.Mod, addedAt);
    }

    private static CommunityResourceCategory GetCategory(
        string projectType,
        CommunityResourceCategory fallback) =>
        projectType.ToLowerInvariant() switch
        {
            "mod" => CommunityResourceCategory.Mod,
            "modpack" => CommunityResourceCategory.Modpack,
            "datapack" or "data-pack" => CommunityResourceCategory.DataPack,
            "resourcepack" or "resource-pack" => CommunityResourceCategory.ResourcePack,
            "shader" => CommunityResourceCategory.Shader,
            "world" => CommunityResourceCategory.World,
            _ => fallback
        };

    private static string CreateProviderKey(CommunityResourceSource source, string projectId) =>
        $"{(int)source}:{projectId}";

    private static bool MatchesProjectId(CommunityResourceEntry entry, string projectId)
    {
        if (string.Equals(entry.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
            return true;
        return MatchesReference(CommunityResourceSource.Modrinth) ||
               MatchesReference(CommunityResourceSource.CurseForge);

        bool MatchesReference(CommunityResourceSource source) =>
            string.Equals(
                entry.GetProjectReference(source)?.ProjectId,
                projectId,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string GetShareProjectId(CommunityResourceEntry entry)
    {
        CommunityResourceSource preferred = entry.Source == CommunityResourceSource.CurseForge
            ? CommunityResourceSource.CurseForge
            : CommunityResourceSource.Modrinth;
        return entry.GetProjectReference(preferred)?.ProjectId ?? entry.ProjectId;
    }

    private static CommunityFavoriteFolderData ResolveFolder(
        CommunityFavoritesDocument document,
        string? folderId)
    {
        string id = string.IsNullOrWhiteSpace(folderId) ? document.SelectedFolderId : folderId;
        return FindFolder(document, id)
            ?? throw new KeyNotFoundException($"找不到收藏夹：{id}");
    }

    private static CommunityFavoriteFolderData GetSelectedFolder(CommunityFavoritesDocument document) =>
        ResolveFolder(document, document.SelectedFolderId);

    private static CommunityFavoriteFolderData? FindFolder(
        CommunityFavoritesDocument document,
        string folderId) =>
        document.Folders.FirstOrDefault(folder =>
            string.Equals(folder.Id, folderId, StringComparison.OrdinalIgnoreCase));

    private static void EnsureUniqueFolderName(
        CommunityFavoritesDocument document,
        string name,
        string? exceptFolderId = null)
    {
        if (document.Folders.Any(folder =>
                !string.Equals(folder.Id, exceptFolderId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"已存在名为“{name}”的收藏夹。");
        }
    }

    private static string NormalizeNewFolderName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string normalized = name.Trim();
        return normalized.Length <= 64
            ? normalized
            : throw new ArgumentOutOfRangeException(nameof(name), "收藏夹名称不能超过 64 个字符。");
    }

    private static CommunityFavoriteFolder CreateSnapshot(CommunityFavoriteFolderData folder) =>
        new(
            folder.Id,
            folder.Name,
            SortItems(folder.Items),
            new Dictionary<string, string>(folder.Notes, StringComparer.OrdinalIgnoreCase));

    private static CommunityFavoriteEntry[] SortItems(
        IEnumerable<CommunityFavoriteEntry> items) =>
        items.OrderByDescending(static item => item.AddedAt).ToArray();

    private static bool IsSameProject(CommunityResourceEntry left, CommunityResourceEntry right)
    {
        if (left.Source == right.Source &&
            string.Equals(left.ProjectId, right.ProjectId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return HasSameReference(CommunityResourceSource.Modrinth) ||
               HasSameReference(CommunityResourceSource.CurseForge);

        bool HasSameReference(CommunityResourceSource source)
        {
            CommunityResourceProjectReference? leftReference = left.GetProjectReference(source);
            CommunityResourceProjectReference? rightReference = right.GetProjectReference(source);
            return leftReference is not null && rightReference is not null &&
                   string.Equals(
                    leftReference.ProjectId,
                    rightReference.ProjectId,
                    StringComparison.OrdinalIgnoreCase);
        }
    }

    private static LoadResult Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new LoadResult(CreateEmptyDocument(), false);
            string json = File.ReadAllText(path);
            (CommunityFavoritesDocument document, bool migrated) = ParseNativeDocument(json);
            NormalizeDocument(document);
            return new LoadResult(document, migrated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return new LoadResult(CreateEmptyDocument(), false);
        }
    }

    private static (CommunityFavoritesDocument Document, bool Migrated) ParseNativeDocument(string json)
    {
        using JsonDocument parsed = JsonDocument.Parse(json);
        if (parsed.RootElement.ValueKind == JsonValueKind.Object)
        {
            CommunityFavoritesDocument document = JsonSerializer.Deserialize(
                    json,
                    CommunityFavoritesJsonContext.Default.CommunityFavoritesDocument)
                ?? throw new InvalidDataException("收藏夹数据为空。");
            return (document, false);
        }

        if (parsed.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("收藏夹数据格式无效。");

        if (parsed.RootElement.GetArrayLength() > 0 &&
            parsed.RootElement[0].ValueKind == JsonValueKind.String)
        {
            List<string> ids = ParseShareIds(json);
            CommunityFavoritesDocument shared = CreateEmptyDocument();
            AddSharedIds(shared, shared.Folders[0], ids);
            return (shared, true);
        }

        List<CommunityFavoriteEntry> legacyItems = JsonSerializer.Deserialize(
                json,
                CommunityFavoritesJsonContext.Default.ListCommunityFavoriteEntry)
            ?? [];
        CommunityFavoritesDocument migrated = CreateEmptyDocument();
        migrated.Folders[0].Items = legacyItems;
        return (migrated, true);
    }

    private static void NormalizeDocument(CommunityFavoritesDocument document)
    {
        document.Folders ??= [];
        document.Folders = document.Folders.Where(static folder => folder is not null).ToList();
        if (document.Folders.Count == 0)
            document.Folders.Add(CreateDefaultFolder());

        HashSet<string> usedIds = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> usedNames = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < document.Folders.Count; index++)
        {
            CommunityFavoriteFolderData folder = document.Folders[index];
            folder.Id = string.IsNullOrWhiteSpace(folder.Id) || !usedIds.Add(folder.Id.Trim())
                ? CreateUniqueId(usedIds)
                : folder.Id.Trim();
            folder.Name = CreateUniqueImportedName(folder.Name, usedNames);
            folder.Items ??= [];
            folder.Notes ??= [];
            folder.Notes = folder.Notes
                .Where(static note => !string.IsNullOrWhiteSpace(note.Value))
                .ToDictionary(
                    static note => note.Key,
                    static note => note.Value,
                    StringComparer.OrdinalIgnoreCase);
            List<CommunityFavoriteEntry> normalizedItems = [];
            foreach (CommunityFavoriteEntry item in folder.Items.Where(static item => item is not null))
            {
                if (!normalizedItems.Any(existing => IsSameProject(existing.Entry, item.Entry)))
                    normalizedItems.Add(item);
            }
            folder.Items = normalizedItems;
        }

        if (FindFolder(document, document.SelectedFolderId) is null)
            document.SelectedFolderId = document.Folders[0].Id;
    }

    private static string CreateUniqueImportedName(string? requested, HashSet<string> usedNames)
    {
        string baseName = string.IsNullOrWhiteSpace(requested) ? DefaultFolderName : requested.Trim();
        if (baseName.Length > 64)
            baseName = baseName[..64];
        if (usedNames.Add(baseName))
            return baseName;

        for (int suffix = 2; ; suffix++)
        {
            string suffixText = $" ({suffix})";
            int maximumBaseLength = Math.Max(1, 64 - suffixText.Length);
            string candidate = baseName[..Math.Min(baseName.Length, maximumBaseLength)] + suffixText;
            if (usedNames.Add(candidate))
                return candidate;
        }
    }

    private static string CreateUniqueId(HashSet<string> usedIds)
    {
        string id;
        do
        {
            id = Guid.NewGuid().ToString("N");
        }
        while (!usedIds.Add(id));
        return id;
    }

    private static CommunityFavoritesDocument CreateEmptyDocument()
    {
        CommunityFavoriteFolderData folder = CreateDefaultFolder();
        return new CommunityFavoritesDocument
        {
            SelectedFolderId = folder.Id,
            Folders = [folder]
        };
    }

    private static CommunityFavoriteFolderData CreateDefaultFolder() => new()
    {
        Id = DefaultFolderId,
        Name = DefaultFolderName
    };

    private static CommunityFavoritesDocument CloneDocument(CommunityFavoritesDocument source) => new()
    {
        SelectedFolderId = source.SelectedFolderId,
        Folders = source.Folders.Select(static folder => new CommunityFavoriteFolderData
        {
            Id = folder.Id,
            Name = folder.Name,
            Items = [.. folder.Items],
            Notes = new Dictionary<string, string>(folder.Notes, StringComparer.OrdinalIgnoreCase)
        }).ToList()
    };

    private static void Save(string path, CommunityFavoritesDocument document)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        string temporary = Path.Combine(
            directory ?? AppContext.BaseDirectory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
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
                    document,
                    CommunityFavoritesJsonContext.Default.CommunityFavoritesDocument);
                stream.Flush(flushToDisk: true);
            }

            ReplaceWithRetry(temporary, path);
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
        return Path.Combine(PCL.Desktop.Paths.LauncherPathLayout.ResolveDataDirectory(), "community-favorites.json");
    }

    private static void ReplaceWithRetry(string temporaryPath, string targetPath)
    {
        Exception? lastException = null;
        for (int attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                File.Move(temporaryPath, targetPath, overwrite: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastException = exception;
                if (attempt < 6)
                    Thread.Sleep(20 * attempt);
            }
        }

        throw new IOException($"Unable to replace community favorites file '{targetPath}'.", lastException);
    }

    private sealed class StoreState(CommunityFavoritesDocument document)
    {
        public object Gate { get; } = new();

        public CommunityFavoritesDocument Document { get; set; } = document;

        public EventHandler? Changed { get; set; }
    }

    private readonly record struct MutationResult<TResult>(bool Changed, TResult Value);

    private readonly record struct LoadResult(CommunityFavoritesDocument Document, bool ShouldSave);
}

internal sealed class CommunityFavoritesDocument
{
    public string SelectedFolderId { get; set; } = string.Empty;

    public List<CommunityFavoriteFolderData> Folders { get; set; } = [];
}

internal sealed class CommunityFavoriteFolderData
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<CommunityFavoriteEntry> Items { get; set; } = [];

    public Dictionary<string, string> Notes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class CommunityFavoriteCeFolderData
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("Favs")]
    public List<string> Favs { get; set; } = [];

    [JsonPropertyName("Notes")]
    public Dictionary<string, string> Notes { get; set; } = [];
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(CommunityFavoritesDocument))]
[JsonSerializable(typeof(List<CommunityFavoriteEntry>))]
[JsonSerializable(typeof(List<CommunityFavoriteCeFolderData>))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class CommunityFavoritesJsonContext : JsonSerializerContext;
