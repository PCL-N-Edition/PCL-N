// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Application.Updates;

/// <summary>
/// Managed VCDIFF (RFC 3284) decoder for protocol v2 block deltas.
/// Secondary compression and custom code tables are rejected; callers must
/// treat any failure as non-fatal and fall back to the full gzip block.
/// </summary>
internal static class LauncherUpdateVcdiff
{
    public const string Algorithm = "vcdiff-rfc3284";

    private const byte Magic0 = 0xD6;
    private const byte Magic1 = 0xC3;
    private const byte Magic2 = 0xC4;

    public static bool TryDecode(ReadOnlySpan<byte> delta, ReadOnlySpan<byte> source, out byte[] target)
    {
        try
        {
            target = Decode(delta, source);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            target = [];
            return false;
        }
    }

    public static byte[] Decode(ReadOnlySpan<byte> delta, ReadOnlySpan<byte> source)
    {
        if (delta.Length < 4 ||
            delta[0] != Magic0 ||
            delta[1] != Magic1 ||
            delta[2] != Magic2)
        {
            throw new InvalidDataException("VCDIFF header invalid.");
        }

        int offset = 4;
        byte hdrIndicator = delta[offset - 1];
        // Header layout: magic(3) + indicator(1) already consumed indicator at [3]
        offset = 4;
        hdrIndicator = delta[3];

        if ((hdrIndicator & 0x01) != 0) // VCD_DECOMPRESS
            throw new InvalidDataException("VCDIFF secondary decompression is not supported.");
        if ((hdrIndicator & 0x02) != 0) // VCD_CODETABLE
            throw new InvalidDataException("VCDIFF custom code tables are not supported.");
        if ((hdrIndicator & 0x04) != 0) // VCD_APPHEADER
        {
            int appLen = ReadInt(delta, ref offset);
            offset = checked(offset + appLen);
        }

        using MemoryStream output = new();
        AddressCache cache = new();
        while (offset < delta.Length)
        {
            byte winIndicator = delta[offset++];
            ReadOnlySpan<byte> sourceWindow = ReadOnlySpan<byte>.Empty;
            if ((winIndicator & 0x01) != 0) // VCD_SOURCE
            {
                int sourceSize = ReadInt(delta, ref offset);
                int sourcePos = ReadInt(delta, ref offset);
                if ((uint)sourcePos > (uint)source.Length ||
                    (uint)sourceSize > (uint)(source.Length - sourcePos))
                {
                    throw new InvalidDataException("VCDIFF source window out of range.");
                }

                sourceWindow = source.Slice(sourcePos, sourceSize);
            }
            else if ((winIndicator & 0x02) != 0) // VCD_TARGET
            {
                throw new InvalidDataException("VCDIFF target-as-source windows are not supported.");
            }

            int deltaEncodingLength = ReadInt(delta, ref offset);
            int windowEnd = checked(offset + deltaEncodingLength);
            if (windowEnd > delta.Length)
                throw new InvalidDataException("VCDIFF delta encoding length exceeds payload.");

            int targetLen = ReadInt(delta, ref offset);
            byte deltaIndicator = delta[offset++];
            if (deltaIndicator != 0)
                throw new InvalidDataException("VCDIFF interleaved/compressed sections are not supported.");

            int dataLen = ReadInt(delta, ref offset);
            int instLen = ReadInt(delta, ref offset);
            int addrLen = ReadInt(delta, ref offset);
            if (checked(dataLen + instLen + addrLen) > windowEnd - offset)
                throw new InvalidDataException("VCDIFF section lengths exceed window.");

            ReadOnlySpan<byte> data = delta.Slice(offset, dataLen);
            offset += dataLen;
            ReadOnlySpan<byte> inst = delta.Slice(offset, instLen);
            offset += instLen;
            ReadOnlySpan<byte> addr = delta.Slice(offset, addrLen);
            offset += addrLen;
            if (offset != windowEnd)
                throw new InvalidDataException("VCDIFF window size mismatch.");

            byte[] window = DecodeWindow(sourceWindow, data, inst, addr, targetLen, cache);
            output.Write(window, 0, window.Length);
        }

        return output.ToArray();
    }

    private static byte[] DecodeWindow(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> inst,
        ReadOnlySpan<byte> addr,
        int targetLen,
        AddressCache cache)
    {
        byte[] target = new byte[targetLen];
        int here = 0;
        int dataPos = 0;
        int instPos = 0;
        int addrPos = 0;
        cache.Reset();

        while (here < targetLen)
        {
            if (instPos >= inst.Length)
                throw new InvalidDataException("VCDIFF instruction stream exhausted.");

            byte code = inst[instPos++];
            CodeEntry entry = DefaultCodeTable[code];
            Execute(entry.Type1, entry.Size1, entry.Mode1, source, target, ref here, data, ref dataPos, addr, ref addrPos, cache);
            if (entry.Type2 != InstType.Noop)
                Execute(entry.Type2, entry.Size2, entry.Mode2, source, target, ref here, data, ref dataPos, addr, ref addrPos, cache);
        }

        if (here != targetLen)
            throw new InvalidDataException("VCDIFF produced unexpected target length.");
        return target;
    }

    private static void Execute(
        InstType type,
        int size,
        int mode,
        ReadOnlySpan<byte> source,
        Span<byte> target,
        ref int here,
        ReadOnlySpan<byte> data,
        ref int dataPos,
        ReadOnlySpan<byte> addr,
        ref int addrPos,
        AddressCache cache)
    {
        if (type == InstType.Noop)
            return;

        if (size == 0)
        {
            size = type is InstType.Add or InstType.Run
                ? ReadInt(data, ref dataPos)
                : ReadInt(addr, ref addrPos);
        }

        switch (type)
        {
            case InstType.Add:
            {
                if (size < 0 || dataPos + size > data.Length || here + size > target.Length)
                    throw new InvalidDataException("VCDIFF ADD out of range.");
                data.Slice(dataPos, size).CopyTo(target.Slice(here, size));
                dataPos += size;
                here += size;
                break;
            }
            case InstType.Run:
            {
                if (size < 0 || dataPos >= data.Length || here + size > target.Length)
                    throw new InvalidDataException("VCDIFF RUN out of range.");
                byte value = data[dataPos++];
                target.Slice(here, size).Fill(value);
                here += size;
                break;
            }
            case InstType.Copy:
            {
                if (size < 0 || here + size > target.Length)
                    throw new InvalidDataException("VCDIFF COPY out of range.");
                int address = cache.Decode(mode, here + source.Length, addr, ref addrPos);
                for (int i = 0; i < size; i++)
                {
                    int abs = address + i;
                    byte value = abs < source.Length
                        ? source[abs]
                        : target[abs - source.Length];
                    target[here + i] = value;
                }

                cache.Update(address);
                here += size;
                break;
            }
            default:
                throw new InvalidDataException("Unknown VCDIFF instruction.");
        }
    }

    private static int ReadInt(ReadOnlySpan<byte> data, ref int offset)
    {
        int result = 0;
        while (offset < data.Length)
        {
            byte b = data[offset++];
            result = (result << 7) | (b & 0x7F);
            if ((b & 0x80) == 0)
                return result;
        }

        throw new InvalidDataException("VCDIFF integer truncated.");
    }

    private enum InstType : byte
    {
        Noop,
        Add,
        Run,
        Copy
    }

    private readonly struct CodeEntry(
        InstType type1,
        int size1,
        int mode1,
        InstType type2,
        int size2,
        int mode2)
    {
        public InstType Type1 { get; } = type1;
        public int Size1 { get; } = size1;
        public int Mode1 { get; } = mode1;
        public InstType Type2 { get; } = type2;
        public int Size2 { get; } = size2;
        public int Mode2 { get; } = mode2;
    }

    private sealed class AddressCache
    {
        private const int NearSize = 4;
        private const int SameSize = 3 * 256;
        private readonly int[] _near = new int[NearSize];
        private readonly int[] _same = new int[SameSize];
        private int _nextNear;

        public void Reset()
        {
            Array.Clear(_near);
            Array.Clear(_same);
            _nextNear = 0;
        }

        public int Decode(int mode, int here, ReadOnlySpan<byte> addr, ref int addrPos)
        {
            // Modes: 0=SELF, 1=HERE, 2..5=NEAR, 6..8=SAME (s_near=4, s_same=3)
            if (mode == 0)
                return ReadInt(addr, ref addrPos);
            if (mode == 1)
                return here - ReadInt(addr, ref addrPos);
            if (mode >= 2 && mode <= 5)
                return _near[mode - 2] + ReadInt(addr, ref addrPos);
            if (mode >= 6 && mode <= 8)
            {
                if (addrPos >= addr.Length)
                    throw new InvalidDataException("VCDIFF same-address stream exhausted.");
                int m = mode - 6;
                return _same[(m * 256) + addr[addrPos++]];
            }

            throw new InvalidDataException("VCDIFF address mode invalid.");
        }

        public void Update(int address)
        {
            _near[_nextNear] = address;
            _nextNear = (_nextNear + 1) % NearSize;
            _same[address % SameSize] = address;
        }
    }

    /// <summary>RFC 3284 Appendix A default code table.</summary>
    private static readonly CodeEntry[] DefaultCodeTable = BuildDefaultCodeTable();

    private static CodeEntry[] BuildDefaultCodeTable()
    {
        CodeEntry[] table = new CodeEntry[256];
        int i = 0;
        table[i++] = new(InstType.Run, 0, 0, InstType.Noop, 0, 0);
        table[i++] = new(InstType.Add, 0, 0, InstType.Noop, 0, 0);
        for (int size = 1; size <= 17; size++)
            table[i++] = new(InstType.Add, size, 0, InstType.Noop, 0, 0);

        for (int mode = 0; mode <= 8; mode++)
        {
            table[i++] = new(InstType.Copy, 0, mode, InstType.Noop, 0, 0);
            for (int size = 4; size <= 18; size++)
                table[i++] = new(InstType.Copy, size, mode, InstType.Noop, 0, 0);
        }

        for (int mode = 0; mode <= 5; mode++)
        {
            for (int add = 1; add <= 4; add++)
            {
                for (int copy = 4; copy <= 6; copy++)
                    table[i++] = new(InstType.Add, add, 0, InstType.Copy, copy, mode);
            }
        }

        while (i < 256)
            table[i++] = new(InstType.Noop, 0, 0, InstType.Noop, 0, 0);

        return table;
    }
}
