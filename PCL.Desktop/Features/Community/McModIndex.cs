// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace PCL.Desktop.Features.Community;

public sealed record McModIndexEntry(int WikiId, string ChineseName, string? CurseForgeSlug, string? ModrinthSlug)
{
    public IEnumerable<string> Slugs
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CurseForgeSlug))
                yield return CurseForgeSlug;
            if (!string.IsNullOrWhiteSpace(ModrinthSlug) &&
                !string.Equals(ModrinthSlug, CurseForgeSlug, StringComparison.OrdinalIgnoreCase))
            {
                yield return ModrinthSlug;
            }
        }
    }
}

public sealed class McModIndex
{
    private const string ResourceName = "PCL.Desktop.Assets.mcmod.buf";
    private static readonly Lazy<McModIndex> CurrentIndex = new(LoadEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly IReadOnlyList<McModIndexEntry> _entries;
    private readonly Dictionary<string, McModIndexEntry> _curseForge;
    private readonly Dictionary<string, McModIndexEntry> _modrinth;

    internal McModIndex(IReadOnlyList<McModIndexEntry> entries)
    {
        _entries = entries;
        _curseForge = BuildSlugMap(entries, static entry => entry.CurseForgeSlug);
        _modrinth = BuildSlugMap(entries, static entry => entry.ModrinthSlug);
    }

    public static McModIndex Current => CurrentIndex.Value;

    public int Count => _entries.Count;

    public McModIndexEntry? FindBySlug(CommunityResourceSource source, string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return null;
        Dictionary<string, McModIndexEntry> map = source == CommunityResourceSource.CurseForge
            ? _curseForge
            : _modrinth;
        return map.GetValueOrDefault(slug.Trim());
    }

    public IReadOnlyList<McModIndexEntry> SearchChinese(string query, int limit = 12)
    {
        string normalized = query.Trim();
        return FindChineseCandidates(normalized)
            .OrderBy(entry => entry.ChineseName.Equals(normalized, StringComparison.OrdinalIgnoreCase) ? 0 :
                              entry.ChineseName.StartsWith(normalized, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(static entry => entry.ChineseName.Length)
            .Take(Math.Max(1, limit))
            .ToArray();
    }

    internal IEnumerable<McModIndexEntry> FindChineseCandidates(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || !query.Any(IsCjk))
            return [];
        string normalized = query.Trim();
        return _entries.Where(entry =>
            !string.IsNullOrWhiteSpace(entry.ChineseName) &&
            entry.ChineseName.Contains(normalized, StringComparison.OrdinalIgnoreCase));
    }

    public CommunityResourceEntry Decorate(CommunityResourceEntry entry)
    {
        McModIndexEntry? match = FindBySlug(entry.Source, entry.Slug);
        if (match is null)
            return entry;
        return entry with
        {
            WikiId = match.WikiId > 0 ? match.WikiId : null,
            ChineseName = string.IsNullOrWhiteSpace(match.ChineseName) ? null : match.ChineseName,
            OriginalTitle = entry.Title
        };
    }

    internal static McModIndex Load(Stream compressed)
    {
        using GZipStream gzip = new(compressed, CompressionMode.Decompress, leaveOpen: true);
        using MemoryStream payload = new();
        gzip.CopyTo(payload);
        return new McModIndex(ParseTopLevel(payload.ToArray()));
    }

    private static McModIndex LoadEmbedded()
    {
        Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                        ?? throw new InvalidDataException("内置 MC 百科索引资源缺失。");
        using (stream)
            return Load(stream);
    }

    private static List<McModIndexEntry> ParseTopLevel(ReadOnlySpan<byte> data)
    {
        List<McModIndexEntry> entries = [];
        int offset = 0;
        while (offset < data.Length)
        {
            ulong tag = ReadVarint(data, ref offset);
            int wireType = (int)(tag & 7);
            if ((tag >> 3) == 1 && wireType == 2)
            {
                ReadOnlySpan<byte> message = ReadBytes(data, ref offset);
                McModIndexEntry? entry = ParseEntry(message);
                if (entry is not null)
                    entries.Add(entry);
            }
            else
            {
                Skip(data, ref offset, wireType);
            }
        }
        return entries;
    }

    private static McModIndexEntry? ParseEntry(ReadOnlySpan<byte> data)
    {
        int wikiId = 0;
        string chinese = string.Empty;
        string? curseForge = null;
        string? modrinth = null;
        int offset = 0;
        while (offset < data.Length)
        {
            ulong tag = ReadVarint(data, ref offset);
            int field = (int)(tag >> 3);
            int wireType = (int)(tag & 7);
            if (field == 1 && wireType == 0)
                wikiId = checked((int)ReadVarint(data, ref offset));
            else if (wireType == 2 && field is >= 2 and <= 4)
            {
                string value = Encoding.UTF8.GetString(ReadBytes(data, ref offset));
                if (field == 2) chinese = value;
                else if (field == 3) curseForge = NullIfEmpty(value);
                else modrinth = NullIfEmpty(value);
            }
            else
                Skip(data, ref offset, wireType);
        }
        return wikiId == 0 && string.IsNullOrWhiteSpace(curseForge) && string.IsNullOrWhiteSpace(modrinth)
            ? null
            : new McModIndexEntry(wikiId, chinese, curseForge, modrinth);
    }

    private static Dictionary<string, McModIndexEntry> BuildSlugMap(
        IEnumerable<McModIndexEntry> entries,
        Func<McModIndexEntry, string?> selector)
    {
        Dictionary<string, McModIndexEntry> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (McModIndexEntry entry in entries)
        {
            string? slug = selector(entry);
            if (!string.IsNullOrWhiteSpace(slug))
                result.TryAdd(slug, entry);
        }
        return result;
    }

    private static ReadOnlySpan<byte> ReadBytes(ReadOnlySpan<byte> data, ref int offset)
    {
        int length = checked((int)ReadVarint(data, ref offset));
        if (length < 0 || offset > data.Length - length)
            throw new InvalidDataException("MC 百科索引包含无效长度。");
        ReadOnlySpan<byte> value = data.Slice(offset, length);
        offset += length;
        return value;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong value = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (offset >= data.Length)
                throw new EndOfStreamException();
            byte current = data[offset++];
            value |= (ulong)(current & 0x7f) << shift;
            if ((current & 0x80) == 0)
                return value;
        }
        throw new InvalidDataException("MC 百科索引包含无效 varint。");
    }

    private static void Skip(ReadOnlySpan<byte> data, ref int offset, int wireType)
    {
        switch (wireType)
        {
            case 0: ReadVarint(data, ref offset); break;
            case 1: offset = checked(offset + 8); break;
            case 2: _ = ReadBytes(data, ref offset); break;
            case 5: offset = checked(offset + 4); break;
            default: throw new InvalidDataException("MC 百科索引包含不支持的 wire type。");
        }
        if (offset > data.Length)
            throw new EndOfStreamException();
    }

    private static bool IsCjk(char value) => value is >= '\u3400' and <= '\u9fff';

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
