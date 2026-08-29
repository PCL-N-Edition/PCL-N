using System.Text.Json.Serialization;

namespace PCL.Services.Updates;

/// <summary>
/// The verified hand-off from the updater to the install step: a staged tree whose files all
/// match the target manifest, the install root they belong to, the launcher entry file, and
/// the managed leftovers that must be deleted. Property names are the plan file contract and
/// match the legacy install plan exactly.
/// </summary>
public sealed class UpdateInstallPlan
{
    public int FormatVersion { get; set; } = 1;

    public string? InstallRoot { get; set; }

    public string? EntryRelativePath { get; set; }

    public string? StagedRoot { get; set; }

    public List<UpdateFileEntry> Files { get; set; } = [];

    public List<string> DeletePaths { get; set; } = [];
}
