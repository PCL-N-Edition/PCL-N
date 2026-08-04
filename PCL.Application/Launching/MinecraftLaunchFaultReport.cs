// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Application.Launching;

/// <summary>
/// Stable machine-readable categories emitted by the launcher and the experimental JVM host.
/// Keep these values independent from exception type names so the repair pipeline can evolve.
/// </summary>
public enum MinecraftLaunchFaultCode
{
    Unknown,
    JavaRuntimeMissing,
    JavaRuntimeIncompatible,
    JvmInitializationFailed,
    MainClassMissing,
    ClasspathDependencyMissing,
    AuthenticationFailed,
    SessionServiceUnavailable,
    NativeLibraryFailed,
    GraphicsInitializationFailed,
    ModLoaderBootstrapFailed,
    ModConflict,
    MissingModDependency,
    OutOfMemory,
    FileLocked,
    AccessDenied
}

/// <summary>
/// Repair operations that may be selected by deterministic analysis or the local model.
/// The model is never allowed to invent commands or file writes outside this list.
/// </summary>
public enum MinecraftRepairActionKind
{
    InspectOnly,
    RepairVersionFiles,
    ReextractNatives,
    InstallMissingModDependencies,
    DownloadMod,
    DisableMod,
    UpdateMod,
    ReadModMetadata,
    SelectCompatibleJava,
    DownloadCompatibleJava,
    ReinstallVersionAndUpdateLoader,
    RefreshAccount,
    ReduceMemoryPressure,
    ReviewModSet,
    DisableExperimentalJvmHost
}

public sealed record MinecraftLaunchFaultReport
{
    public MinecraftLaunchFaultCode Code { get; init; }

    public string Stage { get; init; } = "Unknown";

    public string Subsystem { get; init; } = "Minecraft";

    public string ExceptionType { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string? StackTrace { get; init; }

    public string? LastClassName { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public string[] Evidence { get; init; } = [];

    public MinecraftRepairActionKind[] AllowedActions { get; init; } = [MinecraftRepairActionKind.InspectOnly];
}

public static class MinecraftLaunchFaultAnalyzer
{
    public static MinecraftLaunchFaultReport Analyze(
        Exception exception,
        string? stage = null,
        string? lastClassName = null,
        IEnumerable<string>? additionalEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string normalizedStage = string.IsNullOrWhiteSpace(stage) ? "Unknown" : stage.Trim();
        string combined = string.Join(
            '\n',
            new[] { exception.GetType().FullName, exception.Message, exception.StackTrace, lastClassName, normalizedStage }
                .Concat(additionalEvidence ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        MinecraftLaunchFaultCode code = Classify(combined, normalizedStage);
        return new MinecraftLaunchFaultReport
        {
            Code = code,
            Stage = normalizedStage,
            Subsystem = GetSubsystem(code, lastClassName),
            ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
            Message = exception.Message,
            StackTrace = Truncate(exception.StackTrace, 16_384),
            LastClassName = string.IsNullOrWhiteSpace(lastClassName) ? null : lastClassName,
            Evidence = NormalizeEvidence(additionalEvidence),
            AllowedActions = GetAllowedActions(code)
        };
    }

    public static MinecraftLaunchFaultReport AnalyzeText(
        IEnumerable<string> evidence,
        string? stage = null,
        string? lastClassName = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        string[] normalized = NormalizeEvidence(evidence);
        string combined = string.Join('\n', normalized.Prepend(lastClassName ?? string.Empty));
        MinecraftLaunchFaultCode code = Classify(combined, stage ?? "GameProcess");
        return new MinecraftLaunchFaultReport
        {
            Code = code,
            Stage = string.IsNullOrWhiteSpace(stage) ? "GameProcess" : stage,
            Subsystem = GetSubsystem(code, lastClassName),
            Message = normalized.LastOrDefault() ?? "Minecraft 进程异常退出。",
            LastClassName = lastClassName,
            Evidence = normalized,
            AllowedActions = GetAllowedActions(code)
        };
    }

    private static MinecraftLaunchFaultCode Classify(string text, string stage)
    {
        if (ContainsAny(text, "being used by another process", "另一个进程正在使用", "sharing violation"))
            return MinecraftLaunchFaultCode.FileLocked;
        if (ContainsAny(text, "access is denied", "accessdenied", "unauthorizedaccessexception", "permission denied", "拒绝访问"))
            return MinecraftLaunchFaultCode.AccessDenied;
        // High-confidence Java/runtime signatures synchronized with upstream PCL CE 2.15.0.
        // Keep these before generic ClassNotFound/NoClassDef rules so a missing JDK API is not
        // misdiagnosed as a damaged Minecraft library.
        if (ContainsAny(
                text,
                "unsupportedclassversionerror",
                "class file version",
                "only recognizes class file versions",
                "unsupported major.minor version",
                "unable to make protected final java.lang.class java.lang.classloader.defineclass",
                "java.lang.classcastexception: java.base/jdk",
                "java.lang.classcastexception: class jdk.",
                "open j9 is not supported",
                "openj9 is incompatible",
                ".j9vminternals.",
                "because module java.base does not export",
                "java.lang.classnotfoundexception: jdk.nashorn.api.scripting.nashornscriptenginefactory",
                "java.lang.classnotfoundexception: java.lang.invoke.lambdametafactory",
                "java.lang.nosuchmethoderror: sun.security.util.manifestentryverifier",
                "invalid maximum heap size"))
        {
            return MinecraftLaunchFaultCode.JavaRuntimeIncompatible;
        }
        if (ContainsAny(text, "outofmemoryerror", "java heap space", "unable to create native thread", "could not reserve enough space"))
            return MinecraftLaunchFaultCode.OutOfMemory;
        if (ContainsAny(text, "could not find or load main class", "mainclassmissing", "找不到或无法加载主类"))
            return MinecraftLaunchFaultCode.MainClassMissing;
        if (ContainsAny(text, "java.lang.classnotfoundexception: org.spongepowered.asm.launch.mixintweaker"))
            return MinecraftLaunchFaultCode.ModLoaderBootstrapFailed;
        if (ContainsAny(text, "noclassdeffounderror", "classnotfoundexception", "no such file or directory") &&
            !stage.Contains("JvmStarting", StringComparison.OrdinalIgnoreCase))
            return MinecraftLaunchFaultCode.ClasspathDependencyMissing;
        if (ContainsAny(text, "missing mandatory dependencies", "requires version", "requires any version", "mod resolution encountered", "依赖模组"))
            return MinecraftLaunchFaultCode.MissingModDependency;
        if (ContainsAny(
                text,
                "mod loading has failed",
                "modloadingexception",
                "mixin apply failed",
                "mixintransformererror",
                "failed to load mod",
                "the directories below appear to be extracted jar files",
                "extracted mod jars found, loading will not continue",
                "shaders mod detected. please remove it, optifine has built-in support for shaders",
                "invalid module name: '' is not a java identifier",
                "transformer/net.optifine/net.optifine.reflect.reflector.<clinit>(reflector.java",
                "net.minecraft.client.renderer.texture.spritecontents.<init>",
                "com.mojang.blaze3d.systems.rendersystem.getbackenddescription"))
        {
            return MinecraftLaunchFaultCode.ModConflict;
        }
        if (ContainsAny(
                text,
                "cannot find launch target fmlclient, unable to launch") ||
            ContainsAll(text, "invalid paths argument, contained no existing paths", "libraries\\net\\minecraftforge\\fmlcore") ||
            ContainsAny(text, "fabricloader", "modlauncher", "quiltloader", "neoforged", "minecraftforge") &&
            ContainsAny(text, "bootstrap", "failed", "exception", "error"))
        {
            return MinecraftLaunchFaultCode.ModLoaderBootstrapFailed;
        }
        if (ContainsAny(text, "unsatisfiedlinkerror", "failed to load library", "no lwjgl", "natives") ||
            ContainsAny(text, "org.lwjgl.system.library", "java.library.path"))
            return MinecraftLaunchFaultCode.NativeLibraryFailed;
        if (ContainsAny(
                text,
                "glfw error",
                "failed to create window",
                "opengl",
                "vulkan",
                "graphics driver",
                "pixel format",
                "the driver does not appear to support opengl",
                "couldn't set pixel format",
                "pixel format not accelerated",
                "1282: invalid operation",
                "maybe try a lower resolution resourcepack"))
        {
            return MinecraftLaunchFaultCode.GraphicsInitializationFailed;
        }
        if (ContainsAny(
                text,
                "invalid credentials",
                "invalid token",
                "forbiddenoperationexception",
                "authenticationexception",
                "http 401",
                "http 403",
                "n cloud 档案需要已登录",
                "n cloud 档案需要已登录的在线服务账户",
                "没有已登录的 pcl n 在线服务账户",
                "请在设置中重新连接账户",
                "请在设置 → 在线 → 账户",
                "在线服务账户",
                "重新连接账户"))
            return MinecraftLaunchFaultCode.AuthenticationFailed;
        if (ContainsAny(text, "sessionserver", "hasjoined", "joinserver", "http 503", "service unavailable") &&
            ContainsAny(text, "session", "auth", "profile"))
            return MinecraftLaunchFaultCode.SessionServiceUnavailable;
        if (ContainsAny(text, "jvm.dll", "libjvm.so", "libjvm.dylib", "java executable", "找不到 java"))
            return MinecraftLaunchFaultCode.JavaRuntimeMissing;
        if (stage.Contains("JvmStarting", StringComparison.OrdinalIgnoreCase) ||
            ContainsAny(text, "createjavavm", "jni_err", "jvmti", "jvminitializer"))
            return MinecraftLaunchFaultCode.JvmInitializationFailed;
        return MinecraftLaunchFaultCode.Unknown;
    }

    private static string GetSubsystem(MinecraftLaunchFaultCode code, string? lastClassName) => code switch
    {
        MinecraftLaunchFaultCode.JavaRuntimeMissing or
        MinecraftLaunchFaultCode.JavaRuntimeIncompatible or
        MinecraftLaunchFaultCode.JvmInitializationFailed => "JVM",
        MinecraftLaunchFaultCode.AuthenticationFailed or
        MinecraftLaunchFaultCode.SessionServiceUnavailable => "Authentication",
        MinecraftLaunchFaultCode.NativeLibraryFailed => "NativeRuntime",
        MinecraftLaunchFaultCode.GraphicsInitializationFailed => "Graphics",
        MinecraftLaunchFaultCode.ModLoaderBootstrapFailed or
        MinecraftLaunchFaultCode.ModConflict or
        MinecraftLaunchFaultCode.MissingModDependency => "ModLoader",
        MinecraftLaunchFaultCode.FileLocked or
        MinecraftLaunchFaultCode.AccessDenied => "FileSystem",
        _ when lastClassName?.Contains("lwjgl", StringComparison.OrdinalIgnoreCase) == true => "NativeRuntime",
        _ => "Minecraft"
    };

    private static MinecraftRepairActionKind[] GetAllowedActions(MinecraftLaunchFaultCode code) => code switch
    {
        MinecraftLaunchFaultCode.MainClassMissing or MinecraftLaunchFaultCode.ClasspathDependencyMissing =>
            [MinecraftRepairActionKind.RepairVersionFiles, MinecraftRepairActionKind.ReinstallVersionAndUpdateLoader],
        MinecraftLaunchFaultCode.NativeLibraryFailed =>
            [MinecraftRepairActionKind.ReextractNatives, MinecraftRepairActionKind.RepairVersionFiles],
        MinecraftLaunchFaultCode.MissingModDependency =>
            [
                MinecraftRepairActionKind.InstallMissingModDependencies,
                MinecraftRepairActionKind.DownloadMod,
                MinecraftRepairActionKind.ReadModMetadata
            ],
        MinecraftLaunchFaultCode.JavaRuntimeMissing or MinecraftLaunchFaultCode.JavaRuntimeIncompatible =>
            [MinecraftRepairActionKind.SelectCompatibleJava, MinecraftRepairActionKind.DownloadCompatibleJava],
        MinecraftLaunchFaultCode.AuthenticationFailed or MinecraftLaunchFaultCode.SessionServiceUnavailable =>
            [MinecraftRepairActionKind.RefreshAccount, MinecraftRepairActionKind.DisableExperimentalJvmHost],
        MinecraftLaunchFaultCode.OutOfMemory =>
            [MinecraftRepairActionKind.ReduceMemoryPressure],
        MinecraftLaunchFaultCode.ModConflict or MinecraftLaunchFaultCode.ModLoaderBootstrapFailed =>
            [
                MinecraftRepairActionKind.ReadModMetadata,
                MinecraftRepairActionKind.DisableMod,
                MinecraftRepairActionKind.UpdateMod,
                MinecraftRepairActionKind.DownloadMod,
                MinecraftRepairActionKind.ReinstallVersionAndUpdateLoader,
                MinecraftRepairActionKind.ReviewModSet
            ],
        MinecraftLaunchFaultCode.JvmInitializationFailed =>
            [
                MinecraftRepairActionKind.DisableExperimentalJvmHost,
                MinecraftRepairActionKind.SelectCompatibleJava,
                MinecraftRepairActionKind.DownloadCompatibleJava
            ],
        _ =>
        [
            MinecraftRepairActionKind.InspectOnly,
            MinecraftRepairActionKind.RepairVersionFiles,
            MinecraftRepairActionKind.ReextractNatives,
            MinecraftRepairActionKind.InstallMissingModDependencies,
            MinecraftRepairActionKind.DownloadMod,
            MinecraftRepairActionKind.DisableMod,
            MinecraftRepairActionKind.UpdateMod,
            MinecraftRepairActionKind.ReadModMetadata,
            MinecraftRepairActionKind.SelectCompatibleJava,
            MinecraftRepairActionKind.DownloadCompatibleJava,
            MinecraftRepairActionKind.ReinstallVersionAndUpdateLoader
        ]
    };

    private static string[] NormalizeEvidence(IEnumerable<string>? evidence) => evidence?
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => Truncate(line.Trim(), 2_048) ?? string.Empty)
        .Where(line => line.Length > 0)
        .TakeLast(200)
        .ToArray() ?? [];

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAll(string text, params string[] values) =>
        values.All(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static string? Truncate(string? value, int maximumLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maximumLength ? value : value[..maximumLength];
}
