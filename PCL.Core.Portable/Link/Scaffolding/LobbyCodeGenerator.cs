// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.
//
// The lobby-code wire format is compatible with the Terracotta/Scaffolding
// implementation used by PCL Community Edition 2.15.0.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PCL.Core.Link.Scaffolding.Client.Models;

namespace PCL.Core.Link.Scaffolding;

public static class LobbyCodeGenerator
{
    private const string Chars = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string FullCodePrefix = "U/";
    private const string NetworkNamePrefix = "scaffolding-mc-";
    private const int BaseValue = 34;
    private const int DataLength = 16;
    private const int PayloadLength = 19;
    private const int CodeLength = 21;
    private static readonly UInt128 EncodingMaxValue = CalculatePower(BaseValue, DataLength);
    private static readonly Dictionary<char, byte> CharToValueMap = CreateCharacterMap();

    public static LobbyInfo Generate()
    {
        UInt128 randomValue = GetSecureRandomUInt128();
        UInt128 valueInRange = randomValue % EncodingMaxValue;
        UInt128 validValue = valueInRange - valueInRange % 7;
        return Encode(validValue);
    }

    public static bool TryParse(string? input, [NotNullWhen(true)] out LobbyInfo? roomInfo)
    {
        roomInfo = null;
        if (string.IsNullOrWhiteSpace(input) ||
            !input.StartsWith(FullCodePrefix, StringComparison.Ordinal) ||
            input.Length != CodeLength)
        {
            return false;
        }

        Span<byte> values = stackalloc byte[DataLength];
        int valueIndex = 0;
        ReadOnlySpan<char> payload = input.AsSpan(FullCodePrefix.Length);
        for (int index = 0; index < payload.Length; index++)
        {
            char character = payload[index];
            if (character == '-')
            {
                if (index is not (4 or 9 or 14))
                    return false;
                continue;
            }

            if (valueIndex >= DataLength ||
                !CharToValueMap.TryGetValue(char.ToUpperInvariant(character), out byte characterValue))
            {
                return false;
            }

            values[valueIndex++] = characterValue;
        }

        if (valueIndex != DataLength)
            return false;

        UInt128 value = 0;
        for (int index = DataLength - 1; index >= 0; index--)
            value = value * BaseValue + values[index];

        if (value % 7 != 0)
            return false;

        ReadOnlySpan<char> networkNamePayload = payload[..9];
        ReadOnlySpan<char> networkSecretPayload = payload[10..];
        roomInfo = new LobbyInfo(
            string.Concat(FullCodePrefix, payload).ToUpperInvariant(),
            string.Concat(NetworkNamePrefix, networkNamePayload),
            networkSecretPayload.ToString());
        return true;
    }

    private static LobbyInfo Encode(UInt128 value)
    {
        string codePayload = string.Create(PayloadLength, value, static (span, currentValue) =>
        {
            Span<char> characters = stackalloc char[DataLength];
            for (int index = 0; index < DataLength; index++)
            {
                characters[index] = Chars[(int)(currentValue % BaseValue)];
                currentValue /= BaseValue;
            }

            characters[..4].CopyTo(span[..4]);
            span[4] = '-';
            characters[4..8].CopyTo(span[5..9]);
            span[9] = '-';
            characters[8..12].CopyTo(span[10..14]);
            span[14] = '-';
            characters[12..16].CopyTo(span[15..]);
        });

        return new LobbyInfo(
            string.Concat(FullCodePrefix, codePayload),
            string.Concat(NetworkNamePrefix, codePayload.AsSpan(0, 9)),
            codePayload.AsSpan(10).ToString());
    }

    private static UInt128 GetSecureRandomUInt128()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        ulong lower = MemoryMarshal.Read<ulong>(bytes);
        ulong upper = MemoryMarshal.Read<ulong>(bytes[8..]);
        return new UInt128(upper, lower);
    }

    private static UInt128 CalculatePower(uint baseValue, int exponent)
    {
        UInt128 result = 1;
        for (int index = 0; index < exponent; index++)
            result *= baseValue;
        return result;
    }

    private static Dictionary<char, byte> CreateCharacterMap()
    {
        Dictionary<char, byte> result = new(36);
        for (byte index = 0; index < Chars.Length; index++)
            result[Chars[index]] = index;
        result['I'] = 1;
        result['O'] = 0;
        return result;
    }
}
