// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace PCL.Desktop.Hosting.PluginSidecar;

/// <summary>Length-prefixed (BE u32) UTF-8 JSON frames over a bidirectional stream.</summary>
internal static class PluginSidecarFraming
{
    public const int MaxFrameBytes = 4 * 1024 * 1024;

    public static async Task WriteAsync<T>(
        Stream stream,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        if (payload.Length > MaxFrameBytes)
            throw new InvalidOperationException($"Sidecar frame too large: {payload.Length}");

        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T?> ReadAsync<T>(
        Stream stream,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0 || length > MaxFrameBytes)
            throw new InvalidOperationException($"Invalid sidecar frame length: {length}");

        byte[] payload = new byte[length];
        await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(payload, typeInfo);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Sidecar pipe closed while reading frame.");
            offset += read;
        }
    }

    public static string DescribePayload(ReadOnlySpan<byte> utf8) =>
        Encoding.UTF8.GetString(utf8);
}
