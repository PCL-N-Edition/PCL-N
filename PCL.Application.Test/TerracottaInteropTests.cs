// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Application.Link;
using PCL.Core.Link.Scaffolding.Client.Models;

namespace PCL.Application.Test;

[TestClass]
public sealed class TerracottaInteropTests
{
    [TestMethod]
    public async Task EasyTierInstaller_ExtractsValidatedPlatformBundle()
    {
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), "pcln-easytier-test-" + Guid.NewGuid().ToString("N"));
        byte[] archive = CreateEasyTierArchive();
        using HttpClient httpClient = new(new StaticArchiveHandler(archive));
        try
        {
            await using EasyTierRuntime runtime = new(temporaryDirectory, httpClient);
            await runtime.EnsureInstalledAsync();

            string coreName = OperatingSystem.IsWindows() ? "easytier-core.exe" : "easytier-core";
            string cliName = OperatingSystem.IsWindows() ? "easytier-cli.exe" : "easytier-cli";
            Assert.IsTrue(Directory.EnumerateFiles(
                temporaryDirectory,
                coreName,
                SearchOption.AllDirectories).Any());
            Assert.IsTrue(Directory.EnumerateFiles(
                temporaryDirectory,
                cliName,
                SearchOption.AllDirectories).Any());
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ScaffoldingClientAndServer_UsePclCeCompatibleControlFlow()
    {
        int port = GetFreeTcpPort();
        PlayerProfile host = new()
        {
            Name = "Host",
            MachineId = "host-machine",
            Vendor = "PCL CE test",
            Kind = PlayerKind.HOST
        };
        await using ScaffoldingServerHost server = new(port, 25565, host);
        server.Start();

        PlayerProfile guest = new()
        {
            Name = "Guest",
            MachineId = "guest-machine",
            Vendor = "PCL N test"
        };
        await using ScaffoldingClientSession client = new("127.0.0.1", port, guest);
        await client.ConnectAsync();

        IReadOnlyList<string> protocols = await client.GetProtocolsAsync();
        Assert.IsTrue(protocols.Contains("c:server_port"));
        Assert.IsTrue(protocols.Contains("c:player_ping"));
        Assert.AreEqual((ushort)25565, await client.GetMinecraftPortAsync());
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes("pcln"),
            (await client.PingAsync(Encoding.UTF8.GetBytes("pcln"))).ToArray());

        IReadOnlyList<PlayerProfile> players = await client.GetPlayersAsync();
        Assert.AreEqual(2, players.Count);
        Assert.AreEqual(PlayerKind.HOST, players[0].Kind);
        Assert.AreEqual(PlayerKind.GUEST, players[1].Kind);
    }

    private static int GetFreeTcpPort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static byte[] CreateEasyTierArchive()
    {
        string coreName = OperatingSystem.IsWindows() ? "easytier-core.exe" : "easytier-core";
        string cliName = OperatingSystem.IsWindows() ? "easytier-cli.exe" : "easytier-cli";
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (StreamWriter writer = new(zip.CreateEntry("bundle/" + coreName).Open()))
                writer.Write("core");
            using (StreamWriter writer = new(zip.CreateEntry("bundle/" + cliName).Open()))
                writer.Write("cli");
        }

        return stream.ToArray();
    }

    private sealed class StaticArchiveHandler(byte[] archive) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(archive)
            });
        }
    }
}
