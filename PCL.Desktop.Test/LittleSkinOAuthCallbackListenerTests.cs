// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Sockets;
using System.Text;
using PCL.Desktop.Features.Launching;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class LittleSkinOAuthCallbackListenerTests
{
    [TestMethod]
    public void ParseCallbackTarget_ReadsCodeStateAndErrors()
    {
        Uri redirect = new("http://127.0.0.1:17342/oauth/littleskin/callback");

        LittleSkinAuthorizationCallback? success =
            LittleSkinOAuthCallbackListener.ParseCallbackTarget(
                "/oauth/littleskin/callback?code=abc%2B123&state=expected",
                redirect);
        LittleSkinAuthorizationCallback? failure =
            LittleSkinOAuthCallbackListener.ParseCallbackTarget(
                "/oauth/littleskin/callback?error=access_denied&" +
                "error_description=No+thanks&state=expected",
                redirect);

        Assert.IsNotNull(success);
        Assert.AreEqual("abc+123", success.Code);
        Assert.AreEqual("expected", success.State);
        Assert.IsNotNull(failure);
        Assert.AreEqual("access_denied", failure.Error);
        Assert.AreEqual("No thanks", failure.ErrorDescription);
    }

    [TestMethod]
    public void ParseCallbackTarget_RejectsWrongPathAndUsesConstantTimeStateCheck()
    {
        Uri redirect = new("http://127.0.0.1:17342/oauth/littleskin/callback");

        Assert.IsNull(LittleSkinOAuthCallbackListener.ParseCallbackTarget(
            "/oauth/other/callback?code=abc&state=expected",
            redirect));
        Assert.IsTrue(LittleSkinOAuthCallbackListener.HasExpectedState(
            "expected",
            "expected"));
        Assert.IsFalse(LittleSkinOAuthCallbackListener.HasExpectedState(
            "unexpected",
            "expected"));
        Assert.IsFalse(LittleSkinOAuthCallbackListener.HasExpectedState(
            null,
            "expected"));
    }

    [TestMethod]
    public void CreateState_ReturnsUniqueUrlSafeEntropy()
    {
        string first = LittleSkinOAuthCallbackListener.CreateState();
        string second = LittleSkinOAuthCallbackListener.CreateState();

        Assert.AreNotEqual(first, second);
        Assert.IsTrue(first.Length >= 40);
        Assert.IsFalse(first.Contains('+', StringComparison.Ordinal));
        Assert.IsFalse(first.Contains('/', StringComparison.Ordinal));
        Assert.IsFalse(first.Contains('=', StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task WaitForCallbackAsync_ReceivesLoopbackAuthorizationCode()
    {
        TcpListener reservation = new(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();

        Uri redirect = new(
            $"http://127.0.0.1:{port}/oauth/littleskin/callback");
        using LittleSkinOAuthCallbackListener listener = new(redirect);
        listener.Start();
        Task<LittleSkinAuthorizationCallback> callbackTask =
            listener.WaitForCallbackAsync("expected-state", CancellationToken.None);

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using NetworkStream stream = client.GetStream();
        byte[] request = Encoding.ASCII.GetBytes(
            "GET /oauth/littleskin/callback?code=authorization-code&" +
            "state=expected-state HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);
        await stream.FlushAsync();
        using MemoryStream response = new();
        await stream.CopyToAsync(response);

        LittleSkinAuthorizationCallback callback = await callbackTask;

        Assert.AreEqual("authorization-code", callback.Code);
        Assert.AreEqual("expected-state", callback.State);
        StringAssert.Contains(
            Encoding.UTF8.GetString(response.ToArray()),
            "200 OK");
    }
}
