// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Hosting.RuntimeExtensions;

namespace PCL.Desktop.Hosting.PluginSidecar;

/// <summary>
/// Host-side feedback bridge: submission runs in the CoreCLR sidecar (device session + GitHub).
/// </summary>
internal sealed class PluginSidecarFeedbackSubmissionHandler : IHostFeedbackSubmissionHandler
{
    public async Task<HostFeedbackSubmissionResult> SubmitAsync(
        HostFeedbackDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!PluginSidecarSupervisor.Instance.IsAvailable)
        {
            bool started = await PluginSidecarSupervisor.Instance.TryStartAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!started)
                throw new InvalidOperationException("插件侧车未运行，无法提交反馈。");
        }

        PluginSidecarClient client = PluginSidecarSupervisor.Instance.Client
            ?? throw new InvalidOperationException("插件侧车未连接。");

        PluginSidecarResult session = await client.FeedbackSessionAsync(cancellationToken).ConfigureAwait(false);
        if (!session.HasSession)
            throw new InvalidOperationException("请先在「设置 → 在线 → 账户」中连接 PCL N 在线服务账户，再提交反馈。");

        PluginSidecarResult result = await client.FeedbackSubmitAsync(
                draft.Category,
                draft.Title,
                draft.Description,
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Ok)
            throw new InvalidOperationException(result.Message ?? "提交失败。");

        return new HostFeedbackSubmissionResult(
            true,
            result.Message ?? $"Issue #{result.IssueNumber} 已创建。",
            result.IssueUrl);
    }
}
