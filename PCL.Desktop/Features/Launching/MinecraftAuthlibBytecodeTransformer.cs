// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Jvm.NET.Abstractions;

namespace PCL.Desktop.Features.Launching;

/// <summary>
/// JVMTI-time wrapper around <see cref="AuthlibClassTransformer"/> for the session bridge.
/// Preferred path is on-disk jar replacement via <see cref="AuthlibJarPatcher"/> (JNI host disables JVMTI).
/// </summary>
internal sealed class MinecraftAuthlibBytecodeTransformer(string bridgeBaseUrl) : IBytecodeTransformer
{
    private readonly AuthlibPatchProfile _profile = AuthlibPatchProfile.ForLoopbackBridge(bridgeBaseUrl);

    public string Name => "PCL-N authlib loopback bridge";

    public byte[]? Transform(string className, byte[] originalBytes) =>
        AuthlibClassTransformer.Transform(className, originalBytes, _profile);
}
