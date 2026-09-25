using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexa.Services.Settings;

namespace Nexa.Services.Minecraft.Management;

/// <summary>Coordinates file/settings compensation. The caller supplies current process/scope validation.</summary>
internal static class RecoveryTransactionCoordinator
{
    internal static async Task ExecuteAsync(RecoveryPreparedRestore prepared, IReadOnlyList<RecoveryFileEdit> files,
        RecoverySettingsPlan plan, SettingsPolicyService settings, Func<CancellationToken, Task> validate,
        bool restoreLeaseHeld = false, CancellationToken token = default)
    {
        RecoveryFileTransaction.Validate(prepared, new(prepared.Snapshot.InstanceDirectory, prepared.Snapshot.GameDirectory), files);
        CheckScope(prepared, plan);
        using var exclusive = restoreLeaseHeld ? null : await InstanceRecoveryOperationGate.EnterRestoreAsync(Root(prepared), token).ConfigureAwait(false);
        await validate(token).ConfigureAwait(false);
        string hash = await PrepareAsync(prepared, plan, token).ConfigureAwait(false);
        bool settingsAttempted = false;
        try
        {
            await RecoveryFileTransaction.ApplyAsync(prepared, files, token).ConfigureAwait(false);
            await validate(token).ConfigureAwait(false);
            await WriteStateAsync(prepared, hash, "settings-applying", true, token).ConfigureAwait(false);
            settingsAttempted = true;
            var applied = settings.ApplyRecoverySettingsPlan(plan, reverse: false);
            if (!applied.IsSuccess) throw new IOException(applied.Error?.Message ?? "无法提交恢复设置。");
            // Settings may already be durable: finish the commit record even if the request was cancelled.
            await WriteStateAsync(prepared, hash, "committed", true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            try { await RollbackCoreAsync(prepared, plan, hash, settingsAttempted, settings, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception rollbackError) when (rollbackError is not OutOfMemoryException and not AccessViolationException)
            { throw new AggregateException("恢复事务未完成，已保留文件与设置备份。", error, rollbackError); }
            throw;
        }
    }

    internal static async Task RecoverAsync(RecoveryPreparedRestore prepared, SettingsPolicyService settings,
        Func<CancellationToken, Task> validate, CancellationToken token = default) =>
        await RecoverUnderLeaseAsync(prepared, settings, validate, false, token).ConfigureAwait(false);

    internal static async Task RecoverUnderLeaseAsync(RecoveryPreparedRestore prepared, SettingsPolicyService settings,
        Func<CancellationToken, Task> validate, bool restoreLeaseHeld, CancellationToken token = default)
    {
        using var exclusive = restoreLeaseHeld ? null : await InstanceRecoveryOperationGate.EnterRestoreAsync(Root(prepared), token).ConfigureAwait(false);
        await validate(token).ConfigureAwait(false);
        byte[] bytes = await RecoveryFileTransaction.ReadRecordAsync(Path.Combine(prepared.Directory, "settings-plan.json"), 4 * 1024 * 1024, token).ConfigureAwait(false);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        var plan = SettingsPolicyService.DecodeRecoverySettingsPlan(JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException("恢复设置计划无效。"));
        CheckScope(prepared, plan);
        byte[] marker = await RecoveryFileTransaction.ReadRecordAsync(Path.Combine(prepared.Directory, "transaction.json"), 4096, token).ConfigureAwait(false);
        var state = JsonNode.Parse(marker) as JsonObject ?? throw new InvalidDataException("恢复事务进度无效。");
        if (state["version"]?.GetValue<int>() != 1 || state["transaction"]?.GetValue<string>() != prepared.TransactionId.ToString("D")
            || state["baseline"]?.GetValue<string>() != prepared.Snapshot.Revision.ToString("D") || state["settingsPlanSha256"]?.GetValue<string>() != hash)
            throw new InvalidDataException("恢复事务身份或设置计划已变化。");
        string? phase = state["phase"]?.GetValue<string>();
        if (phase is "committed" or "rolled-back") return;
        if (phase is not ("prepared" or "settings-applying" or "rolling-back")) throw new InvalidDataException("恢复事务阶段无效。");
        await RollbackCoreAsync(prepared, plan, hash, state["settingsAttempted"]!.GetValue<bool>(), settings, token).ConfigureAwait(false);
    }

    internal static async Task<string> PrepareAsync(RecoveryPreparedRestore prepared, RecoverySettingsPlan plan, CancellationToken token = default)
    {
        CheckScope(prepared, plan);
        foreach (string name in new[] { "settings-plan.json", "transaction.json" })
        {
            string path = Path.Combine(prepared.Directory, name); RecoveryBlobStore.CheckLinks(path);
            if (File.Exists(path)) throw new IOException("恢复事务已有设置计划，请先处理未完成记录。");
        }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(SettingsPolicyService.EncodeRecoverySettingsPlan(plan), RecoveryJsonContext.Default.JsonObject);
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("恢复设置计划过大。");
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        await RecoveryFileTransaction.WriteRecordAsync(prepared, "settings-plan.json", bytes, token).ConfigureAwait(false);
        await WriteStateAsync(prepared, hash, "prepared", false, token).ConfigureAwait(false);
        return hash;
    }

    private static async Task RollbackCoreAsync(RecoveryPreparedRestore prepared, RecoverySettingsPlan plan, string hash,
        bool settingsAttempted, SettingsPolicyService settings, CancellationToken token)
    {
        await WriteStateAsync(prepared, hash, "rolling-back", settingsAttempted, token).ConfigureAwait(false);
        if (settingsAttempted && !settings.ApplyRecoverySettingsPlan(plan, reverse: true).IsSuccess)
            throw new IOException("启动设置发生冲突，已保留恢复记录。");
        string files = Path.Combine(prepared.Directory, "apply.json"); RecoveryBlobStore.CheckLinks(files);
        if (File.Exists(files)) await RecoveryFileTransaction.RollbackAsync(prepared, token).ConfigureAwait(false);
        await WriteStateAsync(prepared, hash, "rolled-back", settingsAttempted, token).ConfigureAwait(false);
    }

    private static string Root(RecoveryPreparedRestore prepared)
    {
        var parent = Directory.GetParent(prepared.Snapshot.InstanceDirectory);
        if (parent?.Name != "versions" || parent.Parent is null) throw new InvalidDataException("恢复实例目录无效。");
        return parent.Parent.FullName;
    }

    private static void CheckScope(RecoveryPreparedRestore prepared, RecoverySettingsPlan plan)
    {
        SettingsPolicyService.ValidateRecoverySettingsPlan(plan);
        string expected = Path.Combine(prepared.Snapshot.InstanceDirectory, "Nexa", "Recovery", "transactions", prepared.TransactionId.ToString("N"));
        if (!MinecraftLibraryService.PathComparer.Equals(plan.InstanceDirectory, prepared.Snapshot.InstanceDirectory)
            || !MinecraftLibraryService.PathComparer.Equals(expected, prepared.Directory)) throw new InvalidDataException("恢复设置不属于当前事务实例。");
        RecoveryBlobStore.CheckLinks(expected);
    }

    private static Task WriteStateAsync(RecoveryPreparedRestore prepared, string hash, string phase, bool settingsAttempted, CancellationToken token)
    {
        var state = new JsonObject
        {
            ["version"] = 1,
            ["transaction"] = prepared.TransactionId.ToString("D"),
            ["baseline"] = prepared.Snapshot.Revision.ToString("D"),
            ["phase"] = phase,
            ["settingsPlanSha256"] = hash,
            ["settingsAttempted"] = settingsAttempted
        };
        return RecoveryFileTransaction.WriteRecordAsync(prepared, "transaction.json", JsonSerializer.SerializeToUtf8Bytes(state, RecoveryJsonContext.Default.JsonObject), token);
    }
}
