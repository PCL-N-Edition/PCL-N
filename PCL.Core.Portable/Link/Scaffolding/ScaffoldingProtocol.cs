// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.
//
// Wire-compatible implementation of the Scaffolding protocol used by
// PCL Community Edition 2.15.0. All integer lengths are network byte order.

using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using PCL.Core.Link.Scaffolding.Client.Models;

namespace PCL.Core.Link.Scaffolding;

public readonly record struct ScaffoldingRequestFrame(string RequestType, ReadOnlyMemory<byte> Body);

public readonly record struct ScaffoldingResponseFrame(byte Status, ReadOnlyMemory<byte> Body);

public sealed class ScaffoldingRequestException(byte status, string? serverMessage = null)
    : IOException(serverMessage is null
        ? $"Scaffolding request failed with status {status}."
        : $"Scaffolding request failed with status {status}: {serverMessage}")
{
    public byte Status { get; } = status;
}

public static class ScaffoldingProtocol
{
    public const int MaximumTypeLength = 128;
    public const int MaximumBodyLength = 65_536;

    public static readonly string[] SupportedRequests =
    [
        "c:ping",
        "c:protocols",
        "c:server_port",
        "c:player_ping",
        "c:player_profiles_list"
    ];

    public static byte[] EncodeRequest(string requestType, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestType);
        int typeLength = Encoding.ASCII.GetByteCount(requestType);
        if (typeLength is <= 0 or > MaximumTypeLength)
            throw new ArgumentOutOfRangeException(nameof(requestType));
        if (body.Length > MaximumBodyLength)
            throw new ArgumentOutOfRangeException(nameof(body));

        byte[] packet = GC.AllocateUninitializedArray<byte>(1 + typeLength + 4 + body.Length);
        packet[0] = (byte)typeLength;
        Encoding.ASCII.GetBytes(requestType, packet.AsSpan(1, typeLength));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1 + typeLength, 4), (uint)body.Length);
        body.CopyTo(packet.AsSpan(1 + typeLength + 4));
        return packet;
    }

    public static byte[] EncodeResponse(byte status, ReadOnlySpan<byte> body)
    {
        if (body.Length > MaximumBodyLength)
            throw new ArgumentOutOfRangeException(nameof(body));
        byte[] packet = GC.AllocateUninitializedArray<byte>(5 + body.Length);
        packet[0] = status;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1, 4), (uint)body.Length);
        body.CopyTo(packet.AsSpan(5));
        return packet;
    }

    public static async ValueTask<ScaffoldingRequestFrame?> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        byte[] typeLengthBuffer = new byte[1];
        int firstRead = await stream.ReadAsync(typeLengthBuffer, cancellationToken).ConfigureAwait(false);
        if (firstRead == 0)
            return null;

        int typeLength = typeLengthBuffer[0];
        if (typeLength is <= 0 or > MaximumTypeLength)
            throw new InvalidDataException($"Invalid Scaffolding request type length: {typeLength}.");

        byte[] header = new byte[typeLength + 4];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        string requestType = Encoding.UTF8.GetString(header, 0, typeLength);
        uint bodyLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(typeLength, 4));
        if (bodyLength > MaximumBodyLength)
            throw new InvalidDataException($"Scaffolding request body is too large: {bodyLength}.");

        byte[] body = new byte[bodyLength];
        await ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false);
        return new ScaffoldingRequestFrame(requestType, body);
    }

    public static async ValueTask<ScaffoldingResponseFrame> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[5];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        byte status = header[0];
        uint bodyLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1, 4));
        if (bodyLength > MaximumBodyLength)
            throw new InvalidDataException($"Scaffolding response body is too large: {bodyLength}.");

        byte[] body = new byte[bodyLength];
        await ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false);
        if (status != 0)
        {
            string? message = status == byte.MaxValue ? Encoding.UTF8.GetString(body) : null;
            throw new ScaffoldingRequestException(status, message);
        }

        return new ScaffoldingResponseFrame(status, body);
    }

    public static byte[] SerializeProfile(PlayerProfile profile) =>
        JsonSerializer.SerializeToUtf8Bytes(profile, ScaffoldingJsonContext.Default.PlayerProfile);

    public static PlayerProfile? DeserializeProfile(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize(json, ScaffoldingJsonContext.Default.PlayerProfile);

    public static byte[] SerializeProfiles(IReadOnlyList<PlayerProfile> profiles) =>
        JsonSerializer.SerializeToUtf8Bytes(profiles.ToArray(), ScaffoldingJsonContext.Default.PlayerProfileArray);

    public static IReadOnlyList<PlayerProfile> DeserializeProfiles(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize(json, ScaffoldingJsonContext.Default.PlayerProfileArray) ?? [];

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Scaffolding connection closed before a complete frame arrived.");
            offset += read;
        }
    }
}
