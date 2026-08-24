// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.IO.Net;
using PCL.Core.Logging;
using PCL.Core.Serialization;

namespace PCL.Core.Portable.Test;

[TestClass]
public sealed class PortableInfrastructureTests
{
    [TestMethod]
    public void PortableJson_AllowsCommentsTrailingCommasAndStringNumbers()
    {
        var node = PortableJson.ParseObject("""
            {
                // launcher metadata can contain comments in local debug fixtures
                "count": "42",
            }
            """);

        Assert.AreEqual("42", node["COUNT"]?.ToString());
        var model = JsonSerializer.Deserialize<JsonModel>(
            """{"count":"42"}""",
            PortableJson.SerializerOptions);
        Assert.IsNotNull(model);
        Assert.AreEqual(42, model.Count);
    }

    [TestMethod]
    public void PortableLog_PublishesStructuredEntries()
    {
        var entries = new List<PortableLogEntry>();
        void Capture(PortableLogEntry entry) => entries.Add(entry);

        PortableLog.Written += Capture;
        try
        {
            PortableLog.Warn("PortableTest", "hello");
        }
        finally
        {
            PortableLog.Written -= Capture;
        }

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(PortableLogLevel.Warn, entries[0].Level);
        Assert.AreEqual("PortableTest", entries[0].Module);
        Assert.AreEqual("hello", entries[0].Message);
        Assert.AreNotEqual(default, entries[0].Timestamp);
    }

    [TestMethod]
    public void PortableLog_InfoFiltersDebugAndRealTime()
    {
        PortableLogLevel previous = PortableLog.MaximumLevel;
        List<PortableLogEntry> entries = [];
        void Capture(PortableLogEntry entry) => entries.Add(entry);

        PortableLog.Written += Capture;
        try
        {
            PortableLog.MaximumLevel = PortableLogLevel.Info;
            PortableLog.Error("Levels", "error");
            PortableLog.Warn("Levels", "warn");
            PortableLog.Info("Levels", "info");
            PortableLog.Debug("Levels", "debug");
            PortableLog.RealTime("Levels", "realtime");
        }
        finally
        {
            PortableLog.Written -= Capture;
            PortableLog.MaximumLevel = previous;
        }

        CollectionAssert.AreEqual(
            new[] { PortableLogLevel.Error, PortableLogLevel.Warn, PortableLogLevel.Info },
            entries.Select(static entry => entry.Level).ToArray());
    }

    [TestMethod]
    public void PortableLog_RealTimeEnablesHighFrequencyEntriesAndRedactsSecrets()
    {
        PortableLogLevel previous = PortableLog.MaximumLevel;
        List<PortableLogEntry> entries = [];
        void Capture(PortableLogEntry entry) => entries.Add(entry);

        PortableLog.Written += Capture;
        try
        {
            PortableLog.MaximumLevel = PortableLogLevel.RealTime;
            PortableLog.RealTime(
                "Loop",
                "tick access_token=abc123 Authorization: Bearer top-secret https://example.test/?code=login-code exit code: 1");
        }
        finally
        {
            PortableLog.Written -= Capture;
            PortableLog.MaximumLevel = previous;
        }

        Assert.HasCount(1, entries);
        Assert.AreEqual(PortableLogLevel.RealTime, entries[0].Level);
        StringAssert.Contains(entries[0].Message, "access_token=<redacted>");
        StringAssert.Contains(entries[0].Message, "Authorization: <redacted>");
        StringAssert.Contains(entries[0].Message, "?code=<redacted>");
        StringAssert.Contains(entries[0].Message, "exit code: 1");
        Assert.DoesNotContain("abc123", entries[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret", entries[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("login-code", entries[0].Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task PortableHttp_ReadStringAsync_UsesResponseCancellationAwareApi()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("portable", Encoding.UTF8, "text/plain")
        };

        var value = await PortableHttp.ReadStringAsync(response, TestContext.CancellationToken);

        Assert.AreEqual("portable", value);
    }

    [TestMethod]
    public async Task PortableHttp_ConfigureAfterRequest_ReplacesHandlerWithoutReplacingClient()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = ServeSingleHttpResponseAsync(listener, TestContext.CancellationToken);

        PortableHttp.Configure(enableDoH: false, proxy: null, useProxy: false);
        HttpClient client = PortableHttp.Client;
        string response = await client.GetStringAsync(
            new Uri($"http://127.0.0.1:{port}/"),
            TestContext.CancellationToken);

        Assert.AreEqual("ok", response);
        PortableHttp.Configure(enableDoH: true, proxy: null, useProxy: true);
        Assert.AreSame(client, PortableHttp.Client);
        await server;
    }

    private static async Task ServeSingleHttpResponseAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using NetworkStream stream = client.GetStream();
        var requestBuffer = new byte[4096];
        _ = await stream.ReadAsync(requestBuffer, cancellationToken);
        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        await stream.WriteAsync(response, cancellationToken);
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed record JsonModel(int Count);
}
