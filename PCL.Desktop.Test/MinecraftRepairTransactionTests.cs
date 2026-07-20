// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Application.Launching;
using PCL.Desktop.Features.Launching;
using PCL.Desktop.Views;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class MinecraftRepairTransactionTests
{
    [TestMethod]
    public async Task HasChanges_OnlyTracksRegisteredMutations()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcln-repair-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using MinecraftRepairTransaction transaction = new();
            Assert.IsFalse(transaction.HasChanges);

            await transaction.BackupFileAsync(Path.Combine(root, "created.txt"), CancellationToken.None);

            Assert.IsTrue(transaction.HasChanges);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void DisableExperimentalJvmHost_IsAutomaticallyExecutable()
    {
        Assert.IsTrue(MainWindow.IsAutomaticallyExecutableRepairForTest(
            MinecraftRepairActionKind.DisableExperimentalJvmHost));
        Assert.AreEqual(
            "关闭实验性 Jvm.NET Host，并改用传统 Java 进程启动",
            MainWindow.DescribeAiRepairStepForTest(MinecraftRepairActionKind.DisableExperimentalJvmHost));
    }

    [TestMethod]
    public void ConventionalRepairRouting_DefersToExperimentalAi()
    {
        Assert.IsFalse(MainWindow.ShouldExecuteConventionalRepairDirectly(
            isFirstAttempt: true,
            automaticRepairEnabled: true,
            experimentalAiRepairEnabled: true));
        Assert.IsTrue(MainWindow.ShouldExecuteConventionalRepairDirectly(
            isFirstAttempt: true,
            automaticRepairEnabled: true,
            experimentalAiRepairEnabled: false));
        Assert.IsFalse(MainWindow.ShouldExecuteConventionalRepairDirectly(
            isFirstAttempt: false,
            automaticRepairEnabled: true,
            experimentalAiRepairEnabled: false));
        Assert.IsFalse(MainWindow.ShouldExecuteConventionalRepairDirectly(
            isFirstAttempt: true,
            automaticRepairEnabled: false,
            experimentalAiRepairEnabled: false));
    }

    [TestMethod]
    public void FailedRepairFeedback_IncludesPreviousRepairAndCurrentFailure()
    {
        string feedback = MainWindow.FormatFailedRepairFeedback(
            "来源=AI 链式修复；动作=UpdateMod；执行结果=已更新 example；实际修改文件=是；执行失败=否。",
            MinecraftLaunchFaultCode.JvmInitializationFailed,
            "JvmMode",
            unchecked((int)0xC0000409));

        StringAssert.Contains(feedback, "上次修复已执行");
        StringAssert.Contains(feedback, "修复后的重新启动仍然失败");
        StringAssert.Contains(feedback, "动作=UpdateMod");
        StringAssert.Contains(feedback, "Code=JvmInitializationFailed");
        StringAssert.Contains(feedback, "Stage=JvmMode");
        StringAssert.Contains(feedback, "ExitCode=-1073740791");
        StringAssert.Contains(feedback, "避免无依据重复上次修复");
    }

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
