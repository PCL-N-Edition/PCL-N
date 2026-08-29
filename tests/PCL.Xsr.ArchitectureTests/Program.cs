using System.Xml.Linq;

namespace PCL.Xsr.ArchitectureTests;

internal static class Program
{
    private static readonly IReadOnlyDictionary<string, string[]> AllowedReferences =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["PCL.Core"] = [],
            ["PCL.Domain"] = ["PCL.Core"],
            ["PCL.Contracts"] = ["PCL.Core"],
            ["PCL.Xsr.Abstractions"] = ["PCL.Contracts", "PCL.Core"],
            ["PCL.Xsr.State"] = ["PCL.Core", "PCL.Xsr.Abstractions"],
            ["PCL.Xsr.Diagnostics"] = ["PCL.Core", "PCL.Xsr.Abstractions"],
            ["PCL.Xsr.Transport"] = ["PCL.Core", "PCL.Xsr.Abstractions", "PCL.Xsr.Diagnostics"],
            ["PCL.Xsr.Runtime"] =
            [
                "PCL.Contracts",
                "PCL.Core",
                "PCL.Xsr.Abstractions",
                "PCL.Xsr.Diagnostics",
                "PCL.Xsr.State",
                "PCL.Xsr.Transport",
            ],
            ["PCL.Xsr.Generators"] = [],
            ["PCL.Services"] =
            [
                "PCL.Contracts",
                "PCL.Core",
                "PCL.Domain",
                "PCL.Xsr.Abstractions",
                "PCL.Xsr.State",
            ],
            ["PCL.UI.Next"] = ["PCL.Core", "PCL.Xsr.Abstractions", "PCL.Xsr.State"],
            ["PCL.UI.Next.Backend.Avalonia"] = ["PCL.UI.Next"],
            ["PCL.UI.Next.DevTools"] = ["PCL.UI.Next", "PCL.Xsr.Diagnostics"],
            ["PCL.UI.Next.Benchmarks"] = ["PCL.UI.Next", "PCL.Xsr.State"],
            ["PCL.Pxml.Compiler"] = ["PCL.Core", "PCL.Pxml.Generators", "PCL.Xsr.Abstractions", "PCL.UI.Next"],
            ["PCL.Pxml.Runtime"] =
            ["PCL.Core", "PCL.Pxml.Compiler", "PCL.UI.Next", "PCL.Xsr.Abstractions", "PCL.Xsr.State"],
            ["PCL.Pxml.Generators"] = [],
            ["PCL.Sidecar.Protocol"] = [],
            ["PCL.Sidecar.Transport"] = ["PCL.Sidecar.Protocol"],
            ["PCL.Desktop"] =
            [
                "PCL.Pxml.Runtime",
                "PCL.Services",
                "PCL.Sidecar.Protocol",
                "PCL.Sidecar.Transport",
                "PCL.UI.Next",
                "PCL.UI.Next.Backend.Avalonia",
                "PCL.Xsr.Runtime",
            ],
            ["PCL.Xsr.ArchitectureTests"] = [],
            ["PCL.Xsr.Runtime.Tests"] = ["PCL.Xsr.Abstractions", "PCL.Xsr.Runtime", "PCL.Xsr.State", "PCL.Xsr.Diagnostics"],
            ["PCL.UI.Next.Tests"] = ["PCL.Xsr.Abstractions", "PCL.Xsr.State", "PCL.UI.Next"],
                        ["PCL.Pxml.Tests"] = ["PCL.Pxml.Compiler", "PCL.Pxml.Runtime", "PCL.UI.Next", "PCL.Xsr.Abstractions", "PCL.Xsr.State"],
            ["PCL.Sidecar.Tests"] = ["PCL.Sidecar.Protocol", "PCL.Sidecar.Transport"],
        };

    private static readonly HashSet<string> ExecutableProjects =
        [
            "PCL.Desktop",
            "PCL.UI.Next.Benchmarks",
            "PCL.Xsr.ArchitectureTests",
            "PCL.Xsr.Runtime.Tests",
            "PCL.UI.Next.Tests",
            "PCL.Pxml.Tests",
            "PCL.Sidecar.Tests",
        ];

    private static readonly HashSet<string> GeneratorProjects =
        ["PCL.Pxml.Generators", "PCL.Xsr.Generators"];

    private static readonly HashSet<string> AotCompatibleProjects =
        [
            "PCL.Xsr.Abstractions",
            "PCL.Xsr.Runtime",
            "PCL.Xsr.State",
            "PCL.Xsr.Diagnostics",
            "PCL.UI.Next",
            "PCL.Pxml.Compiler",
            "PCL.Pxml.Runtime",
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
        if (ExecutableProjects.Contains(projectName) && !string.Equals(outputType, "Exe", StringComparison.Ordinal))
        {
            failures.Add($"{projectName} must remain an executable project.");
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

            if (!string.Equals(projectName, "PCL.UI.Next.Backend.Avalonia", StringComparison.Ordinal)
                && packageName.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{projectName} must not reference Avalonia package '{packageName}'.");
            }
        }
    }

    private static void ValidateSolution(string repositoryRoot, List<string> failures)
    {
        string solutionPath = Path.Combine(repositoryRoot, "PCL-N.slnx");
        if (!File.Exists(solutionPath))
        {
            failures.Add("PCL-N.slnx is missing.");
            return;
        }

        XDocument solution = XDocument.Load(solutionPath);
        HashSet<string> solutionProjects = [];
        foreach (XElement project in Elements(solution, "Project"))
        {
            string? path = project.Attribute("Path")?.Value;
            if (string.IsNullOrWhiteSpace(path))
            {
                failures.Add("PCL-N.slnx contains a Project without Path.");
                continue;
            }

            string projectPath = Path.GetFullPath(Path.Combine(repositoryRoot, path));
            if (!IsInsideRepository(repositoryRoot, projectPath) || !File.Exists(projectPath))
            {
                failures.Add($"PCL-N.slnx contains invalid project path '{path}'.");
                continue;
            }

            solutionProjects.Add(Path.GetFileNameWithoutExtension(projectPath));
        }

        foreach (string missing in AllowedReferences.Keys.Except(solutionProjects, StringComparer.Ordinal))
        {
            failures.Add($"PCL-N.slnx is missing project '{missing}'.");
        }

        foreach (string unexpected in solutionProjects.Except(AllowedReferences.Keys, StringComparer.Ordinal))
        {
            failures.Add($"PCL-N.slnx contains unregistered project '{unexpected}'.");
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
            || !configuredDirectory.Replace('\\', '/').EndsWith("PCL.UI.Next/PxmlControls", StringComparison.Ordinal))
        {
            failures.Add("Directory.Build.props must explicitly select the UI.Next-owned PxmlControlCatalogDirectory.");
        }

        string catalogDirectory = Path.Combine(repositoryRoot, "PCL.UI.Next", "PxmlControls");
        string[] descriptors = Directory.Exists(catalogDirectory)
            ? Directory.GetFiles(catalogDirectory, "*.pxml-control", SearchOption.TopDirectoryOnly)
            : [];
        if (descriptors.Length == 0)
        {
            failures.Add("The UI.Next PXML control catalog must contain .pxml-control descriptors.");
        }

        if (!projectPaths.TryGetValue("PCL.Pxml.Compiler", out string? compilerProjectPath))
        {
            return;
        }

        XDocument compilerProject = XDocument.Load(compilerProjectPath);
        if (!string.Equals(Property(compilerProject, "EmitCompilerGeneratedFiles"), "true", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("PCL.Pxml.Compiler must materialize compiler-generated files for inspection.");
        }

        string? generatedPath = Property(compilerProject, "CompilerGeneratedFilesOutputPath");
        if (generatedPath is null
            || !generatedPath.Contains("$(BaseIntermediateOutputPath)", StringComparison.Ordinal)
            || !generatedPath.Replace('\\', '/').EndsWith("Generated", StringComparison.Ordinal))
        {
            failures.Add("PCL.Pxml.Compiler generated files must stay under BaseIntermediateOutputPath.");
        }

        bool includesCatalog = Elements(compilerProject, "PxmlControlDefinition")
            .Select(element => element.Attribute("Include")?.Value)
            .Any(value => value?.Contains("$(PxmlControlCatalogDirectory)", StringComparison.Ordinal) == true
                && value.EndsWith("*.pxml-control", StringComparison.Ordinal));
        if (!includesCatalog)
        {
            failures.Add("PCL.Pxml.Compiler must enumerate PxmlControlCatalogDirectory as .pxml-control inputs.");
        }

        bool passesAdditionalFiles = Elements(compilerProject, "AdditionalFiles")
            .Select(element => element.Attribute("Include")?.Value)
            .Any(value => string.Equals(value, "@(PxmlControlDefinition)", StringComparison.Ordinal));
        if (!passesAdditionalFiles)
        {
            failures.Add("PCL.Pxml.Compiler must pass every control descriptor to the generator as AdditionalFiles.");
        }

        string[] compileCacheInputs = Elements(compilerProject, "CoreCompileCache")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();
        if (!compileCacheInputs.Contains("$(PxmlControlCatalogDirectory)", StringComparer.Ordinal)
            || !compileCacheInputs.Contains("@(PxmlControlDefinition)", StringComparer.Ordinal))
        {
            failures.Add("PCL.Pxml.Compiler must fingerprint the selected catalog path and file set for incremental builds.");
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
            failures.Add("PCL.Pxml.Compiler must fail before CoreCompile when the catalog path is missing or empty.");
        }

        XElement? generatorReference = Elements(compilerProject, "ProjectReference")
            .FirstOrDefault(element => element.Attribute("Include")?.Value.Replace('\\', '/')
                .EndsWith("PCL.Pxml.Generators/PCL.Pxml.Generators.csproj", StringComparison.Ordinal) == true);
        if (generatorReference is null
            || !string.Equals(generatorReference.Attribute("OutputItemType")?.Value, "Analyzer", StringComparison.Ordinal)
            || !string.Equals(generatorReference.Attribute("ReferenceOutputAssembly")?.Value, "false", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("PCL.Pxml.Compiler must consume PCL.Pxml.Generators only as an analyzer.");
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
                        $"PCL.Pxml.Compiler must not propagate '{requiredProperty}' to its build-only generator reference.");
                }
            }
        }

        string compilerSource = File.ReadAllText(Path.Combine(repositoryRoot, "PCL.Pxml.Compiler", "PxmlCompiler.cs"));
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
                failures.Add($"PCL.Pxml.Compiler must not embed generated control model name '{controlName}'.");
            }
        }

        string loaderSource = File.ReadAllText(Path.Combine(repositoryRoot, "PCL.Pxml.Runtime", "PxmlUiLoader.cs"));
        foreach (string controlName in controlNames)
        {
            string forbiddenKind = $"PxmlIrNodeKind.{controlName}";
            if (loaderSource.Contains(forbiddenKind, StringComparison.Ordinal))
            {
                failures.Add($"PCL.Pxml.Runtime must dispatch generated controls through recipes, not {forbiddenKind}.");
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
        foreach (string projectDirectoryName in new[] { "PCL.Pxml.Compiler", "PCL.Pxml.Runtime" })
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

                if (projectDirectoryName == "PCL.Pxml.Runtime")
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

        string irSource = File.ReadAllText(Path.Combine(repositoryRoot, "PCL.Pxml.Compiler", "PxmlIr.cs"));
        if (irSource.Contains("enum PxmlIrNodeKind", StringComparison.Ordinal))
        {
            failures.Add("PxmlIrNodeKind must be generated from the configured control catalog.");
        }

        if (projectPaths.TryGetValue("PCL.Pxml.Generators", out string? generatorProjectPath))
        {
            XDocument generatorProject = XDocument.Load(generatorProjectPath);
            if (!string.Equals(Property(generatorProject, "TargetFramework"), "netstandard2.0", StringComparison.Ordinal))
            {
                failures.Add("PCL.Pxml.Generators must target netstandard2.0 for compiler-host compatibility.");
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
                    failures.Add($"PCL.Pxml.Generators must treat '{isolatedProperty}' as a local build-tool property.");
                }
            }

            foreach (string disabledProperty in isolatedPublishProperties.Where(
                         property => property != "RuntimeIdentifier"))
            {
                if (!string.Equals(Property(generatorProject, disabledProperty), "false", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"PCL.Pxml.Generators must disable inherited '{disabledProperty}'.");
                }
            }

            if (!string.IsNullOrEmpty(Property(generatorProject, "RuntimeIdentifier")))
            {
                failures.Add("PCL.Pxml.Generators must clear inherited RuntimeIdentifier values.");
            }

            XElement? roslynPackage = Elements(generatorProject, "PackageReference")
                .FirstOrDefault(element => string.Equals(
                    element.Attribute("Include")?.Value,
                    "Microsoft.CodeAnalysis.CSharp",
                    StringComparison.Ordinal));
            if (roslynPackage is null
                || !string.Equals(roslynPackage.Attribute("PrivateAssets")?.Value, "all", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("PCL.Pxml.Generators must keep Roslyn dependencies private.");
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
            "dotnet run --project tests/PCL.Pxml.Tests/PCL.Pxml.Tests.csproj",
            "dotnet publish tests/PCL.Pxml.Tests/PCL.Pxml.Tests.csproj",
            "-p:PublishAot=true",
            "pxml-aot/PCL.Pxml.Tests",
            "PxmlControlCatalogDirectory",
            "Fixtures/AlternateCatalog",
            "Fixtures/InvalidCatalog",
            "PXMLGEN002",
            "PxmlControlCatalog.g.cs",
            "dotnet format PCL-N.slnx --no-restore --verify-no-changes --diagnostics IDE0055 IMPORTS",
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
