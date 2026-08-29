using System.IO.Compression;
using System.Security.Cryptography;

namespace PCL.Services.Updates;

/// <summary>
/// Downloads one patch payload to a destination path. The orchestration layer wires this to
/// the download capability; tests substitute an in-memory writer.
/// </summary>
public delegate Task DownloadPatchHandler(UpdatePatchStep step, string destinationPath, CancellationToken cancellationToken);

/// <summary>
/// Applies planned patch steps to produce a staged target: full-file HDiffPatch chains over
/// the current binary, and scatter bundle operations (hdiff / add / replace / delete) into a
/// staged tree that ends up matching the bundle manifest exactly. Every payload is verified
/// by SHA-256 and size before use; every output is verified after.
/// </summary>
public sealed class UpdatePatchApplier
{
    private readonly IProcessRunner _runner;
    private readonly HDiffPatchTool _tool;

    public UpdatePatchApplier(IProcessRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _tool = new HDiffPatchTool(runner);
    }

    /// <summary>
    /// Applies a full-file patch chain over the running binary. The current file must match
    /// the first step's source digest; each downloaded patch is verified by SHA-256 and size
    /// before the tool runs; the final output must match the last step's target digest before
    /// it is moved to <paramref name="stagedOutputPath"/>. Temporary files never survive.
    /// </summary>
    public async Task ApplyBinaryChainAsync(
        IReadOnlyList<UpdatePatchStep> steps,
        string currentBinaryPath,
        string stagedOutputPath,
        DownloadPatchHandler downloadPatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(downloadPatch);
        if (steps.Count == 0)
        {
            throw new InvalidDataException("补丁链为空。");
        }

        string workRoot = Path.Combine(
            Path.GetDirectoryName(stagedOutputPath) ?? ".",
            ".patch-work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);
        try
        {
            await VerifyFileDigestAsync(currentBinaryPath, steps[0].FromSha256).ConfigureAwait(false);
            string input = currentBinaryPath;
            for (int index = 0; index < steps.Count; index++)
            {
                UpdatePatchStep step = steps[index];
                string patchPath = Path.Combine(workRoot, $"patch-{index}.bin");
                await downloadPatch(step, patchPath, cancellationToken).ConfigureAwait(false);
                await VerifyFileDigestAsync(patchPath, step.Sha256).ConfigureAwait(false);
                long patchSize = new FileInfo(patchPath).Length;
                if (step.Size > 0 && patchSize != step.Size)
                {
                    throw new InvalidDataException($"补丁大小不匹配：{step.DownloadUrl}");
                }

                string output = index == steps.Count - 1
                    ? Path.Combine(workRoot, "final")
                    : Path.Combine(workRoot, $"hop-{index}.bin");
                await _tool.ApplyAsync(input, patchPath, output, cancellationToken).ConfigureAwait(false);
                if (index == steps.Count - 1)
                {
                    await VerifyFileDigestAsync(output, step.TargetSha256).ConfigureAwait(false);
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(stagedOutputPath))!);
                    File.Move(output, stagedOutputPath, overwrite: true);
                }
                else
                {
                    input = output;
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(workRoot, recursive: true);
            }
            catch (IOException)
            {
                // Work-dir cleanup must not mask the transfer outcome.
            }
        }
    }

    /// <summary>
    /// Applies scatter bundle operations into a staged tree: `hdiff` transforms the current
    /// installation file through an HDiffPatch payload, `add`/`replace` stage a verified
    /// bundle blob, and `delete` stages nothing. The staged tree is then verified against the
    /// manifest's target files, so a returned plan is exactly the bundle's promise.
    /// </summary>
    public async Task ApplyScatterOpsAsync(
        UpdateScatterPatchManifest manifest,
        string bundleZipPath,
        string sourceRoot,
        string stagedRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleZipPath);
        Directory.CreateDirectory(stagedRoot);
        using ZipArchive bundle = ZipFile.OpenRead(bundleZipPath);

        foreach (UpdateScatterPatchOperation operation in manifest.Ops)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = UpdateStaging.NormalizeRelativePath(operation.Path);
            string output = UpdateStaging.ResolveSafeRelativePath(stagedRoot, operation.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            switch (operation.Op)
            {
                case "hdiff":
                    {
                        string source = UpdateStaging.ResolveSafeRelativePath(sourceRoot, operation.Path);
                        await VerifyFileDigestAsync(source, operation.FromSha256
                            ?? throw new InvalidDataException($"hdiff 操作缺少源文件校验：{relative}")).ConfigureAwait(false);
                        string payload = UpdateStaging.ResolveSafeRelativePath(stagedRoot, ".payload-" + Guid.NewGuid().ToString("N"));
                        try
                        {
                            await ExtractMemberAsync(bundle, operation.Patch, payload, operation.PatchSha256, operation.PatchSize)
                                .ConfigureAwait(false);
                            await _tool.ApplyAsync(source, payload, output, cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            TryDelete(payload);
                        }

                        break;
                    }
                case "add":
                case "replace":
                    await ExtractMemberAsync(bundle, operation.Blob, output, operation.BlobSha256, operation.BlobSize)
                        .ConfigureAwait(false);
                    break;
                case "delete":
                    break; // Absence in the staged tree is the delete.
                default:
                    throw new InvalidDataException($"不支持的散包操作：{operation.Op}。");
            }
        }

        // The staged tree must satisfy the manifest's own target manifest before anyone plans
        // an installation from it.
        UpdateStaging.VerifyStagedTree(stagedRoot, manifest.TargetFiles);
        foreach (UpdateFileEntry file in manifest.TargetFiles)
        {
            if (file.UnixMode is int mode && mode >= 0 && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    UpdateStaging.ResolveSafeRelativePath(stagedRoot, file.Path),
                    (UnixFileMode)mode);
            }
        }
    }

    private static async Task ExtractMemberAsync(
        ZipArchive bundle,
        string? memberPath,
        string destination,
        string? sha256,
        long size)
    {
        string normalized = UpdateStaging.NormalizeRelativePath(memberPath);
        if (normalized.Length == 0)
        {
            throw new InvalidDataException("散包操作缺少载荷成员。");
        }

        ZipArchiveEntry? entry = bundle.Entries.FirstOrDefault(candidate =>
            string.Equals(
                UpdateStaging.NormalizeRelativePath(candidate.FullName),
                normalized,
                StringComparison.Ordinal))
            ?? throw new InvalidDataException($"散包缺少载荷成员：{memberPath}");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        using (Stream content = entry.Open())
        {
            using FileStream output = new(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.SequentialScan);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[128 * 1024];
            long written = 0;
            while (true)
            {
                int read = await content.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer.AsSpan(0, read));
                await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                written += read;
            }

            if (size > 0 && written != size)
            {
                throw new InvalidDataException($"散包载荷大小不匹配：{memberPath}");
            }

            if (!string.IsNullOrWhiteSpace(sha256)
                && !string.Equals(
                    Convert.ToHexStringLower(hash.GetHashAndReset()),
                    sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"散包载荷 SHA-256 校验失败：{memberPath}");
            }
        }
    }

    private static async Task VerifyFileDigestAsync(string path, string expectedSha256)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        string actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"文件 SHA-256 校验失败：{path}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Cleanup must not mask the transfer outcome.
        }
    }
}
