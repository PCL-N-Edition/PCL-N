using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Nexa.Services.Logging;

namespace Nexa.Services.Minecraft.Java;

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
        // Executables, not homes: a resolved root may be a real Java home (bin/java) OR a
        // direct-executable directory (Oracle javapath shims). Collapsing both into "home +
        // /bin/java" dropped every shim the earlier stage had just accepted.
        HashSet<string> executables = new(GetPathComparer());
        foreach (JavaSearchRoot root in EnumerateSearchRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (root.ExpandChildren)
            {
                foreach (string home in ExpandRoot(root.Path, cancellationToken))
                {
                    if (ResolveJavaExecutable(home) is { } homeExecutable)
                    {
                        executables.Add(homeExecutable);
                    }
                }
            }
            else if (ResolveJavaExecutable(root.Path) is { } directExecutable)
            {
                executables.Add(directExecutable);
            }
        }

        List<string> ordered = executables
            .ToList();
        ordered.Sort(GetPathComparer());
        _log?.Info("Java", $"Runtime discovery started executables={ordered.Count}");

        List<JavaRuntimeCandidate> candidates = [];
        foreach (string path in ordered)
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

    /// <summary>The search roots in legacy order: launcher runtime, environment, registry, vendor trees.</summary>
    internal IEnumerable<JavaSearchRoot> EnumerateSearchRoots()
    {
        if (_launcherRuntimeRoot is not null)
        {
            yield return new JavaSearchRoot(_launcherRuntimeRoot, ExpandChildren: true);
        }

        string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            yield return new JavaSearchRoot(javaHome, ExpandChildren: false);
        }

        foreach (string pathEntry in EnumeratePathEntries())
        {
            // PATH entries are executable lookup directories, not installation roots.
            yield return new JavaSearchRoot(pathEntry, ExpandChildren: false);
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (string home in WindowsRegistryJavaHomes.EnumerateHomes())
            {
                yield return new JavaSearchRoot(home, ExpandChildren: false);
            }

            foreach (string root in EnumerateExisting(
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                         @"D:\Program Files",
                         @"D:\Program Files (x86)",
                         @"C:\Program Files",
                         @"C:\Program Files (x86)",
                         @"C:\Java",
                         @"D:\Java"))
            {
                foreach (string vendor in VendorFolderNames())
                {
                    yield return new JavaSearchRoot(Path.Combine(root, vendor), ExpandChildren: true);
                }

                yield return new JavaSearchRoot(
                    Path.Combine(root, "Common Files", "Oracle", "Java"), ExpandChildren: true);
                // Scoop / portable layouts often live under the user profile.
                yield return new JavaSearchRoot(Path.Combine(root, "scoop", "apps", "temurin-jdk", "current"), ExpandChildren: false);
                yield return new JavaSearchRoot(Path.Combine(root, "scoop", "apps", "zulu-jdk", "current"), ExpandChildren: false);
                yield return new JavaSearchRoot(Path.Combine(root, "scoop", "apps", "graalvm-jdk", "current"), ExpandChildren: false);
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (string root in EnumerateExisting(
                         "/Library/Java/JavaVirtualMachines",
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Java", "JavaVirtualMachines"),
                         "/opt/homebrew/opt/openjdk",
                         "/usr/local/opt/openjdk",
                         "/opt/homebrew/opt/openjdk@21",
                         "/opt/homebrew/opt/openjdk@17",
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sdkman", "candidates", "java")))
            {
                yield return new JavaSearchRoot(root, ExpandChildren: true);
            }
        }
        else
        {
            foreach (string root in EnumerateExisting(
                         "/usr/lib/jvm",
                         "/usr/java",
                         "/opt/java",
                         "/opt/jdk",
                         "/opt/graalvm",
                         "/usr/lib/jvm/zulu-openjdk",
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sdkman", "candidates", "java"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jdks")))
            {
                yield return new JavaSearchRoot(root, ExpandChildren: true);
            }
        }
    }

    private static IEnumerable<string> VendorFolderNames()
    {
        return
        [
            "Java",
            "Eclipse Adoptium",
            "AdoptOpenJDK",
            "Microsoft",
            "Zulu",
            "Azul",
            "zulu",
            "BellSoft",
            "Amazon Corretto",
            "GraalVM",
            "graalvm",
            "Liberica",
            "Semeru",
            "Dragonwell",
        ];
    }

    private static IEnumerable<string> EnumeratePathEntries()
    {
        string? value = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (string directory in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = directory.Trim();
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }

    private static IEnumerable<string> EnumerateExisting(params string[] paths)
    {
        foreach (string path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                yield return path;
            }
        }
    }

    /// <summary>
    /// Legacy expand semantics: the root itself may be a home; otherwise one level of children
    /// is checked, with a single extra level for vendor trees like
    /// <c>Program Files\Eclipse Adoptium\jdk-17.0.x-hotspot</c>. A resolved home is never
    /// descended into — its bin/lib/legal trees can only dominate the scan time.
    /// </summary>
    internal static IEnumerable<string> ExpandRoot(string root, CancellationToken cancellationToken)
    {
        string normalizedRoot;
        try
        {
            normalizedRoot = Path.GetFullPath(root);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            yield break;
        }

        if (ResolveJavaHome(normalizedRoot) is { } directHome)
        {
            yield return directHome;
            yield break;
        }

        if (!Directory.Exists(normalizedRoot))
        {
            yield break;
        }

        foreach (string child in SafeEnumerateDirectories(normalizedRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ResolveJavaHome(child) is { } childHome)
            {
                yield return childHome;
                continue;
            }

            foreach (string grandChild in SafeEnumerateDirectories(child))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ResolveJavaHome(grandChild) is { } grandChildHome)
                {
                    yield return grandChildHome;
                }
            }
        }
    }

    /// <summary>
    /// Resolves a search path to a Java home: an executable path ascends out of <c>bin</c>,
    /// a macOS bundle resolves through <c>Contents/Home</c>, and a directory is the home
    /// itself (the probe verifies it actually runs).
    /// </summary>
    /// <summary>
    /// Resolves a search path to the final executable to probe: the path itself when it is a
    /// java executable, <c>bin/java</c> inside a real home, the executable sitting directly in
    /// the directory (Oracle javapath shims), or the macOS bundle layout. Returns null when
    /// nothing runnable exists here.
    /// </summary>
    internal static string? ResolveJavaExecutable(string path)
    {
        if (File.Exists(path) && IsJavaExecutableName(Path.GetFileName(path)))
        {
            return Path.GetFullPath(path);
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        string normal = Path.Combine(path, "bin", JavaExecutableName());
        if (File.Exists(normal))
        {
            return Path.GetFullPath(normal);
        }

        string direct = Path.Combine(path, JavaExecutableName());
        if (File.Exists(direct))
        {
            return Path.GetFullPath(direct);
        }

        string mac = Path.Combine(path, "Contents", "Home", "bin", JavaExecutableName());
        return File.Exists(mac) ? Path.GetFullPath(mac) : null;
    }

    internal static string? ResolveJavaHome(string path)
    {
        if (File.Exists(path) && IsJavaExecutableName(Path.GetFileName(path)))
        {
            string? directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }

            // .../bin/java(.exe) — the home is the parent of bin.
            if (string.Equals(Path.GetFileName(directory), "bin", StringComparison.OrdinalIgnoreCase))
            {
                string? home = Directory.GetParent(directory)?.FullName;
                if (!string.IsNullOrWhiteSpace(home) && File.Exists(Path.Combine(home, "bin", JavaExecutableName())))
                {
                    return Path.GetFullPath(home);
                }
            }

            // Oracle javapath shim directory: keep it; the probe decides by running -version.
            return Path.GetFullPath(directory);
        }

        if (Directory.Exists(path))
        {
            // A directory only counts as a home when its executable actually exists —
            // vendor parent folders (e.g. `Eclipse Adoptium`) must return null so the scan
            // keeps descending instead of adopting them as homes.
            string macBundleHome = Path.Combine(path, "Contents", "Home");
            if (Directory.Exists(macBundleHome)
                && File.Exists(Path.Combine(macBundleHome, "bin", JavaExecutableName())))
            {
                return Path.GetFullPath(macBundleHome);
            }

            if (File.Exists(Path.Combine(path, "bin", JavaExecutableName())))
            {
                return Path.GetFullPath(path);
            }

            // A PATH entry pointing directly at a bin/ directory.
            string parent = Directory.GetParent(path)?.FullName ?? string.Empty;
            if (string.Equals(Path.GetFileName(path), "bin", StringComparison.OrdinalIgnoreCase)
                && parent.Length > 0
                && File.Exists(Path.Combine(path, JavaExecutableName())))
            {
                return Path.GetFullPath(parent);
            }

            // A directory that directly contains the executable (Oracle javapath shim).
            if (File.Exists(Path.Combine(path, JavaExecutableName())))
            {
                return Path.GetFullPath(path);
            }
        }

        return null;
    }

    internal readonly record struct JavaSearchRoot(string Path, bool ExpandChildren);

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _ = exception;
            return [];
        }
    }

    private static bool IsJavaExecutableName(string fileName) =>
        fileName.Equals(JavaExecutableName(), StringComparison.OrdinalIgnoreCase);

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

    internal static bool TryCreateCandidate(
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
        // A JDK is identified by its compiler: every OpenJDK build's runtime.name is
        // "OpenJDK Runtime Environment" — even for full JDKs — so the name can never decide
        // this; javac on disk can.
        bool isJre = !File.Exists(Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "javac.exe" : "javac"));
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
