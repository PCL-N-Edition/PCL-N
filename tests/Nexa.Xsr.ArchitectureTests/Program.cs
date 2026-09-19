using System.Xml.Linq;

namespace Nexa.Xsr.ArchitectureTests;

internal static class Program
{
    private static readonly IReadOnlyDictionary<string, string[]> AllowedReferences =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Nexa.Core"] = [],
            ["Nexa.Domain"] = ["Nexa.Core"],
            ["Nexa.Contracts"] = ["Nexa.Core"],
            ["Nexa.Xsr.Abstractions"] = ["Nexa.Contracts", "Nexa.Core"],
            ["Nexa.Xsr.State"] = ["Nexa.Core", "Nexa.Xsr.Abstractions"],
            ["Nexa.Xsr.Diagnostics"] = ["Nexa.Core", "Nexa.Xsr.Abstractions"],
            ["Nexa.Xsr.Transport"] = ["Nexa.Core", "Nexa.Xsr.Abstractions", "Nexa.Xsr.Diagnostics"],
            ["Nexa.Xsr.Runtime"] =
            [
                "Nexa.Contracts",
                "Nexa.Core",
                "Nexa.Sidecar.Protocol",
                "Nexa.Sidecar.Transport",
                "Nexa.Xsr.Abstractions",
                "Nexa.Xsr.Diagnostics",
                "Nexa.Xsr.State",
                "Nexa.Xsr.Transport",
            ],
            ["Nexa.Xsr.Generators"] = [],
            ["Nexa.Services"] =
            [
                "Nexa.Contracts",
                "Nexa.Core",
                "Nexa.Domain",
                "Nexa.Xsr.Abstractions",
                "Nexa.Xsr.State",
            ],
            ["Nexa.Services.Composition"] = ["Nexa.Services", "Nexa.Xsr.Runtime"],
            ["Nexa.UI.Next"] = ["Nexa.Core", "Nexa.Xsr.Abstractions", "Nexa.Xsr.State"],
            ["Nexa.UI.Next.Backend.Avalonia"] = ["Nexa.UI.Next"],
            ["Nexa.UI.Next.DevTools"] = ["Nexa.UI.Next", "Nexa.Xsr.Diagnostics"],
            ["Nexa.UI.Next.Benchmarks"] = ["Nexa.UI.Next", "Nexa.Xsr.State"],
            ["Nexa.Pxml.Compiler"] = ["Nexa.Core", "Nexa.Pxml.Generators", "Nexa.Xsr.Abstractions", "Nexa.UI.Next"],
            ["Nexa.Pxml.Runtime"] =
            ["Nexa.Core", "Nexa.Pxml.Compiler", "Nexa.UI.Next", "Nexa.Xsr.Abstractions", "Nexa.Xsr.State"],
            ["Nexa.Pxml.Generators"] = [],
            ["Nexa.Sidecar.Protocol"] = [],
            ["Nexa.Sidecar.Transport"] = ["Nexa.Sidecar.Protocol"],
            ["Nexa.Desktop"] =
            [
                "Nexa.Pxml.Runtime",
                "Nexa.Services",
                "Nexa.Services.Composition",
                "Nexa.Sidecar.Protocol",
                "Nexa.Sidecar.Transport",
                "Nexa.UI.Next",
                "Nexa.UI.Next.Backend.Avalonia",
                "Nexa.Xsr.Runtime",
            ],
            ["Nexa.Xsr.ArchitectureTests"] = [],
            ["Nexa.Xsr.Runtime.Tests"] = ["Nexa.Sidecar.Protocol", "Nexa.Sidecar.Transport", "Nexa.Xsr.Abstractions", "Nexa.Xsr.Diagnostics", "Nexa.Xsr.Runtime", "Nexa.Xsr.State"],
            ["Nexa.UI.Next.Tests"] = ["Nexa.Xsr.Abstractions", "Nexa.Xsr.State", "Nexa.UI.Next"],
            ["Nexa.UI.Next.Backend.Avalonia.Tests"] = ["Nexa.UI.Next.Backend.Avalonia", "Nexa.Xsr.Abstractions", "Nexa.Xsr.State"],
            ["Nexa.Pxml.Tests"] = ["Nexa.Pxml.Compiler", "Nexa.Pxml.Runtime", "Nexa.UI.Next", "Nexa.Xsr.Abstractions", "Nexa.Xsr.State"],
            ["Nexa.Sidecar.Tests"] = ["Nexa.Sidecar.Protocol", "Nexa.Sidecar.Transport"],
            ["Nexa.Services.Tests"] =
            ["Nexa.Pxml.Compiler", "Nexa.Pxml.Runtime", "Nexa.Services", "Nexa.Services.Composition", "Nexa.UI.Next", "Nexa.Xsr.Runtime", "Nexa.Xsr.State"],
            ["Nexa.Desktop.Tests"] = ["Nexa.Desktop", "Nexa.Services", "Nexa.Services.Composition", "Nexa.UI.Next", "Nexa.Xsr.State"],
        };

    private static readonly HashSet<string> ExecutableProjects =
        [
            "Nexa.Desktop",
            "Nexa.UI.Next.Benchmarks",
            "Nexa.Xsr.ArchitectureTests",
            "Nexa.Xsr.Runtime.Tests",
            "Nexa.UI.Next.Tests",
            "Nexa.UI.Next.Backend.Avalonia.Tests",
            "Nexa.Pxml.Tests",
            "Nexa.Sidecar.Tests",
            "Nexa.Services.Tests",
            "Nexa.Desktop.Tests",
        ];

    private static readonly HashSet<string> GeneratorProjects =
        ["Nexa.Pxml.Generators", "Nexa.Xsr.Generators"];

    private static readonly HashSet<string> AotCompatibleProjects =
        [
            "Nexa.Xsr.Abstractions",
            "Nexa.Xsr.Runtime",
            "Nexa.Xsr.State",
            "Nexa.Xsr.Diagnostics",
            "Nexa.UI.Next",
            "Nexa.Pxml.Compiler",
            "Nexa.Pxml.Runtime",
            "Nexa.Services",
            "Nexa.Services.Composition",
        ];

    public static int Main(string[] args)
    {
        string repositoryRoot = ResolveRepositoryRoot(args);
        List<string> failures = [];
        Dictionary<string, string> projectPaths = DiscoverProjects(repositoryRoot, failures);

        ValidateProjectInventory(projectPaths, failures);
        ValidateProjects(repositoryRoot, projectPaths, failures);
        ValidateSolution(repositoryRoot, failures);
        ValidateCommonBuildProperties(repositoryRoot, failures);
        ValidateServicesDoNotNameDesktop(repositoryRoot, failures);
        ValidateDesktopInstallBoundary(repositoryRoot, failures);
        ValidateDesktopSettingsBoundary(repositoryRoot, failures);
        ValidateNativeHostInterop(repositoryRoot, failures);
        ValidatePxmlControlCatalog(repositoryRoot, projectPaths, failures);
        ValidateWave3Ci(repositoryRoot, failures);
        ValidateAcyclicGraph(failures);

        if (failures.Count == 0)
        {
            Console.WriteLine($"XSR architecture tests passed for {projectPaths.Count} projects.");
            return 0;
        }

        Console.Error.WriteLine($"XSR architecture tests failed with {failures.Count} error(s):");
        foreach (string failure in failures.Order(StringComparer.Ordinal))
        {
            Console.Error.WriteLine($"- {failure}");
        }

        return 1;
    }

    private static void ValidateDesktopSettingsBoundary(string repositoryRoot, List<string> failures)
    {
        foreach (string path in Directory.EnumerateFiles(Path.Combine(repositoryRoot, "Nexa.Desktop", "Ui"), "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(path)) continue;
            string source = File.ReadAllText(path);
            foreach (string forbidden in new[] { "SettingsPolicyService", "SettingsPolicySchema", "LauncherSettingsJsonPort", "SettingsService", "NexaSettingsLayers" })
                if (source.Contains(forbidden, StringComparison.Ordinal))
                    failures.Add($"Settings UI must use sealed state/query/command contracts, not {forbidden}: {Path.GetRelativePath(repositoryRoot, path)}");
        }
    }

    private static void ValidateDesktopInstallBoundary(string repositoryRoot, List<string> failures)
    {
        foreach (string path in Directory.EnumerateFiles(Path.Combine(repositoryRoot, "Nexa.Desktop"), "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(path)) continue;
            string source = File.ReadAllText(path);
            if (Path.GetFileName(path).StartsWith("LaunchPageController", StringComparison.Ordinal))
                foreach (string forbidden in new[] { "MinecraftLaunchFaultAnalyzer", "System.Diagnostics.Process", "Process.GetProcessById" })
                    if (source.Contains(forbidden, StringComparison.Ordinal))
                        failures.Add($"Desktop launch UI must consume Service process state, not {forbidden}: {Path.GetRelativePath(repositoryRoot, path)}");
            foreach (string forbidden in new[] { "InstallCompatibility", "InstallCatalogService.StateKey", "MachineCapabilityBroker", "IMachineCapabilityProvider", "MachineCapabilityCatalog" })
                if (source.Contains(forbidden, StringComparison.Ordinal))
                    failures.Add($"Desktop must project the sealed install contract, not {forbidden}: {Path.GetRelativePath(repositoryRoot, path)}");
        }
    }

    private static void ValidateServicesDoNotNameDesktop(
        string repositoryRoot,
        List<string> failures)
    {
        string servicesDirectory = Path.Combine(repositoryRoot, "Nexa.Services");
        foreach (string sourcePath in Directory.EnumerateFiles(
                     servicesDirectory,
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            if (IsBuildOutput(sourcePath))
            {
                continue;
            }

            string source = File.ReadAllText(sourcePath);
            if (source.Contains("Nexa.Desktop", StringComparison.Ordinal))
            {
                string relativePath = Path.GetRelativePath(repositoryRoot, sourcePath)
                    .Replace('\\', '/');
                failures.Add(
                    $"{relativePath} names the Desktop product layer; UI projection state belongs to the composition root.");
            }
        }
    }

    private static void ValidateNativeHostInterop(string repositoryRoot, List<string> failures)
    {
        foreach (string file in Directory.EnumerateFiles(Path.Combine(repositoryRoot, "Nexa.Desktop"), "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file)) continue;
            string source = File.ReadAllText(file);
            if (source.Contains("[ComImport", StringComparison.Ordinal)
                || source.Contains("Marshal.GetObjectForIUnknown", StringComparison.Ordinal)
                || source.Contains("Marshal.GetTypedObjectForIUnknown", StringComparison.Ordinal))
                failures.Add($"{Path.GetRelativePath(repositoryRoot, file)} uses built-in COM, unsupported by NativeAOT; use generated COM wrappers.");
        }
    }

    private static string ResolveRepositoryRoot(string[] args)
    {
        int optionIndex = Array.IndexOf(args, "--repo-root");
        string candidate = optionIndex >= 0 && optionIndex + 1 < args.Length
            ? args[optionIndex + 1]
            : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");

        return Path.GetFullPath(candidate);
    }

    private static Dictionary<string, string> DiscoverProjects(string repositoryRoot, List<string> failures)
    {
        string[] paths = Directory
            .EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Dictionary<string, string> projects = new(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (!projects.TryAdd(name, path))
            {
                failures.Add($"Duplicate project name '{name}' at '{path}'.");
            }
        }

        return projects;
    }

    private static bool IsBuildOutput(string path)
    {
        string[] segments = Path.GetRelativePath(Environment.CurrentDirectory, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return segments.Contains("bin", StringComparer.OrdinalIgnoreCase)
            || segments.Contains("obj", StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateProjectInventory(
        Dictionary<string, string> projectPaths,
        List<string> failures)
    {
        foreach (string missing in AllowedReferences.Keys.Except(projectPaths.Keys, StringComparer.Ordinal))
        {
            failures.Add($"Required project '{missing}' is missing.");
        }

        foreach (string unexpected in projectPaths.Keys.Except(AllowedReferences.Keys, StringComparer.Ordinal))
        {
            failures.Add($"Project '{unexpected}' is not registered in the architecture graph.");
        }
    }

    private static void ValidateProjects(
        string repositoryRoot,
        Dictionary<string, string> projectPaths,
        List<string> failures)
    {
        foreach ((string projectName, string[] allowed) in AllowedReferences)
        {
            if (!projectPaths.TryGetValue(projectName, out string? projectPath))
            {
                continue;
            }

            XDocument project = XDocument.Load(projectPath, LoadOptions.SetLineInfo);
            HashSet<string> actualReferences = [];

            foreach (XElement reference in Elements(project, "ProjectReference"))
            {
                string? include = reference.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                {
                    failures.Add($"{projectName} has a ProjectReference without Include.");
                    continue;
                }

                string targetPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectPath)!, include));
                if (!IsInsideRepository(repositoryRoot, targetPath))
                {
                    failures.Add($"{projectName} references a project outside the XSR repository: '{include}'.");
                    continue;
                }

                if (!File.Exists(targetPath))
                {
                    failures.Add($"{projectName} references missing project '{include}'.");
                    continue;
                }

                actualReferences.Add(Path.GetFileNameWithoutExtension(targetPath));
            }

            foreach (string forbidden in actualReferences.Except(allowed, StringComparer.Ordinal))
            {
                failures.Add($"{projectName} has forbidden project reference '{forbidden}'.");
            }

            foreach (string missing in allowed.Except(actualReferences, StringComparer.Ordinal))
            {
                failures.Add($"{projectName} is missing locked project reference '{missing}'.");
            }

            ValidateProjectKind(projectName, project, failures);
            ValidateFrameworkPackages(projectName, project, failures);
        }
    }

    private static void ValidateProjectKind(string projectName, XDocument project, List<string> failures)
    {
        string? outputType = Property(project, "OutputType");
        string expectedOutputType = projectName == "Nexa.Desktop" ? "WinExe" : "Exe";
        if (ExecutableProjects.Contains(projectName) && !string.Equals(outputType, expectedOutputType, StringComparison.Ordinal))
        {
            failures.Add($"{projectName} must remain an executable project with OutputType={expectedOutputType}.");
        }

        if (GeneratorProjects.Contains(projectName)
            && !string.Equals(Property(project, "IsRoslynComponent"), "true", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{projectName} must remain marked as a Roslyn component.");
        }

        if (AotCompatibleProjects.Contains(projectName)
            && !string.Equals(Property(project, "IsAotCompatible"), "true", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{projectName} must remain marked as AOT compatible.");
        }
    }

    private static void ValidateFrameworkPackages(string projectName, XDocument project, List<string> failures)
    {
        foreach (XElement package in Elements(project, "PackageReference"))
        {
            string? packageName = package.Attribute("Include")?.Value;
            if (packageName is null)
            {
                continue;
            }

            if (AllowedReferences.ContainsKey(packageName))
            {
                failures.Add($"{projectName} bypasses the project graph with internal package '{packageName}'.");
            }

            // Avalonia.Headless is the one sanctioned exception outside the backend project: the
            // backend test project runs the real desktop lifetime headlessly for regression
            // tests and must never leak the platform into product projects.
            bool headlessLifetimeTestPackage = string.Equals(projectName, "Nexa.UI.Next.Backend.Avalonia.Tests", StringComparison.Ordinal)
                && string.Equals(packageName, "Avalonia.Headless", StringComparison.Ordinal);
            if (!string.Equals(projectName, "Nexa.UI.Next.Backend.Avalonia", StringComparison.Ordinal)
                && packageName.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase)
                && !headlessLifetimeTestPackage)
            {
                failures.Add($"{projectName} must not reference Avalonia package '{packageName}'.");
            }
        }
    }

    private static void ValidateSolution(string repositoryRoot, List<string> failures)
    {
        string solutionPath = Path.Combine(repositoryRoot, "NexaCL.slnx");
        if (!File.Exists(solutionPath))
        {
            failures.Add("NexaCL.slnx is missing.");
            return;
        }

        XDocument solution = XDocument.Load(solutionPath);
        HashSet<string> solutionProjects = [];
        foreach (XElement project in Elements(solution, "Project"))
        {
            string? path = project.Attribute("Path")?.Value;
            if (string.IsNullOrWhiteSpace(path))
            {
                failures.Add("NexaCL.slnx contains a Project without Path.");
                continue;
            }

            string projectPath = Path.GetFullPath(Path.Combine(repositoryRoot, path));
            if (!IsInsideRepository(repositoryRoot, projectPath) || !File.Exists(projectPath))
            {
                failures.Add($"NexaCL.slnx contains invalid project path '{path}'.");
                continue;
            }

            solutionProjects.Add(Path.GetFileNameWithoutExtension(projectPath));
        }

        foreach (string missing in AllowedReferences.Keys.Except(solutionProjects, StringComparer.Ordinal))
        {
            failures.Add($"NexaCL.slnx is missing project '{missing}'.");
        }

        foreach (string unexpected in solutionProjects.Except(AllowedReferences.Keys, StringComparer.Ordinal))
        {
            failures.Add($"NexaCL.slnx contains unregistered project '{unexpected}'.");
        }
    }

    private static void ValidateCommonBuildProperties(string repositoryRoot, List<string> failures)
    {
        string propsPath = Path.Combine(repositoryRoot, "Directory.Build.props");
        if (!File.Exists(propsPath))
        {
            failures.Add("Directory.Build.props is missing.");
            return;
        }

        XDocument props = XDocument.Load(propsPath);
        bool importsVersion = Elements(props, "Import")
            .Select(element => element.Attribute("Project")?.Value)
            .Any(path => path?.Replace('\\', '/').EndsWith("eng/xsr/Xsr.Version.props", StringComparison.Ordinal) == true);

        if (!importsVersion)
        {
            failures.Add("Directory.Build.props must import eng/xsr/Xsr.Version.props.");
        }

        if (!string.Equals(Property(props, "TargetFramework"), "net10.0", StringComparison.Ordinal))
        {
            failures.Add("The XSR project graph must target net10.0 by default.");
        }

        if (!string.Equals(Property(props, "TreatWarningsAsErrors"), "true", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("TreatWarningsAsErrors must remain enabled for XSR projects.");
        }
    }

    private static void ValidateAcyclicGraph(List<string> failures)
    {
        HashSet<string> visiting = new(StringComparer.Ordinal);
        HashSet<string> visited = new(StringComparer.Ordinal);

        foreach (string project in AllowedReferences.Keys)
        {
            Visit(project, visiting, visited, failures);
        }
    }

    private static void ValidatePxmlControlCatalog(
        string repositoryRoot,
        Dictionary<string, string> projectPaths,
        List<string> failures)
    {
        string propsPath = Path.Combine(repositoryRoot, "Directory.Build.props");
        XDocument props = XDocument.Load(propsPath);
        string? configuredDirectory = Property(props, "PxmlControlCatalogDirectory");
        if (string.IsNullOrWhiteSpace(configuredDirectory)
            || !configuredDirectory.Replace('\\', '/').EndsWith("Nexa.UI.Next/PxmlControls", StringComparison.Ordinal))
        {
            failures.Add("Directory.Build.props must explicitly select the UI.Next-owned PxmlControlCatalogDirectory.");
        }

        string catalogDirectory = Path.Combine(repositoryRoot, "Nexa.UI.Next", "PxmlControls");
        string[] descriptors = Directory.Exists(catalogDirectory)
            ? Directory.GetFiles(catalogDirectory, "*.pxml-control", SearchOption.TopDirectoryOnly)
            : [];
        if (descriptors.Length == 0)
        {
            failures.Add("The UI.Next PXML control catalog must contain .pxml-control descriptors.");
        }

        if (!projectPaths.TryGetValue("Nexa.Pxml.Compiler", out string? compilerProjectPath))
        {
            return;
        }

        XDocument compilerProject = XDocument.Load(compilerProjectPath);
        if (!string.Equals(Property(compilerProject, "EmitCompilerGeneratedFiles"), "true", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("Nexa.Pxml.Compiler must materialize compiler-generated files for inspection.");
        }

        string? generatedPath = Property(compilerProject, "CompilerGeneratedFilesOutputPath");
        if (generatedPath is null
            || !generatedPath.Contains("$(BaseIntermediateOutputPath)", StringComparison.Ordinal)
            || !generatedPath.Replace('\\', '/').EndsWith("Generated", StringComparison.Ordinal))
        {
            failures.Add("Nexa.Pxml.Compiler generated files must stay under BaseIntermediateOutputPath.");
        }

        bool includesCatalog = Elements(compilerProject, "PxmlControlDefinition")
            .Select(element => element.Attribute("Include")?.Value)
            .Any(value => value?.Contains("$(PxmlControlCatalogDirectory)", StringComparison.Ordinal) == true
                && value.EndsWith("*.pxml-control", StringComparison.Ordinal));
        if (!includesCatalog)
        {
            failures.Add("Nexa.Pxml.Compiler must enumerate PxmlControlCatalogDirectory as .pxml-control inputs.");
        }

        bool passesAdditionalFiles = Elements(compilerProject, "AdditionalFiles")
            .Select(element => element.Attribute("Include")?.Value)
            .Any(value => string.Equals(value, "@(PxmlControlDefinition)", StringComparison.Ordinal));
        if (!passesAdditionalFiles)
        {
            failures.Add("Nexa.Pxml.Compiler must pass every control descriptor to the generator as AdditionalFiles.");
        }

        string[] compileCacheInputs = Elements(compilerProject, "CoreCompileCache")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();
        if (!compileCacheInputs.Contains("$(PxmlControlCatalogDirectory)", StringComparer.Ordinal)
            || !compileCacheInputs.Contains("@(PxmlControlDefinition)", StringComparer.Ordinal))
        {
            failures.Add("Nexa.Pxml.Compiler must fingerprint the selected catalog path and file set for incremental builds.");
        }

        XElement? validationTarget = Elements(compilerProject, "Target")
            .FirstOrDefault(element => string.Equals(
                element.Attribute("Name")?.Value,
                "ValidatePxmlControlCatalog",
                StringComparison.Ordinal));
        string validationText = validationTarget?.ToString(SaveOptions.DisableFormatting) ?? string.Empty;
        if (validationTarget is null
            || !validationText.Contains("PxmlControlCatalogDirectory", StringComparison.Ordinal)
            || !validationText.Contains("PxmlControlDefinition", StringComparison.Ordinal))
        {
            failures.Add("Nexa.Pxml.Compiler must fail before CoreCompile when the catalog path is missing or empty.");
        }

        XElement? generatorReference = Elements(compilerProject, "ProjectReference")
            .FirstOrDefault(element => element.Attribute("Include")?.Value.Replace('\\', '/')
                .EndsWith("Nexa.Pxml.Generators/Nexa.Pxml.Generators.csproj", StringComparison.Ordinal) == true);
        if (generatorReference is null
            || !string.Equals(generatorReference.Attribute("OutputItemType")?.Value, "Analyzer", StringComparison.Ordinal)
            || !string.Equals(generatorReference.Attribute("ReferenceOutputAssembly")?.Value, "false", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("Nexa.Pxml.Compiler must consume Nexa.Pxml.Generators only as an analyzer.");
        }
        else
        {
            string[] removedPublishProperties = (generatorReference.Attribute("GlobalPropertiesToRemove")?.Value ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string[] requiredRemovedProperties =
                [
                    "PublishAot",
                    "PublishTrimmed",
                    "PublishSingleFile",
                    "PublishReadyToRun",
                    "SelfContained",
                    "RuntimeIdentifier",
                ];
            foreach (string requiredProperty in requiredRemovedProperties)
            {
                if (!removedPublishProperties.Contains(requiredProperty, StringComparer.Ordinal))
                {
                    failures.Add(
                        $"Nexa.Pxml.Compiler must not propagate '{requiredProperty}' to its build-only generator reference.");
                }
            }
        }

        string compilerSource = File.ReadAllText(Path.Combine(repositoryRoot, "Nexa.Pxml.Compiler", "PxmlCompiler.cs"));
        string[] controlNames = descriptors
            .SelectMany(File.ReadLines)
            .Where(line => line.StartsWith("name=", StringComparison.Ordinal))
            .Select(line => line.Substring("name=".Length).Trim())
            .Where(name => name.Length != 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (string controlName in controlNames)
        {
            if (compilerSource.Contains($"\"{controlName}\"", StringComparison.Ordinal))
            {
                failures.Add($"Nexa.Pxml.Compiler must not embed generated control model name '{controlName}'.");
            }
        }

        string loaderSource = File.ReadAllText(Path.Combine(repositoryRoot, "Nexa.Pxml.Runtime", "PxmlUiLoader.cs"));
        foreach (string controlName in controlNames)
        {
            string forbiddenKind = $"PxmlIrNodeKind.{controlName}";
            if (loaderSource.Contains(forbiddenKind, StringComparison.Ordinal))
            {
                failures.Add($"Nexa.Pxml.Runtime must dispatch generated controls through recipes, not {forbiddenKind}.");
            }
        }

        string[] forbiddenReflectionTokens =
        [
            "System.Reflection",
            "Activator.",
            "BindingFlags",
            "MethodInfo",
            "PropertyInfo",
            ".GetMethod(",
            ".GetProperty(",
            "Type.GetType(",
        ];
        foreach (string projectDirectoryName in new[] { "Nexa.Pxml.Compiler", "Nexa.Pxml.Runtime" })
        {
            string projectDirectory = Path.Combine(repositoryRoot, projectDirectoryName);
            foreach (string sourcePath in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(projectDirectory, sourcePath).Replace('\\', '/');
                if (relativePath.StartsWith("bin/", StringComparison.Ordinal)
                    || relativePath.StartsWith("obj/", StringComparison.Ordinal))
                {
                    continue;
                }

                string source = File.ReadAllText(sourcePath);
                foreach (string forbiddenToken in forbiddenReflectionTokens)
                {
                    if (source.Contains(forbiddenToken, StringComparison.Ordinal))
                    {
                        failures.Add(
                            $"{projectDirectoryName}/{relativePath} contains forbidden runtime-reflection token '{forbiddenToken}'.");
                    }
                }

                if (projectDirectoryName == "Nexa.Pxml.Runtime")
                {
                    string[] forbiddenCatalogIoTokens =
                        ["System.IO", "File.", "Directory.", "PxmlControlCatalogDirectory", ".pxml-control"];
                    foreach (string forbiddenToken in forbiddenCatalogIoTokens)
                    {
                        if (source.Contains(forbiddenToken, StringComparison.Ordinal))
                        {
                            failures.Add(
                                $"{projectDirectoryName}/{relativePath} contains forbidden runtime catalog-I/O token '{forbiddenToken}'.");
                        }
                    }
                }
            }
        }

        string irSource = File.ReadAllText(Path.Combine(repositoryRoot, "Nexa.Pxml.Compiler", "PxmlIr.cs"));
        if (irSource.Contains("enum PxmlIrNodeKind", StringComparison.Ordinal))
        {
            failures.Add("PxmlIrNodeKind must be generated from the configured control catalog.");
        }

        if (projectPaths.TryGetValue("Nexa.Pxml.Generators", out string? generatorProjectPath))
        {
            XDocument generatorProject = XDocument.Load(generatorProjectPath);
            if (!string.Equals(Property(generatorProject, "TargetFramework"), "netstandard2.0", StringComparison.Ordinal))
            {
                failures.Add("Nexa.Pxml.Generators must target netstandard2.0 for compiler-host compatibility.");
            }

            string[] isolatedPublishProperties =
            [
                "PublishAot",
                "PublishTrimmed",
                "PublishSingleFile",
                "PublishReadyToRun",
                "SelfContained",
                "RuntimeIdentifier",
            ];
            string[] localProperties = (generatorProject.Root?.Attribute("TreatAsLocalProperty")?.Value ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string isolatedProperty in isolatedPublishProperties)
            {
                if (!localProperties.Contains(isolatedProperty, StringComparer.Ordinal))
                {
                    failures.Add($"Nexa.Pxml.Generators must treat '{isolatedProperty}' as a local build-tool property.");
                }
            }

            foreach (string disabledProperty in isolatedPublishProperties.Where(
                         property => property != "RuntimeIdentifier"))
            {
                if (!string.Equals(Property(generatorProject, disabledProperty), "false", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"Nexa.Pxml.Generators must disable inherited '{disabledProperty}'.");
                }
            }

            if (!string.IsNullOrEmpty(Property(generatorProject, "RuntimeIdentifier")))
            {
                failures.Add("Nexa.Pxml.Generators must clear inherited RuntimeIdentifier values.");
            }

            XElement? roslynPackage = Elements(generatorProject, "PackageReference")
                .FirstOrDefault(element => string.Equals(
                    element.Attribute("Include")?.Value,
                    "Microsoft.CodeAnalysis.CSharp",
                    StringComparison.Ordinal));
            if (roslynPackage is null
                || !string.Equals(roslynPackage.Attribute("PrivateAssets")?.Value, "all", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("Nexa.Pxml.Generators must keep Roslyn dependencies private.");
            }
        }
    }

    private static void ValidateWave3Ci(string repositoryRoot, List<string> failures)
    {
        string workflowPath = Path.Combine(repositoryRoot, ".github", "workflows", "xsr-ci.yml");
        if (!File.Exists(workflowPath))
        {
            failures.Add("The XSR CI workflow is missing.");
            return;
        }

        string workflow = File.ReadAllText(workflowPath);
        string[] requiredFragments =
        [
            "dotnet run --project tests/Nexa.Pxml.Tests/Nexa.Pxml.Tests.csproj",
            "dotnet publish tests/Nexa.Pxml.Tests/Nexa.Pxml.Tests.csproj",
            "-p:PublishAot=true",
            "pxml-aot/Nexa.Pxml.Tests",
            "pcl-desktop-aot/Nexa.Desktop\" --validate-shell",
            "pcl-desktop-trimmed/Nexa.Desktop\" --validate-shell",
            "-p:PublishTrimmed=true",
            "-p:TrimMode=link",
            "PxmlControlCatalogDirectory",
            "Fixtures/AlternateCatalog",
            "Fixtures/InvalidCatalog",
            "PXMLGEN002",
            "PxmlControlCatalog.g.cs",
            "dotnet format NexaCL.slnx --no-restore --verify-no-changes --diagnostics IDE0055 IMPORTS",
        ];
        foreach (string requiredFragment in requiredFragments)
        {
            if (!workflow.Contains(requiredFragment, StringComparison.Ordinal))
            {
                failures.Add($"The XSR CI workflow is missing the Wave 3 gate fragment '{requiredFragment}'.");
            }
        }
    }

    private static void Visit(
        string project,
        HashSet<string> visiting,
        HashSet<string> visited,
        List<string> failures)
    {
        if (visited.Contains(project))
        {
            return;
        }

        if (!visiting.Add(project))
        {
            failures.Add($"The locked project graph contains a cycle at '{project}'.");
            return;
        }

        foreach (string dependency in AllowedReferences[project])
        {
            if (AllowedReferences.ContainsKey(dependency))
            {
                Visit(dependency, visiting, visited, failures);
            }
        }

        visiting.Remove(project);
        visited.Add(project);
    }

    private static IEnumerable<XElement> Elements(XDocument document, string localName) =>
        document.Descendants().Where(element => element.Name.LocalName == localName);

    private static string? Property(XDocument document, string localName) =>
        Elements(document, localName).Select(element => element.Value.Trim()).LastOrDefault();

    private static bool IsInsideRepository(string repositoryRoot, string path)
    {
        string relative = Path.GetRelativePath(repositoryRoot, path);
        return !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }
}
