// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace PCL.Desktop.Hosting.PluginSidecar;

internal enum PluginSidecarMessageType : ushort
{
    Request = 2,
    Response = 3,
    Progress = 4,
    Cancel = 5,
    Ping = 6
}

[Flags]
internal enum PluginSidecarFrameFlags : uint
{
    None = 0,
    Final = 1
}

internal readonly record struct PluginSidecarFrameHeader(
    PluginSidecarMessageType MessageType,
    PluginSidecarFrameFlags Flags,
    ulong RequestId,
    int PayloadLength);

/// <summary>
/// Protocol v4 framing: fixed 20-byte big-endian header plus an inline payload.
/// Payloads already stored on disk are represented by a path in the JSON control payload.
/// </summary>
internal static class PluginSidecarV4Framing
{
    public const int HeaderBytes = 20;
    public const int MaxInlinePayloadBytes = 1024 * 1024;

    public static bool TryReadFrame(
        ref ReadOnlySequence<byte> buffer,
        out PluginSidecarFrameHeader header,
        out ReadOnlySequence<byte> payload)
    {
        header = default;
        payload = default;
        if (buffer.Length < HeaderBytes)
            return false;

        Span<byte> headerBytes = stackalloc byte[HeaderBytes];
        buffer.Slice(0, HeaderBytes).CopyTo(headerBytes);

        uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(headerBytes);
        ushort protocolVersion = BinaryPrimitives.ReadUInt16BigEndian(headerBytes[4..]);
        ushort rawMessageType = BinaryPrimitives.ReadUInt16BigEndian(headerBytes[6..]);
        uint rawFlags = BinaryPrimitives.ReadUInt32BigEndian(headerBytes[8..]);
        ulong requestId = BinaryPrimitives.ReadUInt64BigEndian(headerBytes[12..]);

        if (protocolVersion != PluginSidecarProtocolVersions.Current)
            throw new InvalidDataException($"Unsupported sidecar frame protocol: {protocolVersion}.");
        if (payloadLength > MaxInlinePayloadBytes)
            throw new InvalidDataException($"Sidecar inline payload is too large: {payloadLength}.");

        long frameLength = HeaderBytes + payloadLength;
        if (buffer.Length < frameLength)
            return false;

        header = new PluginSidecarFrameHeader(
            (PluginSidecarMessageType)rawMessageType,
            (PluginSidecarFrameFlags)rawFlags,
            requestId,
            checked((int)payloadLength));
        payload = buffer.Slice(HeaderBytes, payloadLength);
        buffer = buffer.Slice(frameLength);
        return true;
    }

    public static void WriteJson<T>(
        PipeWriter writer,
        ArrayBufferWriter<byte> payloadBuffer,
        PluginSidecarMessageType messageType,
        PluginSidecarFrameFlags flags,
        ulong requestId,
        T value,
        JsonTypeInfo<T> typeInfo)
    {
        payloadBuffer.Clear();
        using (Utf8JsonWriter jsonWriter = new(payloadBuffer))
        {
            JsonSerializer.Serialize(jsonWriter, value, typeInfo);
        }

        WritePayload(writer, messageType, flags, requestId, payloadBuffer.WrittenSpan);
    }

    public static void WriteEmpty(
        PipeWriter writer,
        PluginSidecarMessageType messageType,
        PluginSidecarFrameFlags flags,
        ulong requestId) =>
        WritePayload(writer, messageType, flags, requestId, ReadOnlySpan<byte>.Empty);

    public static T? ReadJson<T>(ReadOnlySequence<byte> payload, JsonTypeInfo<T> typeInfo)
    {
        Utf8JsonReader reader = new(payload);
        return JsonSerializer.Deserialize(ref reader, typeInfo);
    }

    public static PluginSidecarProgress ReadProgress(ReadOnlySequence<byte> payload)
    {
        const int fixedBytes = 28;
        if (payload.Length < fixedBytes || payload.Length > MaxInlinePayloadBytes)
            throw new InvalidDataException($"Invalid sidecar progress payload length: {payload.Length}.");

        Span<byte> fixedPart = stackalloc byte[fixedBytes];
        payload.Slice(0, fixedBytes).CopyTo(fixedPart);
        long progressBits = BinaryPrimitives.ReadInt64BigEndian(fixedPart);
        int completedFiles = BinaryPrimitives.ReadInt32BigEndian(fixedPart[8..]);
        int totalFiles = BinaryPrimitives.ReadInt32BigEndian(fixedPart[12..]);
        long speed = BinaryPrimitives.ReadInt64BigEndian(fixedPart[16..]);
        ushort stageBytes = BinaryPrimitives.ReadUInt16BigEndian(fixedPart[24..]);
        ushort detailBytes = BinaryPrimitives.ReadUInt16BigEndian(fixedPart[26..]);

        if (payload.Length != fixedBytes + stageBytes + detailBytes)
            throw new InvalidDataException("Invalid sidecar progress string lengths.");

        ReadOnlySequence<byte> strings = payload.Slice(fixedBytes);
        string stage = DecodeUtf8(strings.Slice(0, stageBytes));
        string? detail = detailBytes == 0
            ? null
            : DecodeUtf8(strings.Slice(stageBytes, detailBytes));
        return new PluginSidecarProgress
        {
            Progress = BitConverter.Int64BitsToDouble(progressBits),
            CompletedFiles = completedFiles,
            TotalFiles = totalFiles,
            SpeedBytesPerSecond = speed,
            Stage = stage,
            Detail = detail
        };
    }

    private static void WritePayload(
        PipeWriter writer,
        PluginSidecarMessageType messageType,
        PluginSidecarFrameFlags flags,
        ulong requestId,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxInlinePayloadBytes)
        {
            throw new InvalidOperationException(
                $"Sidecar inline payload is too large: {payload.Length}. Pass a file path instead.");
        }

        Span<byte> destination = writer.GetSpan(HeaderBytes + payload.Length);
        BinaryPrimitives.WriteUInt32BigEndian(destination, checked((uint)payload.Length));
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], PluginSidecarProtocolVersions.Current);
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], (ushort)messageType);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], (uint)flags);
        BinaryPrimitives.WriteUInt64BigEndian(destination[12..], requestId);
        payload.CopyTo(destination[HeaderBytes..]);
        writer.Advance(HeaderBytes + payload.Length);
    }

    private static string DecodeUtf8(ReadOnlySequence<byte> bytes)
    {
        if (bytes.IsSingleSegment)
            return Encoding.UTF8.GetString(bytes.FirstSpan);

        byte[] rented = ArrayPool<byte>.Shared.Rent(checked((int)bytes.Length));
        try
        {
            bytes.CopyTo(rented);
            return Encoding.UTF8.GetString(rented, 0, checked((int)bytes.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
