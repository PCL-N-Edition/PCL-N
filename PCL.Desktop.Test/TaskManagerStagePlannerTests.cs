// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using CommunityToolkit.Mvvm.Messaging;
using PCL.Desktop.Features.Tasks.Views;
using PCL.Desktop.Session;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class TaskManagerStagePlannerTests
{
    [TestMethod]
    public void Advance_KeepsAllPlannedStagesAndNeverRegresses()
    {
        TaskManagerSubTaskSnapshot[] plan = TaskManagerStagePlanner.Create(
            "准备安装",
            "下载版本信息",
            "下载游戏文件",
            "安装加载器",
            "完成安装");

        TaskManagerSubTaskSnapshot[] downloading = TaskManagerStagePlanner.Advance(
            plan,
            "下载版本描述",
            "1.21.1.json",
            0.2d);
        TaskManagerSubTaskSnapshot[] loader = TaskManagerStagePlanner.Advance(
            downloading,
            "准备安装加载器",
            "NeoForge",
            0.5d);
        TaskManagerSubTaskSnapshot[] repeatedDownload = TaskManagerStagePlanner.Advance(
            loader,
            "下载运行库",
            "loader library",
            0.6d);

        Assert.AreEqual(5, repeatedDownload.Length);
        Assert.AreEqual(TaskManagerTaskState.Finished, repeatedDownload[2].State);
        Assert.AreEqual(TaskManagerTaskState.Running, repeatedDownload[3].State);
        Assert.AreEqual("安装加载器", repeatedDownload[3].Name);
        Assert.AreEqual(TaskManagerTaskState.Waiting, repeatedDownload[4].State);
    }

    [TestMethod]
    public void Advance_MapsCommunityDownloadPastAddressResolution()
    {
        TaskManagerSubTaskSnapshot[] plan = TaskManagerStagePlanner.Create(
            "解析下载地址",
            "解析必需前置",
            "下载资源",
            "安装内容");

        TaskManagerSubTaskSnapshot[] resolved = TaskManagerStagePlanner.Advance(
            plan,
            "正在解析必需前置",
            "2 个前置",
            0.1d);
        TaskManagerSubTaskSnapshot[] downloading = TaskManagerStagePlanner.Advance(
            resolved,
            "正在下载 sodium.jar",
            "sodium.jar",
            0.5d);

        Assert.AreEqual(TaskManagerTaskState.Finished, downloading[1].State);
        Assert.AreEqual(TaskManagerTaskState.Running, downloading[2].State);
        Assert.AreEqual("sodium.jar", downloading[2].Detail);
    }

    [TestMethod]
    public void SessionStore_RaisesSnapshotChangeForEveryRealMutation()
    {
        TaskSessionStore store = new(new WeakReferenceMessenger());
        int changes = 0;
        store.SnapshotsChanged += (_, _) => changes++;

        store.Upsert("task", new TaskManagerEntrySnapshot(
            "task", "title", "stage", "detail", 0d, 0, 0, 0, TaskManagerTaskState.Waiting));
        store.Remove("missing");
        store.Remove("task");
        store.Clear();

        Assert.AreEqual(2, changes);
    }
}
