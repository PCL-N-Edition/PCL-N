// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Application.Launching;
using PCL.Desktop.Features.Launching;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class MinecraftSessionBridgeTests
{
    [TestMethod]
    public void JvmHostEntryPoint_RunsSimpleMainWhenJavaIsConfigured()
    {
        string? java = Environment.GetEnvironmentVariable("PCL_JVM_HOST_SMOKE_JAVA");
        if (string.IsNullOrWhiteSpace(java) || !File.Exists(java))
            Assert.Inconclusive("Set PCL_JVM_HOST_SMOKE_JAVA to run the native Jvm.NET smoke test.");

        string bin = MinecraftJvmHostEntryPoint.ResolveJdkBinPath(java);
        string javac = Path.Combine(bin, OperatingSystem.IsWindows() ? "javac.exe" : "javac");
        if (!File.Exists(javac))
            Assert.Inconclusive("The configured Java runtime does not include javac.");

        string root = Path.Combine(Path.GetTempPath(), "pcl-jvm-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string sourcePath = Path.Combine(root, "PclJvmHostSmoke.java");
            File.WriteAllText(
                sourcePath,
                "import com.mojang.authlib.TestAuthlib; " +
                "public final class PclJvmHostSmoke { public static void main(String[] args) { " +
                "if (args.length != 1 || !\"ok\".equals(args[0])) throw new IllegalArgumentException(); " +
                "if (!TestAuthlib.endpoint().startsWith(\"http://127.0.0.1:\")) throw new IllegalStateException(TestAuthlib.endpoint()); " +
                "if (!TestAuthlib.isSignatureValid(new Object()) || !TestAuthlib.isAllowedTextureDomain(\"http://127.0.0.1:1\")) " +
                "throw new IllegalStateException(\"authlib transformer not active\"); } }");
            string authlibSourcePath = Path.Combine(root, "TestAuthlib.java");
            File.WriteAllText(
                authlibSourcePath,
                "package com.mojang.authlib; public final class TestAuthlib { " +
                "public static String endpoint() { return \"https://sessionserver.mojang.com\"; } " +
                "public static boolean isSignatureValid(Object property) { return false; } " +
                "public static boolean isAllowedTextureDomain(String url) { return false; } }");
            ProcessStartInfo compileInfo = new()
            {
                FileName = javac,
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            compileInfo.ArgumentList.Add("-d");
            compileInfo.ArgumentList.Add(root);
            compileInfo.ArgumentList.Add(sourcePath);
            compileInfo.ArgumentList.Add(authlibSourcePath);
            using Process compiler = Process.Start(compileInfo)!;
            compiler.WaitForExit();
            Assert.AreEqual(0, compiler.ExitCode, "Java smoke class compilation failed.");

            MinecraftJvmHostRequest request = new()
            {
                JavaExecutablePath = java,
                WorkingDirectory = root,
                MainClass = "PclJvmHostSmoke",
                PlayerName = "Smoke",
                PlayerUuid = Guid.Empty.ToString("N"),
                JavaMajorVersion = 26,
                ClasspathEntries = [root],
                GameArguments = ["ok"],
                IdentityMode = MinecraftJvmHostIdentityMode.Offline
            };
            string requestPath = Path.Combine(root, "request.json");
            File.WriteAllText(
                requestPath,
                JsonSerializer.Serialize(request, MinecraftJvmHostJsonContext.Default.MinecraftJvmHostRequest));

            Assert.AreEqual(0, MinecraftJvmHostEntryPoint.Run(requestPath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task OfflineProfile_ExposesSessionScopedCustomSkin()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-jvm-bridge-" + Guid.NewGuid().ToString("N"));
        string skinPath = Path.Combine(root, "skin.png");
        Directory.CreateDirectory(root);
        byte[] png = CreatePngHeader(64, 64);
        await File.WriteAllBytesAsync(skinPath, png);

        try
        {
            MinecraftJvmHostRequest request = new()
            {
                JavaExecutablePath = Path.Combine(root, "java"),
                WorkingDirectory = root,
                MainClass = "net.minecraft.client.main.Main",
                PlayerName = "OfflineUser",
                PlayerUuid = "0123456789abcdef0123456789abcdef",
                JavaMajorVersion = 17,
                ClasspathEntries = [Path.Combine(root, "client.jar")],
                IdentityMode = MinecraftJvmHostIdentityMode.Offline,
                OfflineSkinSource = skinPath,
                OfflineSkinSlim = true
            };

            using JvmHostLifecycleWriter lifecycle = new(string.Empty);
            using MinecraftSessionBridge bridge = MinecraftSessionBridge.Start(request, lifecycle);
            using HttpClient client = new(new HttpClientHandler { UseProxy = false });

            string profileJson = await client.GetStringAsync(
                bridge.BaseUrl + "/sessionserver/session/minecraft/profile/" + request.PlayerUuid);
            JsonObject profile = JsonNode.Parse(profileJson)!.AsObject();
            Assert.AreEqual(request.PlayerName, profile["name"]!.GetValue<string>());
            JsonObject property = profile["properties"]!.AsArray()[0]!.AsObject();
            string value = property["value"]!.GetValue<string>();
            JsonObject texturesPayload = JsonNode.Parse(Convert.FromBase64String(value))!.AsObject();
            JsonObject skin = texturesPayload["textures"]!["SKIN"]!.AsObject();
            Assert.AreEqual("slim", skin["metadata"]!["model"]!.GetValue<string>());

            string textureUrl = skin["url"]!.GetValue<string>();
            byte[] downloaded = await client.GetByteArrayAsync(textureUrl);
            CollectionAssert.AreEqual(png, downloaded);

            string discoveryJson = await client.GetStringAsync(bridge.BaseUrl + "/minecraft/client");
            StringAssert.Contains(discoveryJson, "session");
            StringAssert.Contains(discoveryJson, "getProfileById");
            StringAssert.Contains(discoveryJson, "getPublicKeys");
            StringAssert.Contains(discoveryJson, bridge.BaseUrl);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] CreatePngHeader(int width, int height)
    {
        byte[] bytes = new byte[24];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 13);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }
}
