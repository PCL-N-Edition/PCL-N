// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Xsr.ArchitectureTests;

[TestClass]
public sealed class ArchitectureRulesTests
{
    private static readonly string[] RequiredDocuments =
    [
        "architecture.md",
        "dependency-rules.md",
        "state-model.md",
        "service-model.md",
        "renderer-model.md",
        "sidecar-protocol.md",
        "versioning.md",
        "migration-map.md"
    ];

    private static readonly Regex ProductVersionPattern = new(
        @"^\d+\.\d+\.\d+(?:\.(?:alpha|beta)\.[1-9]\d*|\.ci\.[0-9a-f]{6})?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    [TestMethod]
    public void ArchitectureLock_ContainsEveryNormativeDocument()
    {
        string root = FindRepositoryRoot();
        string documentRoot = Path.Combine(root, "docs", "xsr");
        string[] missing = RequiredDocuments
            .Where(file => !File.Exists(Path.Combine(documentRoot, file)))
            .ToArray();

        Assert.AreEqual(0, missing.Length, "Missing XSR architecture documents: " + string.Join(", ", missing));
    }

    [TestMethod]
    public void NewArchitectureProjects_RespectLockedDependencyDirection()
    {
        string root = FindRepositoryRoot();
        ProjectDescriptor[] projects = EnumerateProjects(root).ToArray();
        List<string> violations = [];

        foreach (ProjectDescriptor project in projects)
        {
            foreach (string reference in project.ProjectReferences)
            {
                if (IsForbiddenProjectReference(project.Name, reference))
                    violations.Add($"{project.RelativePath} -> {reference}");
            }

            if (ForbidsAvalonia(project.Name))
            {
                foreach (string package in project.PackageReferences.Where(
                             static package => package.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add($"{project.RelativePath} -> package {package}");
                }
            }
        }

        Assert.AreEqual(
            0,
            violations.Count,
            "Forbidden XSR dependency edges:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void NewArchitectureProjects_ImportCentralVersionPolicy()
    {
        string root = FindRepositoryRoot();
        string[] missingImports = EnumerateProjects(root)
            .Where(static project => UsesXsrProductVersion(project.Name))
            .Where(static project => !project.Source.Contains(
                "eng\\xsr\\Xsr.Version.props",
                StringComparison.OrdinalIgnoreCase))
            .Select(static project => project.RelativePath)
            .ToArray();

        Assert.AreEqual(
            0,
            missingImports.Length,
            "XSR projects missing eng/xsr/Xsr.Version.props: " + string.Join(", ", missingImports));
    }

    [TestMethod]
    public void ProductVersion_DefaultAndCanonicalFormsAreExact()
    {
        Dictionary<string, string> metadata = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(static attribute => attribute.Key, static attribute => attribute.Value ?? string.Empty);

        Assert.AreEqual("2.0.0.alpha.1", metadata["XsrProductVersion"]);
        Assert.AreEqual("2.0.0-alpha.1", metadata["XsrPackageVersion"]);
        Assert.AreEqual(new Version(2, 0, 0, 0), Assembly.GetExecutingAssembly().GetName().Version);

        string[] accepted =
        [
            "2.0.0",
            "2.0.0.alpha.1",
            "2.0.0.beta.1",
            "2.0.0.ci.ffffff"
        ];
        string[] rejected =
        [
            "2.0.0-alpha.1",
            "2.0.0.beta",
            "2.0.0.alpha.0",
            "2.0.0.ci.FFFFFF",
            "2.0.0.ci.fffffff",
            "2.0.0.release"
        ];

        Assert.IsTrue(accepted.All(ProductVersionPattern.IsMatch));
        Assert.IsFalse(rejected.Any(ProductVersionPattern.IsMatch));
    }

    private static bool IsForbiddenProjectReference(string source, string target)
    {
        if (string.Equals(source, "PCL.Domain", StringComparison.Ordinal))
        {
            return StartsWithAny(target, "PCL.Application", "PCL.Desktop", "PCL.Platform", "PCL.UI");
        }

        if (source.StartsWith("PCL.Xsr.", StringComparison.Ordinal))
        {
            return StartsWithAny(
                target,
                "PCL.Application",
                "PCL.Desktop",
                "PCL.Services.",
                "PCL.UI.Next");
        }

        if (source.StartsWith("PCL.Services.", StringComparison.Ordinal))
        {
            return StartsWithAny(target, "PCL.Application", "PCL.Desktop", "PCL.UI.");
        }

        if (string.Equals(source, "PCL.UI.Next", StringComparison.Ordinal))
        {
            return StartsWithAny(
                target,
                "PCL.Application",
                "PCL.Desktop",
                "PCL.Plugin.Sidecar",
                "PCL.Services.",
                "PCL.Sidecar.");
        }

        if (source.StartsWith("PCL.N.Plugin.", StringComparison.Ordinal))
        {
            return StartsWithAny(
                target,
                "PCL.Application",
                "PCL.Desktop",
                "PCL.Platform",
                "PCL.Plugin.Sidecar",
                "PCL.UI.Next");
        }

        return false;
    }

    private static bool ForbidsAvalonia(string projectName) =>
        string.Equals(projectName, "PCL.Domain", StringComparison.Ordinal) ||
        string.Equals(projectName, "PCL.UI.Next", StringComparison.Ordinal) ||
        projectName.StartsWith("PCL.Xsr.", StringComparison.Ordinal) ||
        projectName.StartsWith("PCL.Services.", StringComparison.Ordinal) ||
        projectName.StartsWith("PCL.N.Plugin.", StringComparison.Ordinal);

    private static bool UsesXsrProductVersion(string projectName) =>
        projectName.StartsWith("PCL.Xsr.", StringComparison.Ordinal) ||
        projectName.StartsWith("PCL.Services.", StringComparison.Ordinal) ||
        projectName.StartsWith("PCL.Pxml.", StringComparison.Ordinal) ||
        projectName.StartsWith("PCL.Sidecar.", StringComparison.Ordinal) ||
        projectName.StartsWith("PCL.N.Plugin.", StringComparison.Ordinal) ||
        string.Equals(projectName, "PCL.Plugin.Sidecar", StringComparison.Ordinal);

    private static bool StartsWithAny(string value, params string[] prefixes) =>
        prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.Ordinal));

    private static IEnumerable<ProjectDescriptor> EnumerateProjects(string root)
    {
        foreach (string path in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(root, path);
            if (IsIgnoredPath(relativePath))
                continue;

            string source = File.ReadAllText(path);
            XDocument document = XDocument.Parse(source, LoadOptions.PreserveWhitespace);
            string name = document.Descendants("AssemblyName").Select(static element => element.Value.Trim()).FirstOrDefault()
                ?? Path.GetFileNameWithoutExtension(path);
            string[] projectReferences = document.Descendants("ProjectReference")
                .Select(static element => element.Attribute("Include")?.Value)
                .Where(static include => !string.IsNullOrWhiteSpace(include))
                .Select(include => Path.GetFileNameWithoutExtension(
                    Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, include!))))
                .ToArray();
            string[] packageReferences = document.Descendants("PackageReference")
                .Select(static element => element.Attribute("Include")?.Value)
                .Where(static include => !string.IsNullOrWhiteSpace(include))
                .Select(static include => include!)
                .ToArray();

            yield return new ProjectDescriptor(name, relativePath, source, projectReferences, packageReferences);
        }
    }

    private static bool IsIgnoredPath(string relativePath) =>
        relativePath.StartsWith("external" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        relativePath.Split(Path.DirectorySeparatorChar).Any(static segment =>
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PCL-N.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate the PCL N repository root.");
    }

    private sealed record ProjectDescriptor(
        string Name,
        string RelativePath,
        string Source,
        IReadOnlyList<string> ProjectReferences,
        IReadOnlyList<string> PackageReferences);
}
