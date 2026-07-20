// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;

namespace PCL.Desktop.Features.Launching;

/// <summary>
/// A narrow repair journal. Callers must register a path before mutating it; rollback restores
/// original bytes or removes files that did not exist at the beginning of the transaction.
/// </summary>
internal sealed class MinecraftRepairTransaction : IAsyncDisposable
{
    private readonly string _backupDirectory = Path.Combine(
        Path.GetTempPath(),
        "PCL-N",
        "MinecraftRepair",
        Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, BackupEntry> _entries = new(GetPathComparer());
    private readonly List<DirectoryBackupEntry> _directories = [];
    private readonly SemaphoreSlim _fileBackupGate = new(1, 1);
    private bool _completed;

    public bool HasChanges => _entries.Count > 0 || _directories.Count > 0;

    public async Task BackupFileAsync(string path, CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        await _fileBackupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_entries.ContainsKey(fullPath))
                return;

            bool existed = File.Exists(fullPath);
            string? backupPath = null;
            if (existed)
            {
                Directory.CreateDirectory(_backupDirectory);
                backupPath = Path.Combine(
                    _backupDirectory,
                    _entries.Count.ToString("D4", CultureInfo.InvariantCulture) + ".bak");
                await using FileStream source = new(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using FileStream target = new(
                    backupPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }
            _entries.Add(fullPath, new BackupEntry(fullPath, existed, backupPath));
        }
        finally
        {
            _fileBackupGate.Release();
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (_completed)
            return;
        foreach (DirectoryBackupEntry directory in _directories.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(directory.Path))
                Directory.Delete(directory.Path, recursive: true);
            if (directory.Existed && Directory.Exists(directory.BackupPath))
                Directory.Move(directory.BackupPath, directory.Path);
        }
        foreach (BackupEntry entry in _entries.Values.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.Existed)
            {
                if (File.Exists(entry.Path))
                    File.Delete(entry.Path);
                continue;
            }
            if (entry.BackupPath is null || !File.Exists(entry.BackupPath))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(entry.Path)!);
            await using FileStream source = File.OpenRead(entry.BackupPath);
            await using FileStream target = new(
                entry.Path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        _completed = true;
        DeleteBackupDirectory();
    }

    public void Commit()
    {
        if (_completed)
            return;
        foreach (DirectoryBackupEntry directory in _directories)
        {
            if (Directory.Exists(directory.BackupPath))
                Directory.Delete(directory.BackupPath, recursive: true);
        }
        _completed = true;
        DeleteBackupDirectory();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_completed)
                await RollbackAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
        finally
        {
            _fileBackupGate.Dispose();
        }
    }

    private void DeleteBackupDirectory()
    {
        if (Directory.Exists(_backupDirectory))
            Directory.Delete(_backupDirectory, recursive: true);
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed record BackupEntry(string Path, bool Existed, string? BackupPath);

    private sealed record DirectoryBackupEntry(string Path, bool Existed, string BackupPath);

    public void BackupDirectoryByMove(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (_directories.Any(entry => GetPathComparer().Equals(entry.Path, fullPath)))
            return;
        bool existed = Directory.Exists(fullPath);
        string backupPath = fullPath + ".pcln-repair-" + Guid.NewGuid().ToString("N") + ".bak";
        if (existed)
            Directory.Move(fullPath, backupPath);
        _directories.Add(new DirectoryBackupEntry(fullPath, existed, backupPath));
    }

    public void TrackCreatedDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (_directories.Any(entry => GetPathComparer().Equals(entry.Path, fullPath)))
            return;
        _directories.Add(new DirectoryBackupEntry(
            fullPath,
            Existed: false,
            fullPath + ".pcln-unused-" + Guid.NewGuid().ToString("N")));
    }
}
