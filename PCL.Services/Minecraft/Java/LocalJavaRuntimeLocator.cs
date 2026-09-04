using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using PCL.Services.Logging;

namespace PCL.Services.Minecraft.Java;

/// <summary>
/// Finds launcher-installed and environment Java runtimes and probes their actual properties.
/// Every returned candidate names an existing absolute executable; the launch coordinator never
/// relies on a bare <c>java</c> command or an assumed major version.
/// </summary>
public sealed class LocalJavaRuntimeLocator : IJavaRuntimeLocator
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    private readonly string? _launcherRuntimeRoot;
    private readonly LogService? _log;

    public LocalJavaRuntimeLocator(string? launcherRuntimeRoot = null, LogService? log = null)
    {
        _log = log;
        _launcherRuntimeRoot = string.IsNullOrWhiteSpace(launcherRuntimeRoot)
            ? null
            : Path.GetFullPath(launcherRuntimeRoot);
    }

    public async ValueTask<IReadOnlyList<JavaRuntimeCandidate>> FindAllAsync(
        CancellationToken cancellationToken = default)
    {
        HashSet<string> paths = new(GetPathComparer());
        AddLauncherRuntimes(paths);
        AddJavaHome(paths);
        AddPathRuntimes(paths);
        _log?.Info("Java", $"Runtime discovery started executable_candidates={paths.Count}");

        List<JavaRuntimeCandidate> candidates = [];
        foreach (string path in paths.OrderBy(static path => path, GetPathComparer()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            JavaRuntimeCandidate? candidate = await InspectAsync(path, cancellationToken)
                .ConfigureAwait(false);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        _log?.Info("Java", $"Runtime discovery completed usable_candidates={candidates.Count}");
        return candidates;
    }

    public async ValueTask<JavaRuntimeCandidate?> InspectAsync(
        string javaExecutablePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(javaExecutablePath))
        {
            return null;
        }

        string executable;
        try
        {
            executable = Path.GetFullPath(javaExecutablePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }

        if (!File.Exists(executable))
        {
            _log?.Debug("Java", $"Java probe skipped; executable is absent path={executable}");
            return null;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        using System.Diagnostics.Process process = new()
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-XshowSettings:properties");
        process.StartInfo.ArgumentList.Add("-version");

        try
        {
            _log?.Debug("Java", $"Java probe started executable={executable}");
            if (!process.Start())
            {
                return null;
            }

            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string properties = string.Concat(
                await output.ConfigureAwait(false),
                Environment.NewLine,
                await error.ConfigureAwait(false));
            if (TryCreateCandidate(executable, properties, out JavaRuntimeCandidate? candidate))
            {
                _log?.Debug("Java", $"Java probe completed executable={executable} major={candidate!.Installation.MajorVersion}");
                return candidate;
            }
            _log?.Warn("Java", $"Java probe returned unrecognized version properties executable={executable} exit_code={process.ExitCode}");
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            _log?.Warn("Java", $"Java probe timed out executable={executable} timeout_seconds=8");
            return null;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            _log?.Write(LogLevel.Warn, "Java", $"Java probe failed executable={executable}", ExceptionDiagnostics.Describe(exception));
            return null;
        }
    }

    private void AddLauncherRuntimes(HashSet<string> paths)
    {
        if (_launcherRuntimeRoot is null || !Directory.Exists(_launcherRuntimeRoot))
        {
            return;
        }

        try
        {
            foreach (string path in Directory.EnumerateFiles(
                         _launcherRuntimeRoot,
                         JavaExecutableName(),
                         SearchOption.AllDirectories))
            {
                _ = paths.Add(Path.GetFullPath(path));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log?.Write(LogLevel.Warn, "Java", "Launcher runtime enumeration was incomplete; continuing with environment candidates.", ExceptionDiagnostics.Describe(exception));
            // A partially inaccessible runtime folder must not hide usable PATH/JAVA_HOME entries.
        }
    }

    private static void AddJavaHome(HashSet<string> paths)
    {
        string? home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            return;
        }

        AddIfFile(paths, Path.Combine(home, "bin", JavaExecutableName()));
    }

    private static void AddPathRuntimes(HashSet<string> paths)
    {
        string? value = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (string directory in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                AddIfFile(paths, Path.Combine(directory.Trim(), JavaExecutableName()));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                // Ignore malformed process-environment entries and continue with valid siblings.
            }
        }
    }

    private static void AddIfFile(HashSet<string> paths, string path)
    {
        if (File.Exists(path))
        {
            _ = paths.Add(Path.GetFullPath(path));
        }
    }

    private static bool TryCreateCandidate(
        string executable,
        string properties,
        out JavaRuntimeCandidate? candidate)
    {
        candidate = null;
        string? rawVersion = ReadProperty(properties, "java.version") ?? ReadQuotedVersion(properties);
        if (!TryParseJavaVersion(rawVersion, out Version? version))
        {
            return false;
        }

        string? declaredHome = ReadProperty(properties, "java.home");
        string home = !string.IsNullOrWhiteSpace(declaredHome) && Directory.Exists(declaredHome)
            ? Path.GetFullPath(declaredHome)
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(executable)!, ".."));
        string? architectureText = ReadProperty(properties, "os.arch");
        JavaArchitecture architecture = ParseArchitecture(architectureText);
        bool is64Bit = architecture is JavaArchitecture.X64 or JavaArchitecture.Arm64;
        string? javaw = OperatingSystem.IsWindows()
            ? Path.Combine(Path.GetDirectoryName(executable)!, "javaw.exe")
            : null;
        string? runtimeName = ReadProperty(properties, "java.runtime.name");
        bool isJre = !File.Exists(Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "javac.exe" : "javac"))
            || runtimeName?.Contains("Runtime", StringComparison.OrdinalIgnoreCase) == true;
        JavaInstallation installation = new(
            home,
            executable,
            javaw is not null && File.Exists(javaw) ? javaw : null,
            version!,
            ParseBrand(ReadProperty(properties, "java.vendor")),
            architecture,
            is64Bit,
            isJre);
        candidate = new JavaRuntimeCandidate(installation, Source: JavaSource.AutoScanned);
        return true;
    }

    private static string? ReadProperty(string text, string name)
    {
        foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            int separator = trimmed.IndexOf('=');
            if (separator > 0
                && string.Equals(trimmed[..separator].Trim(), name, StringComparison.Ordinal))
            {
                string value = trimmed[(separator + 1)..].Trim();
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    private static string? ReadQuotedVersion(string text)
    {
        int marker = text.IndexOf("version \"", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return null;
        }

        int start = marker + "version \"".Length;
        int end = text.IndexOf('"', start);
        return end > start ? text[start..end] : null;
    }

    private static bool TryParseJavaVersion(string? raw, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string value = raw.Trim();
        int length = 0;
        while (length < value.Length
               && (char.IsDigit(value[length]) || value[length] is '.' or '_'))
        {
            length++;
        }

        string normalized = value[..length].Replace('_', '.');
        while (normalized.Length > 0 && normalized[^1] == '.')
        {
            normalized = normalized[..^1];
        }

        int componentCount = normalized.Count(static character => character == '.') + 1;
        normalized = componentCount switch
        {
            1 => normalized + ".0",
            > 4 => string.Join('.', normalized.Split('.')[..4]),
            _ => normalized,
        };
        return Version.TryParse(normalized, out version);
    }

    private static JavaArchitecture ParseArchitecture(string? value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "amd64" or "x86_64" or "x64" => JavaArchitecture.X64,
            "x86" or "i386" or "i486" or "i586" or "i686" => JavaArchitecture.X86,
            "aarch64" or "arm64" => JavaArchitecture.Arm64,
            _ when normalized.StartsWith("arm", StringComparison.Ordinal) => JavaArchitecture.Arm,
            _ => JavaArchitecture.Unknown,
        };
    }

    private static JavaBrand ParseBrand(string? value)
    {
        string normalized = value?.ToLowerInvariant() ?? string.Empty;
        if (normalized.Contains("temurin", StringComparison.Ordinal) || normalized.Contains("adoptium", StringComparison.Ordinal)) return JavaBrand.EclipseTemurin;
        if (normalized.Contains("microsoft", StringComparison.Ordinal)) return JavaBrand.Microsoft;
        if (normalized.Contains("zulu", StringComparison.Ordinal) || normalized.Contains("azul", StringComparison.Ordinal)) return JavaBrand.Zulu;
        if (normalized.Contains("liberica", StringComparison.Ordinal) || normalized.Contains("bellsoft", StringComparison.Ordinal)) return JavaBrand.Liberica;
        if (normalized.Contains("corretto", StringComparison.Ordinal) || normalized.Contains("amazon", StringComparison.Ordinal)) return JavaBrand.Corretto;
        if (normalized.Contains("oracle", StringComparison.Ordinal)) return JavaBrand.Oracle;
        if (normalized.Contains("jetbrains", StringComparison.Ordinal)) return JavaBrand.JetBrains;
        return normalized.Contains("openjdk", StringComparison.Ordinal) ? JavaBrand.OpenJdk : JavaBrand.Unknown;
    }

    private static string JavaExecutableName() => OperatingSystem.IsWindows() ? "java.exe" : "java";

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void TryKill(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }
}
