using System.Diagnostics;

namespace PCL.Services.Updates;

/// <summary>
/// External process execution port. Production runs the real tool; tests substitute a fake
/// that asserts arguments and simulates the tool's effect.
/// </summary>
public interface IProcessRunner
{
    /// <summary>Runs the executable and returns its exit code (zero means success).</summary>
    Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

/// <summary>Real process execution over <see cref="Process"/>.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动外部工具：{executable}");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }
}

/// <summary>
/// HDiffPatch command-line integration: applies one patch payload over a source file to
/// produce the target file (`hpatchz source patch output`). Any nonzero exit is a hard
/// failure — the updater falls back to the full package rather than keeping a dubious file.
/// </summary>
public sealed class HDiffPatchTool
{
    public const string ToolName = "hpatchz";

    private readonly IProcessRunner _runner;
    private readonly string _executablePath;

    public HDiffPatchTool(IProcessRunner runner, string executablePath = ToolName)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = executablePath;
    }

    public async Task ApplyAsync(
        string sourceFile,
        string patchFile,
        string outputFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(patchFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);
        int exit = await _runner.RunAsync(
            _executablePath,
            [sourceFile, patchFile, outputFile],
            cancellationToken).ConfigureAwait(false);
        if (exit != 0)
        {
            throw new InvalidDataException($"HDiffPatch 应用补丁失败（exit {exit}）。");
        }
    }
}
