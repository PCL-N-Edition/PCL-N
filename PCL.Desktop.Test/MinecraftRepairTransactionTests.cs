// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Desktop.Features.Launching;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class MinecraftRepairTransactionTests
{
    [TestMethod]
    public async Task Rollback_RestoresFilesAndMovedDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcln-repair-transaction-" + Guid.NewGuid().ToString("N"));
        string existing = Path.Combine(root, "version.json");
        string created = Path.Combine(root, "new-mod.jar");
        string natives = Path.Combine(root, "natives");
        try
        {
            Directory.CreateDirectory(natives);
            await File.WriteAllTextAsync(existing, "before");
            await File.WriteAllTextAsync(Path.Combine(natives, "native.dll"), "before-native");
            await using MinecraftRepairTransaction transaction = new();
            await transaction.BackupFileAsync(existing, CancellationToken.None);
            await transaction.BackupFileAsync(created, CancellationToken.None);
            transaction.BackupDirectoryByMove(natives);
            await File.WriteAllTextAsync(existing, "after");
            await File.WriteAllTextAsync(created, "created");
            Directory.CreateDirectory(natives);
            await File.WriteAllTextAsync(Path.Combine(natives, "replacement.dll"), "replacement");

            await transaction.RollbackAsync();

            Assert.AreEqual("before", await File.ReadAllTextAsync(existing));
            Assert.IsFalse(File.Exists(created));
            Assert.AreEqual("before-native", await File.ReadAllTextAsync(Path.Combine(natives, "native.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(natives, "replacement.dll")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
