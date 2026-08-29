namespace PCL.Services.Minecraft.Crash;

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
    AccessDenied,
}

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
    DisableExperimentalJvmHost,
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
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public IReadOnlyList<MinecraftRepairActionKind> AllowedActions { get; init; } = [MinecraftRepairActionKind.InspectOnly];
}

public sealed record MinecraftMissingDependency(string Name, string ModId, string? RequiredVersion);

public static class MinecraftMissingDependencyParser
{
    public static IReadOnlyList<MinecraftMissingDependency> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        List<MinecraftMissingDependency> result = [];
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Contains("requires mod", StringComparison.OrdinalIgnoreCase) && line.Contains("missing", StringComparison.OrdinalIgnoreCase))
            {
                int firstQuote = line.IndexOf('\'');
                int secondQuote = firstQuote < 0 ? -1 : line.IndexOf('\'', firstQuote + 1);
                int open = secondQuote < 0 ? -1 : line.IndexOf('(', secondQuote);
                int close = open < 0 ? -1 : line.IndexOf(')', open);
                if (firstQuote >= 0 && secondQuote > firstQuote && open > secondQuote && close > open)
                {
                    string modId = line[(open + 1)..close];
                    string? required = ReadVersion(line, close + 1);
                    Add(result, new MinecraftMissingDependency(line[(firstQuote + 1)..secondQuote], modId, required));
                }
            }
            else if (line.Contains("any version", StringComparison.OrdinalIgnoreCase) && line.Contains("missing", StringComparison.OrdinalIgnoreCase))
            {
                int requires = line.IndexOf("requires", StringComparison.OrdinalIgnoreCase);
                int anyVersion = line.IndexOf("any version", StringComparison.OrdinalIgnoreCase);
                string before = line[..anyVersion];
                int open = before.LastIndexOf('(');
                int close = before.LastIndexOf(')');
                if (open >= 0 && close > open) Add(result, new MinecraftMissingDependency(before[..open].Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty, before[(open + 1)..close], null));
                else
                {
                    string after = requires < 0 ? string.Empty : line[(requires + "requires".Length)..anyVersion].Trim();
                    string modId = after.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
                    Add(result, new MinecraftMissingDependency(modId, modId, null));
                }
            }
            else if (line.Contains("requires version", StringComparison.OrdinalIgnoreCase))
            {
                int requires = line.IndexOf("requires version", StringComparison.OrdinalIgnoreCase);
                string[] tail = line[(requires + 16)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tail.Length >= 2) Add(result, new MinecraftMissingDependency(tail[^1], tail[^1], tail[0]));
            }
            else if (line.Contains("需要模组", StringComparison.OrdinalIgnoreCase) && line.Contains("及以上版本", StringComparison.OrdinalIgnoreCase))
            {
                int open = line.IndexOf('(');
                int close = open < 0 ? -1 : line.IndexOf(')', open);
                int versionStart = line.IndexOf('的', close < 0 ? 0 : close);
                int versionEnd = line.IndexOf('及', versionStart < 0 ? 0 : versionStart);
                if (open >= 0 && close > open && versionStart >= 0 && versionEnd > versionStart)
                    Add(result, new MinecraftMissingDependency(line[open..close].Trim('(', ')'), line[(open + 1)..close], line[(versionStart + 1)..versionEnd].Trim()));
            }
        }

        return result;
    }

    private static string? ReadVersion(string line, int start)
    {
        string tail = line[start..];
        int version = tail.IndexOf("version", StringComparison.OrdinalIgnoreCase);
        if (version < 0) return null;
        string value = tail[(version + 7)..].Trim();
        string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 || parts[0].Equals("any", StringComparison.OrdinalIgnoreCase) ? null : parts[0];
    }
    private static void Add(List<MinecraftMissingDependency> result, MinecraftMissingDependency dependency)
    {
        if (dependency.ModId.Length == 0 || result.Any(item => string.Equals(item.ModId, dependency.ModId, StringComparison.OrdinalIgnoreCase) && item.RequiredVersion == dependency.RequiredVersion)) return;
        result.Add(dependency);
    }
}

public static class MinecraftLaunchFaultAnalyzer
{
    public static MinecraftLaunchFaultReport Analyze(Exception exception, string? stage = null, string? lastClassName = null, IEnumerable<string>? additionalEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string normalizedStage = string.IsNullOrWhiteSpace(stage) ? "Unknown" : stage.Trim();
        string text = string.Join('\n', new[] { exception.GetType().FullName, exception.Message, exception.StackTrace, lastClassName, normalizedStage }.Concat(additionalEvidence ?? []));
        MinecraftLaunchFaultCode code = Classify(text, normalizedStage);
        return Create(code, normalizedStage, exception.GetType().FullName ?? exception.GetType().Name, exception.Message, exception.StackTrace, lastClassName, additionalEvidence);
    }

    public static MinecraftLaunchFaultReport AnalyzeText(IEnumerable<string> evidence, string? stage = null, string? lastClassName = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        string[] normalized = evidence.Where(static line => !string.IsNullOrWhiteSpace(line)).Select(static line => line.Trim().Length > 2048 ? line.Trim()[..2048] : line.Trim()).TakeLast(200).ToArray();
        MinecraftLaunchFaultCode code = Classify(string.Join('\n', normalized.Concat([lastClassName ?? string.Empty])), stage ?? "GameProcess");
        return Create(code, string.IsNullOrWhiteSpace(stage) ? "GameProcess" : stage!, string.Empty, normalized.LastOrDefault() ?? "Minecraft process exited unexpectedly.", null, lastClassName, normalized);
    }

    private static MinecraftLaunchFaultReport Create(MinecraftLaunchFaultCode code, string stage, string exceptionType, string message, string? stack, string? lastClass, IEnumerable<string>? evidence) => new()
    {
        Code = code,
        Stage = stage,
        Subsystem = GetSubsystem(code, lastClass),
        ExceptionType = exceptionType,
        Message = message,
        StackTrace = stack is null || stack.Length <= 16384 ? stack : stack[..16384],
        LastClassName = string.IsNullOrWhiteSpace(lastClass) ? null : lastClass,
        Evidence = (evidence ?? []).Where(static line => !string.IsNullOrWhiteSpace(line)).Select(static line => line.Trim()).TakeLast(200).ToArray(),
        AllowedActions = GetActions(code),
    };

    private static MinecraftLaunchFaultCode Classify(string text, string stage)
    {
        if (Any(text, "being used by another process", "sharing violation", "另一个进程正在使用")) return MinecraftLaunchFaultCode.FileLocked;
        if (Any(text, "access is denied", "unauthorizedaccessexception", "permission denied", "拒绝访问")) return MinecraftLaunchFaultCode.AccessDenied;
        if (Any(text, "unsupportedclassversionerror", "class file version", "open j9 is not supported", "module java.base does not export", "invalid maximum heap size")) return MinecraftLaunchFaultCode.JavaRuntimeIncompatible;
        if (Any(text, "outofmemoryerror", "java heap space", "could not reserve enough space")) return MinecraftLaunchFaultCode.OutOfMemory;
        if (Any(text, "could not find or load main class", "mainclassmissing", "找不到或无法加载主类")) return MinecraftLaunchFaultCode.MainClassMissing;
        if (Any(text, "missing mandatory dependencies", "requires version", "requires any version", "依赖模组")) return MinecraftLaunchFaultCode.MissingModDependency;
        if (Any(text, "noclassdeffounderror", "classnotfoundexception", "no such file or directory") && !stage.Contains("JvmStarting", StringComparison.OrdinalIgnoreCase)) return MinecraftLaunchFaultCode.ClasspathDependencyMissing;
        if (Any(text, "mixin apply failed", "mod loading has failed", "extracted mod jars", "mod conflict")) return MinecraftLaunchFaultCode.ModConflict;
        if (Any(text, "cannot find launch target fmlclient", "fabricloader", "modlauncher", "quiltloader", "neoforged", "minecraftforge") && Any(text, "bootstrap", "failed", "exception", "error")) return MinecraftLaunchFaultCode.ModLoaderBootstrapFailed;
        if (Any(text, "unsatisfiedlinkerror", "failed to load library", "java.library.path", "natives")) return MinecraftLaunchFaultCode.NativeLibraryFailed;
        if (Any(text, "glfw error", "failed to create window", "opengl", "vulkan", "pixel format")) return MinecraftLaunchFaultCode.GraphicsInitializationFailed;
        if (Any(text, "invalid credentials", "invalid token", "authenticationexception", "http 401", "http 403", "重新连接账户")) return MinecraftLaunchFaultCode.AuthenticationFailed;
        if (Any(text, "sessionserver", "hasjoined", "joinserver", "http 503") && Any(text, "session", "auth", "profile")) return MinecraftLaunchFaultCode.SessionServiceUnavailable;
        if (Any(text, "jvm.dll", "libjvm.so", "libjvm.dylib", "找不到 java")) return MinecraftLaunchFaultCode.JavaRuntimeMissing;
        if (stage.Contains("JvmStarting", StringComparison.OrdinalIgnoreCase) || Any(text, "createjavavm", "jni_err", "jvmti")) return MinecraftLaunchFaultCode.JvmInitializationFailed;
        return MinecraftLaunchFaultCode.Unknown;
    }

    private static string GetSubsystem(MinecraftLaunchFaultCode code, string? lastClass) => code switch
    {
        MinecraftLaunchFaultCode.JavaRuntimeMissing or MinecraftLaunchFaultCode.JavaRuntimeIncompatible or MinecraftLaunchFaultCode.JvmInitializationFailed => "JVM",
        MinecraftLaunchFaultCode.AuthenticationFailed or MinecraftLaunchFaultCode.SessionServiceUnavailable => "Authentication",
        MinecraftLaunchFaultCode.NativeLibraryFailed => "NativeRuntime",
        MinecraftLaunchFaultCode.GraphicsInitializationFailed => "Graphics",
        MinecraftLaunchFaultCode.ModLoaderBootstrapFailed or MinecraftLaunchFaultCode.ModConflict or MinecraftLaunchFaultCode.MissingModDependency => "ModLoader",
        MinecraftLaunchFaultCode.FileLocked or MinecraftLaunchFaultCode.AccessDenied => "FileSystem",
        _ when lastClass?.Contains("lwjgl", StringComparison.OrdinalIgnoreCase) == true => "NativeRuntime",
        _ => "Minecraft",
    };

    private static IReadOnlyList<MinecraftRepairActionKind> GetActions(MinecraftLaunchFaultCode code) => code switch
    {
        MinecraftLaunchFaultCode.MainClassMissing or MinecraftLaunchFaultCode.ClasspathDependencyMissing => [MinecraftRepairActionKind.RepairVersionFiles, MinecraftRepairActionKind.ReinstallVersionAndUpdateLoader],
        MinecraftLaunchFaultCode.NativeLibraryFailed => [MinecraftRepairActionKind.ReextractNatives, MinecraftRepairActionKind.RepairVersionFiles],
        MinecraftLaunchFaultCode.MissingModDependency => [MinecraftRepairActionKind.InstallMissingModDependencies, MinecraftRepairActionKind.DownloadMod, MinecraftRepairActionKind.ReadModMetadata],
        MinecraftLaunchFaultCode.JavaRuntimeMissing or MinecraftLaunchFaultCode.JavaRuntimeIncompatible => [MinecraftRepairActionKind.SelectCompatibleJava, MinecraftRepairActionKind.DownloadCompatibleJava],
        MinecraftLaunchFaultCode.AuthenticationFailed or MinecraftLaunchFaultCode.SessionServiceUnavailable => [MinecraftRepairActionKind.RefreshAccount, MinecraftRepairActionKind.DisableExperimentalJvmHost],
        MinecraftLaunchFaultCode.OutOfMemory => [MinecraftRepairActionKind.ReduceMemoryPressure],
        MinecraftLaunchFaultCode.ModConflict or MinecraftLaunchFaultCode.ModLoaderBootstrapFailed => [MinecraftRepairActionKind.ReadModMetadata, MinecraftRepairActionKind.DisableMod, MinecraftRepairActionKind.UpdateMod, MinecraftRepairActionKind.ReviewModSet],
        MinecraftLaunchFaultCode.JvmInitializationFailed => [MinecraftRepairActionKind.DisableExperimentalJvmHost, MinecraftRepairActionKind.SelectCompatibleJava],
        _ => [MinecraftRepairActionKind.InspectOnly],
    };

    private static bool Any(string text, params string[] values) => values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
}
