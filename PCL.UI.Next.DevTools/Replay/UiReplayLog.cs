// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text;

namespace PCL.UI.Next.DevTools;

public enum UiReplayEntryKind : byte
{
    PlatformEvent = 1,
    StatePatch = 2,
    ClockTick = 3,
    Viewport = 4,
    ResourceReady = 5
}

public readonly record struct UiReplayEntry
{
    private UiReplayEntry(
        UiReplayEntryKind kind,
        UiPlatformEvent platformEvent,
        UiStatePatch statePatch,
        UiTimestamp timestamp,
        UiSize viewport,
        int resourceId,
        uint resourceGeneration)
    {
        Kind = kind;
        PlatformEvent = platformEvent;
        StatePatch = statePatch;
        Timestamp = timestamp;
        Viewport = viewport;
        ResourceId = resourceId;
        ResourceGeneration = resourceGeneration;
    }

    public UiReplayEntryKind Kind { get; }
    public UiPlatformEvent PlatformEvent { get; }
    public UiStatePatch StatePatch { get; }
    public UiTimestamp Timestamp { get; }
    public UiSize Viewport { get; }
    public int ResourceId { get; }
    public uint ResourceGeneration { get; }

    public static UiReplayEntry FromPlatformEvent(in UiPlatformEvent platformEvent) =>
        new(UiReplayEntryKind.PlatformEvent, platformEvent, default, default, default, 0, 0);

    public static UiReplayEntry FromStatePatch(in UiStatePatch patch) =>
        new(UiReplayEntryKind.StatePatch, default, patch, default, default, 0, 0);

    public static UiReplayEntry ClockTick(UiTimestamp timestamp) =>
        new(UiReplayEntryKind.ClockTick, default, default, timestamp, default, 0, 0);

    public static UiReplayEntry ViewportChanged(UiSize viewport) =>
        new(UiReplayEntryKind.Viewport, default, default, default, viewport, 0, 0);

    public static UiReplayEntry ResourceReady(int resourceId, uint generation) =>
        new(UiReplayEntryKind.ResourceReady, default, default, default, default, resourceId, generation);
}

/// <summary>Versioned immutable .uireplay payload.</summary>
public sealed class UiReplayLog
{
    private const uint Magic = 0x50524955; // UIRP in little-endian byte order.
    public const ushort CurrentVersion = 1;
    private const int MaximumEntryCount = 10_000_000;
    private readonly UiReplayEntry[] _entries;

    public UiReplayLog(ReadOnlySpan<UiReplayEntry> entries)
    {
        _entries = entries.ToArray();
    }

    public ushort Version => CurrentVersion;
    public ReadOnlyMemory<UiReplayEntry> Entries => _entries;

    public void Save(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("Replay destination must be writable.", nameof(destination));
        using BinaryWriter writer = new(destination, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(CurrentVersion);
        writer.Write((ushort)0);
        writer.Write(_entries.Length);
        for (int i = 0; i < _entries.Length; i++)
            WriteEntry(writer, in _entries[i]);
    }

    public static UiReplayLog Load(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("Replay source must be readable.", nameof(source));
        using BinaryReader reader = new(source, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException("The stream is not a PCL UI replay file.");
        ushort version = reader.ReadUInt16();
        _ = reader.ReadUInt16();
        if (version != CurrentVersion)
            throw new InvalidDataException("Unsupported UI replay version: " + version);
        int count = reader.ReadInt32();
        if (count < 0 || count > MaximumEntryCount)
            throw new InvalidDataException("Invalid UI replay entry count: " + count);
        UiReplayEntry[] entries = new UiReplayEntry[count];
        for (int i = 0; i < count; i++)
            entries[i] = ReadEntry(reader);
        return new UiReplayLog(entries);
    }

    private static void WriteEntry(BinaryWriter writer, in UiReplayEntry entry)
    {
        writer.Write((byte)entry.Kind);
        switch (entry.Kind)
        {
            case UiReplayEntryKind.PlatformEvent:
                WriteScope(writer, entry.PlatformEvent.Scope);
                writer.Write(entry.PlatformEvent.Kind);
                writer.Write(entry.PlatformEvent.Timestamp.Seconds);
                writer.Write(entry.PlatformEvent.Payload0);
                writer.Write(entry.PlatformEvent.Payload1);
                writer.Write(entry.PlatformEvent.Payload2);
                writer.Write(entry.PlatformEvent.Payload3);
                writer.Write(entry.PlatformEvent.Payload4);
                writer.Write(entry.PlatformEvent.InputRoot.Index);
                writer.Write(entry.PlatformEvent.InputRoot.Generation);
                break;
            case UiReplayEntryKind.StatePatch:
                WriteScope(writer, entry.StatePatch.Scope);
                writer.Write(entry.StatePatch.RequestGeneration);
                writer.Write(entry.StatePatch.PatchKind);
                writer.Write(entry.StatePatch.Payload0);
                writer.Write(entry.StatePatch.Payload1);
                writer.Write(entry.StatePatch.PayloadLong);
                break;
            case UiReplayEntryKind.ClockTick:
                writer.Write(entry.Timestamp.Seconds);
                break;
            case UiReplayEntryKind.Viewport:
                writer.Write(entry.Viewport.Width);
                writer.Write(entry.Viewport.Height);
                break;
            case UiReplayEntryKind.ResourceReady:
                writer.Write(entry.ResourceId);
                writer.Write(entry.ResourceGeneration);
                break;
            default:
                throw new InvalidDataException("Unknown UI replay entry kind: " + entry.Kind);
        }
    }

    private static UiReplayEntry ReadEntry(BinaryReader reader)
    {
        UiReplayEntryKind kind = (UiReplayEntryKind)reader.ReadByte();
        return kind switch
        {
            UiReplayEntryKind.PlatformEvent => ReadPlatformEvent(reader),
            UiReplayEntryKind.StatePatch => ReadStatePatch(reader),
            UiReplayEntryKind.ClockTick => UiReplayEntry.ClockTick(new UiTimestamp(reader.ReadDouble())),
            UiReplayEntryKind.Viewport => UiReplayEntry.ViewportChanged(new UiSize(reader.ReadSingle(), reader.ReadSingle())),
            UiReplayEntryKind.ResourceReady => UiReplayEntry.ResourceReady(reader.ReadInt32(), reader.ReadUInt32()),
            _ => throw new InvalidDataException("Unknown UI replay entry kind: " + kind)
        };
    }

    private static UiReplayEntry ReadPlatformEvent(BinaryReader reader)
    {
        UiScopeId scope = ReadScope(reader);
        uint kind = reader.ReadUInt32();
        UiTimestamp timestamp = new(reader.ReadDouble());
        int payload0 = reader.ReadInt32();
        int payload1 = reader.ReadInt32();
        int payload2 = reader.ReadInt32();
        int payload3 = reader.ReadInt32();
        int payload4 = reader.ReadInt32();
        UiInputRootId inputRoot = new(reader.ReadInt32(), reader.ReadUInt32());
        UiPlatformEvent platformEvent = new(
            scope,
            kind,
            timestamp,
            payload0,
            payload1,
            payload2,
            payload3,
            inputRoot,
            payload4);
        return UiReplayEntry.FromPlatformEvent(in platformEvent);
    }

    private static UiReplayEntry ReadStatePatch(BinaryReader reader)
    {
        UiStatePatch patch = new(
            ReadScope(reader),
            reader.ReadUInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt64());
        return UiReplayEntry.FromStatePatch(in patch);
    }

    private static void WriteScope(BinaryWriter writer, UiScopeId scope)
    {
        writer.Write(scope.Index);
        writer.Write(scope.Generation);
    }

    private static UiScopeId ReadScope(BinaryReader reader) =>
        new(reader.ReadInt32(), reader.ReadUInt32());
}

