using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PCL.Services.Logging;

namespace PCL.Services.Minecraft.Java;

public sealed record JavaRuntimeInstallProgress(
    string Stage,
    double Progress,
    int CompletedFiles,
    int TotalFiles,
    string? Detail = null);

/// <summary>Acquisition seam used by launch orchestration and deterministic tests.</summary>
public interface IJavaRuntimeInstaller
{
    Task<string> InstallAsync(
        string requestedComponent,
        string runtimeRootDirectory,
        IProgress<JavaRuntimeInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Installs a planned Mojang runtime with resumable, hash-verified file replacement.
/// The installer owns no global state and can therefore be hosted by a command handler.
/// </summary>
public sealed class JavaRuntimeInstaller : IJavaRuntimeInstaller, IDisposable
{
    private readonly JavaRuntimeDownloadPlanService _planService;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly LogService? _log;

    public JavaRuntimeInstaller(IJavaRuntimeMetadataProvider metadataProvider, LogService? log = null)
        : this(
            new JavaRuntimeDownloadPlanService(metadataProvider),
            new HttpClient(log is null ? new HttpClientHandler() : new DiagnosticHttpHandler(log, new HttpClientHandler())) { Timeout = TimeSpan.FromMinutes(10) },
            ownsHttpClient: true, log)
    {
    }

    public JavaRuntimeInstaller(JavaRuntimeDownloadPlanService planService, HttpClient httpClient, bool ownsHttpClient = false, LogService? log = null)
    {
        _planService = planService ?? throw new ArgumentNullException(nameof(planService));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
        _log = log;
    }

    public async Task<string> InstallAsync(
        string requestedComponent,
        string runtimeRootDirectory,
        IProgress<JavaRuntimeInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedComponent);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRootDirectory);

        using LogOperation? operation = _log?.BeginOperation("Java", "InstallRuntime", $"component={requestedComponent}");
        string? currentFile = null;
        try
        {
            operation?.Stage("resolve_runtime_metadata");
            JavaRuntimeDownloadPlan plan = await _planService.CreatePlanAsync(
                requestedComponent,
                DetectPlatform(),
                runtimeRootDirectory,
                cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(plan.TargetDirectory);

            int total = Math.Max(plan.Files.Count, 1);
            int completed = 0;
            progress?.Report(new JavaRuntimeInstallProgress("prepare", 0.02d, 0, total, plan.VersionName));

            operation?.Stage("verify_and_download_files", $"count={plan.Files.Count} target={plan.TargetDirectory}");
            foreach (JavaRuntimeDownloadFile file in plan.Files)
            {
                currentFile = file.RelativePath;
                _log?.Trace("Java", $"Runtime file verification path={file.RelativePath} expected_bytes={file.Size}");
                cancellationToken.ThrowIfCancellationRequested();
                string? parent = Path.GetDirectoryName(file.TargetPath);
                if (parent is null) throw new InvalidOperationException($"Runtime file has no parent directory: {file.RelativePath}");
                Directory.CreateDirectory(parent);

                if (File.Exists(file.TargetPath) && await MatchesAsync(file.TargetPath, file, cancellationToken).ConfigureAwait(false))
                {
                    _log?.Trace("Java", $"Reusing verified runtime file path={file.RelativePath}");
                    ApplyExecutableMode(file);
                }
                else
                {
                    await DownloadFileAsync(file, cancellationToken).ConfigureAwait(false);
                }

                completed++;
                progress?.Report(new JavaRuntimeInstallProgress(
                    "download",
                    0.05d + 0.9d * completed / total,
                    completed,
                    total,
                    file.RelativePath));
            }

            operation?.Stage("locate_installed_executable");
            string? javaExecutable = FindJavaExecutable(plan.TargetDirectory);
            if (javaExecutable is null)
                throw new InvalidOperationException($"Java runtime was installed but no java executable was found in '{plan.TargetDirectory}'.");
            progress?.Report(new JavaRuntimeInstallProgress("complete", 1d, total, total, javaExecutable));
            operation?.Complete($"files={completed} executable={javaExecutable}");
            return javaExecutable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation?.Cancel();
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            _log?.Warn("Java", $"Runtime installation failed current_file={currentFile}");
            operation?.Fail(exception);
            throw;
        }
    }

    public static JavaRuntimePlatform DetectPlatform()
    {
        JavaRuntimeOperatingSystem operatingSystem = OperatingSystem.IsWindows()
            ? JavaRuntimeOperatingSystem.Win32
            : OperatingSystem.IsMacOS()
                ? JavaRuntimeOperatingSystem.MacOs
                : JavaRuntimeOperatingSystem.Linux;
        JavaRuntimeArchitecture architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X86 => JavaRuntimeArchitecture.X86,
            Architecture.Arm64 => JavaRuntimeArchitecture.Arm64,
            _ => JavaRuntimeArchitecture.X64,
        };
        return new JavaRuntimePlatform(operatingSystem, architecture);
    }

    public static string GetDefaultRuntimeRoot(string applicationDataDirectory) =>
        JavaRuntimePackagePlannerRoot(applicationDataDirectory);

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private static string JavaRuntimePackagePlannerRoot(string applicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        return Path.Combine(Path.GetFullPath(applicationDataDirectory), "PCL-N", "runtime");
    }

    private static async Task<bool> MatchesAsync(string path, JavaRuntimeDownloadFile file, CancellationToken cancellationToken)
    {
        if (file.Size >= 0 && new FileInfo(path).Length != file.Size) return false;
        if (string.IsNullOrWhiteSpace(file.Sha1)) return true;
        string actual = await ComputeSha1Async(path, cancellationToken).ConfigureAwait(false);
        return string.Equals(actual, file.Sha1, StringComparison.OrdinalIgnoreCase);
    }

    private async Task DownloadFileAsync(JavaRuntimeDownloadFile file, CancellationToken cancellationToken)
    {
        _log?.Debug("Java", $"Downloading runtime file path={file.RelativePath}");
        using HttpResponseMessage response = await _httpClient.GetAsync(
            file.Url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string temporary = file.TargetPath + ".download";
        try
        {
            await using (Stream network = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (FileStream output = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await network.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!await MatchesAsync(temporary, file, cancellationToken).ConfigureAwait(false))
            {
                _log?.Warn("Java", $"Runtime integrity check failed path={file.RelativePath} expected_bytes={file.Size}");
                throw new InvalidOperationException($"Java runtime file hash or size mismatch: {file.RelativePath}");
            }
            File.Move(temporary, file.TargetPath, overwrite: true);
            ApplyExecutableMode(file);
            temporary = string.Empty;
        }
        finally
        {
            if (temporary.Length > 0)
            {
                try { File.Delete(temporary); } catch (IOException) { }
            }
        }
    }

    private static async Task<string> ComputeSha1Async(string path, CancellationToken cancellationToken)
    {
#pragma warning disable CA5350 // Mojang runtime manifests publish SHA-1 digests.
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
#pragma warning restore CA5350
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ApplyExecutableMode(JavaRuntimeDownloadFile file)
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()) || !file.Executable) return;
        try
        {
            UnixFileMode current = File.GetUnixFileMode(file.TargetPath);
            UnixFileMode executable = current | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if (executable != current) File.SetUnixFileMode(file.TargetPath, executable);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Could not mark Java runtime file executable: {file.RelativePath}", exception);
        }
    }

    private static string? FindJavaExecutable(string root)
    {
        string executableName = OperatingSystem.IsWindows() ? "java.exe" : "java";
        try
        {
            return Directory.EnumerateFiles(root, executableName, SearchOption.AllDirectories)
                .OrderBy(path => path.Length)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
