using Nexa.Services.Files;
using Nexa.Services.Settings;
using Nexa.Xsr;

namespace Nexa.Services.Setup;

public sealed record FirstRunStatus(bool Required, string DataDirectory, bool LocationLocked)
{
    public bool TelemetryRequired { get; init; }
}
public sealed record FirstRunQuery;
public sealed record FirstRunCompleteCommand(string DataDirectory, bool Telemetry);
public static class FirstRunContract
{
    public static readonly XsrSemanticId Status = XsrSemanticId.Parse("setup.status");
    public static readonly XsrSemanticId Complete = XsrSemanticId.Parse("setup.complete");
}

public sealed class FirstRunService(string defaultDirectory, string locatorPath, bool locationLocked = false, string? productVersion = null)
{
    private readonly Lock _gate = new();
    private bool _completed;
    public FirstRunStatus Read()
    {
        string root = Path.GetFullPath(defaultDirectory);
        bool existing = File.Exists(Path.Combine(root, FolderNames.Settings, "settings.json"))
            || File.Exists(Path.Combine(root, FolderNames.Profiles, "profiles.json"));
        return new(!existing && !Volatile.Read(ref _completed), root, locationLocked)
        { TelemetryRequired = Telemetry.LauncherTelemetryPolicy.IsRequired(productVersion) };
    }

    public Task<XsrResult> CompleteAsync(FirstRunCompleteCommand command, CancellationToken token = default) =>
        Task.Run(() =>
        {
            lock (_gate)
            {
                string? createdSettings = null;
                string? createdSettingsFolder = null;
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (!Read().Required) throw new IOException("此安装已完成初始化，未修改已有设置。");
                    if (!Path.IsPathFullyQualified(command.DataDirectory)) throw new IOException("请选择完整的数据目录路径。");
                    string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(command.DataDirectory));
                    string current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(defaultDirectory));
                    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    if (locationLocked && !root.Equals(current, comparison)) throw new IOException("数据位置由环境变量指定，不能在引导中更改。");
                    if (root.Equals(Path.GetPathRoot(root), comparison)) throw new IOException("请选择一个文件夹，不要直接使用磁盘根目录。");
                    Directory.CreateDirectory(root);
                    for (string? ancestor = root; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
                        if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) throw new IOException("数据目录不能包含符号链接。");
                    if (!root.Equals(current, comparison) && Directory.EnumerateFileSystemEntries(root).Any())
                        throw new IOException("请选择空文件夹，已有数据不会被覆盖或迁移。");
                    string settings = Path.Combine(root, FolderNames.Settings, "settings.json");
                    string profilesFolder = Path.Combine(root, FolderNames.Profiles);
                    if (File.Exists(settings) || (Directory.Exists(profilesFolder) && Directory.EnumerateFileSystemEntries(profilesFolder).Any()))
                        throw new IOException("目标目录包含已有配置，未覆盖任何文件。");
                    string settingsFolder = Path.GetDirectoryName(settings)!;
                    if (Directory.Exists(settingsFolder) && (File.GetAttributes(settingsFolder) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("设置目录不能是符号链接。");
                    if (!Directory.Exists(settingsFolder)) createdSettingsFolder = settingsFolder;
                    Directory.CreateDirectory(settingsFolder);
                    // Reserve the path without overwriting a concurrently created installation.
                    using (new FileStream(settings, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                    createdSettings = settings;
                    new LauncherSettingsJsonPort(settings, LauncherDefaults.CreateSchema()).Save(
                        new Dictionary<string, string>(StringComparer.Ordinal) { ["TelemetryExperienceProgram"] = command.Telemetry || Read().TelemetryRequired ? "true" : "false" });
                    token.ThrowIfCancellationRequested();
                    if (!locationLocked) LauncherStorageLocation.Save(locatorPath, root);
                    createdSettings = null;
                    createdSettingsFolder = null;
                    Volatile.Write(ref _completed, true);
                    return XsrResult.Success();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
                { return XsrResult.Failure(new XsrError(XsrErrorKind.Rejected, XsrSemanticId.Parse("setup.save_failed"), error.Message)); }
                finally
                {
                    if (createdSettings is not null) File.Delete(createdSettings);
                    if (createdSettingsFolder is not null && !Directory.EnumerateFileSystemEntries(createdSettingsFolder).Any()) Directory.Delete(createdSettingsFolder);
                }
            }
        }, token);
}
