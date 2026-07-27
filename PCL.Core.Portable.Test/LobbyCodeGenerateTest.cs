// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Link.Scaffolding;
using PCL.Core.Link.Scaffolding.Client.Models;

namespace PCL.Core.Test;

[TestClass]
public sealed class LobbyCodeGenerateTest
{
    [TestMethod]
    public void GeneratedCode_UsesUpstreamTerracottaFormat()
    {
        LobbyInfo generated = LobbyCodeGenerator.Generate();

        Assert.AreEqual(21, generated.FullCode.Length);
        Assert.StartsWith("U/", generated.FullCode);
        Assert.AreEqual('-', generated.FullCode[6]);
        Assert.AreEqual('-', generated.FullCode[11]);
        Assert.AreEqual('-', generated.FullCode[16]);
        Assert.StartsWith("scaffolding-mc-", generated.NetworkName);
        Assert.AreEqual(24, generated.NetworkName.Length);
        Assert.AreEqual(9, generated.NetworkSecret.Length);
    }

    [TestMethod]
    public void GeneratedCode_RoundTripsToSameEasyTierNetwork()
    {
        LobbyInfo generated = LobbyCodeGenerator.Generate();

        Assert.IsTrue(LobbyCodeGenerator.TryParse(generated.FullCode, out LobbyInfo? parsed));
        Assert.IsNotNull(parsed);
        Assert.AreEqual(generated, parsed);
    }

    [TestMethod]
    public void InvalidChecksum_IsRejected()
    {
        string code = LobbyCodeGenerator.Generate().FullCode;
        char replacement = code[^1] == '0' ? '1' : '0';
        string invalid = code[..^1] + replacement;

        Assert.IsFalse(LobbyCodeGenerator.TryParse(invalid, out _));
    }

    [TestMethod]
    public void ScaffoldingFrame_UsesUpstreamNetworkByteOrder()
    {
        byte[] packet = ScaffoldingProtocol.EncodeRequest("c:ping", [0x12, 0x34]);
        byte[] expected =
        [
            0x06,
            (byte)'c', (byte)':', (byte)'p', (byte)'i', (byte)'n', (byte)'g',
            0x00, 0x00, 0x00, 0x02,
            0x12, 0x34
        ];

        CollectionAssert.AreEqual(expected, packet);
        CollectionAssert.AreEqual(
            new byte[] { 0x00, 0x00, 0x00, 0x00, 0x02, 0x12, 0x34 },
            ScaffoldingProtocol.EncodeResponse(0, [0x12, 0x34]));
    }
}
