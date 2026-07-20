// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using PCL.Application.Launching;

namespace PCL.Desktop.Features.Launching;

/// <summary>Pure decision helpers for conventional / AI repair selection.</summary>
internal static class MinecraftRepairPolicy
{
    public static MinecraftRepairActionKind SelectConventionalRepairAction(
        MinecraftLaunchFaultReport fault,
        IReadOnlyList<MinecraftMissingDependency> dependencies,
        string? nativesDirectory)
    {
        if (fault.Code == MinecraftLaunchFaultCode.NativeLibraryFailed &&
            !string.IsNullOrWhiteSpace(nativesDirectory))
            return MinecraftRepairActionKind.ReextractNatives;
        if (fault.Code == MinecraftLaunchFaultCode.MissingModDependency && dependencies.Count > 0)
            return MinecraftRepairActionKind.InstallMissingModDependencies;
        return fault.Code switch
        {
            MinecraftLaunchFaultCode.MainClassMissing or MinecraftLaunchFaultCode.ClasspathDependencyMissing =>
                MinecraftRepairActionKind.RepairVersionFiles,
            MinecraftLaunchFaultCode.JavaRuntimeMissing or MinecraftLaunchFaultCode.JavaRuntimeIncompatible or
                MinecraftLaunchFaultCode.JvmInitializationFailed => MinecraftRepairActionKind.SelectCompatibleJava,
            _ => MinecraftRepairActionKind.InspectOnly
        };
    }

    public static bool IsAutomaticallyExecutableRepair(MinecraftRepairActionKind action) => action is
        MinecraftRepairActionKind.RepairVersionFiles or
        MinecraftRepairActionKind.ReextractNatives or
        MinecraftRepairActionKind.InstallMissingModDependencies or
        MinecraftRepairActionKind.DownloadMod or
        MinecraftRepairActionKind.DisableMod or
        MinecraftRepairActionKind.UpdateMod or
        MinecraftRepairActionKind.SelectCompatibleJava or
        MinecraftRepairActionKind.DownloadCompatibleJava or
        MinecraftRepairActionKind.ReinstallVersionAndUpdateLoader or
        MinecraftRepairActionKind.DisableExperimentalJvmHost;

    public static bool ShouldExecuteConventionalRepairDirectly(
        bool isFirstAttempt,
        bool automaticRepairEnabled,
        bool experimentalAiRepairEnabled) =>
        isFirstAttempt && automaticRepairEnabled && !experimentalAiRepairEnabled;

    public static string BuildRepairAttemptSummary(
        string source,
        string actions,
        MinecraftRepairExecutionResult result) =>
        $"来源={source}；动作={actions}；执行结果={result.Message}；" +
        $"实际修改文件={(result.MadeChanges ? "是" : "否")}；执行失败={(result.IsFailure ? "是" : "否")}。";

    public static string FormatFailedRepairFeedback(
        string? previousRepairSummary,
        MinecraftLaunchFaultCode currentCode,
        string? currentStage,
        int? processExitCode)
    {
        string summary = string.IsNullOrWhiteSpace(previousRepairSummary)
            ? "上次修复内容未记录"
            : previousRepairSummary.Trim();
        return $"上次修复已执行，但修复后的重新启动仍然失败。上次修复：{summary}" +
               $" 本次失败：Code={currentCode}；Stage={currentStage ?? "Unknown"}；" +
               $"ExitCode={processExitCode?.ToString(CultureInfo.InvariantCulture) ?? "Unknown"}。" +
               "请结合本次新错误重新判断，并避免无依据重复上次修复。";
    }

    public static string DescribeAiRepairStep(
        MinecraftRepairActionKind action,
        MinecraftAiRepairParameters parameters) =>
        action switch
        {
            MinecraftRepairActionKind.DownloadMod =>
                $"下载模组 {parameters.ModId} {parameters.ModVersion}",
            MinecraftRepairActionKind.DisableMod => $"禁用模组 {parameters.ModId}",
            MinecraftRepairActionKind.UpdateMod =>
                $"将模组 {parameters.ModId} 更新至 {parameters.ModVersion}",
            MinecraftRepairActionKind.DisableExperimentalJvmHost =>
                "关闭实验性 Jvm.NET Host，并改用传统 Java 进程启动",
            MinecraftRepairActionKind.SelectCompatibleJava => "切换至另一套已安装的兼容 Java",
            MinecraftRepairActionKind.DownloadCompatibleJava => "下载并选择兼容 Java",
            MinecraftRepairActionKind.ReinstallVersionAndUpdateLoader => "重新安装版本并更新模组加载器",
            MinecraftRepairActionKind.RepairVersionFiles => "重新校验并补全 Minecraft 版本文件",
            MinecraftRepairActionKind.ReextractNatives => "重新生成 Minecraft Natives",
            MinecraftRepairActionKind.InstallMissingModDependencies => "下载缺失的前置模组",
            _ => action.ToString()
        };
}
