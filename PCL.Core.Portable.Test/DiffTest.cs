// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using ICSharpCode.SharpZipLib.BZip2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Utils.Diff;

namespace PCL.Core.Test;

[TestClass]
public class DiffTest
{
    [TestMethod]
    public async Task TestBsDiff()
    {
        var diff = new BsDiff();
        var res = await diff.ApplyAsync(
            [
                73, 32, 97, 109, 32, 110, 111, 116, 32, 115, 117, 114, 101, 32, 104, 111, 119, 32, 99, 104, 111, 117,
                108,
                100, 32, 98, 115, 100, 105, 102, 102, 32, 119, 111, 114, 107
            ],
            [
                66, 83, 68, 73, 70, 70, 52, 48, 54, 0, 0, 0, 0, 0, 0, 0, 39, 0, 0, 0, 0, 0, 0, 0, 30, 0, 0, 0, 0, 0, 0,
                0, 66, 90, 104, 57, 49, 65, 89, 38, 83, 89, 247, 165, 175, 102, 0, 0, 17, 192, 64, 94, 172, 64, 0, 32,
                0, 33, 41, 164, 201, 232, 16, 3, 7, 87, 127, 120, 200, 130, 144, 45, 201, 94, 39, 120, 93, 201, 20, 225,
                66, 67, 222, 150, 189, 152, 66, 90, 104, 57, 49, 65, 89, 38, 83, 89, 76, 125, 55, 245, 0, 0, 0, 96, 0,
                64, 0, 1, 0, 32, 0, 33, 0, 130, 131, 23, 114, 69, 56, 80, 144, 76, 125, 55, 245, 66, 90, 104, 57, 49,
                65, 89, 38, 83, 89, 108, 69, 160, 122, 0, 0, 1, 1, 128, 2, 0, 17, 32, 32, 0, 33, 154, 104, 51, 77, 48,
                188, 93, 201, 20, 225, 66, 65, 177, 22, 129, 232
            ]);
        byte[] trueData =
        [
            73, 32, 97, 109, 32, 118, 101, 114, 121, 32, 115, 117, 114, 101, 32, 104, 111, 119, 32, 98, 115, 100, 105,
            102, 102, 32, 119, 111, 114, 107
        ];
        CollectionAssert.AreEqual(trueData, res);
    }

    [TestMethod]
    public async Task TestBsDiffMakeRoundTrip()
    {
        var diff = new BsDiff();
        byte[] originData =
        [
            73, 32, 97, 109, 32, 110, 111, 116, 32, 115, 117, 114, 101, 32, 104, 111, 119, 32, 99, 104, 111, 117,
            108, 100, 32, 98, 115, 100, 105, 102, 102, 32, 119, 111, 114, 107
        ];
        byte[] newData =
        [
            73, 32, 97, 109, 32, 118, 101, 114, 121, 32, 115, 117, 114, 101, 32, 104, 111, 119, 32, 98, 115, 100,
            105, 102, 102, 32, 119, 111, 114, 107
        ];

        byte[] patch = await diff.MakeAsync(originData, newData);
        byte[] applied = await diff.ApplyAsync(originData, patch);

        CollectionAssert.AreEqual(newData, applied);
    }

    [TestMethod]
    public async Task TestBsDiffMakeEmptyTargetRoundTrip()
    {
        var diff = new BsDiff();
        byte[] patch = await diff.MakeAsync([1, 2, 3], []);
        byte[] applied = await diff.ApplyAsync([1, 2, 3], patch);

        CollectionAssert.AreEqual(Array.Empty<byte>(), applied);
    }

    [TestMethod]
    public async Task TestBsDiffAppliesLargeVectorizedDiffBlock()
    {
        const int length = 512 * 1024 + 13;
        byte[] origin = new byte[length];
        byte[] expected = new byte[length];
        byte[] delta = new byte[length];
        Random random = new(20260729);
        random.NextBytes(origin);
        random.NextBytes(expected);
        for (int index = 0; index < length; index++)
            delta[index] = unchecked((byte)(expected[index] - origin[index]));

        byte[] patch = BuildAddPatch(delta);
        byte[] applied = await new BsDiff().ApplyAsync(origin, patch);

        CollectionAssert.AreEqual(expected, applied);
    }

    private static byte[] BuildAddPatch(byte[] delta)
    {
        byte[] control = new byte[24];
        BitConverter.TryWriteBytes(control.AsSpan(0, 8), (long)delta.Length);
        byte[] compressedControl = Compress(control);
        byte[] compressedDelta = Compress(delta);
        byte[] compressedExtra = Compress([]);

        using MemoryStream patch = new();
        using BinaryWriter writer = new(patch);
        writer.Write(0x3034464649445342L);
        writer.Write((long)compressedControl.Length);
        writer.Write((long)compressedDelta.Length);
        writer.Write((long)delta.Length);
        writer.Write(compressedControl);
        writer.Write(compressedDelta);
        writer.Write(compressedExtra);
        writer.Flush();
        return patch.ToArray();
    }

    private static byte[] Compress(byte[] data)
    {
        using MemoryStream stream = new();
        using (BZip2OutputStream output = new(stream))
            output.Write(data);
        return stream.ToArray();
    }
}
