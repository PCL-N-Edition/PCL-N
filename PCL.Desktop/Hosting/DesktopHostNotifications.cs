// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using PCL.Application.Hosting.RuntimeExtensions;

namespace PCL.Desktop.Hosting;

/// <summary>
/// Forwards plugin notifications to the active main-window toast surface when attached;
/// otherwise captures messages for diagnostics (design §9 notifications).
/// </summary>
internal sealed class DesktopHostNotifications : IHostNotifications
{
    public static DesktopHostNotifications Instance { get; } = new();

    private readonly ConcurrentQueue<string> _captured = new();
    private Action<string, bool>? _handler;
    private Func<string, string, string, string, string, bool, Task<int>>? _choiceHandler;

    public IReadOnlyCollection<string> CapturedMessages => _captured.ToArray();

    public void Attach(Action<string, bool> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    public void AttachChoice(Func<string, string, string, string, string, bool, Task<int>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _choiceHandler = handler;
    }

    public void Detach(Action<string, bool> handler)
    {
        if (ReferenceEquals(_handler, handler))
            _handler = null;
    }

    public void DetachChoice(Func<string, string, string, string, string, bool, Task<int>> handler)
    {
        if (ReferenceEquals(_choiceHandler, handler))
            _choiceHandler = null;
    }

    public void ShowInformation(string message) => Dispatch(message, critical: false);

    public void ShowWarning(string message) => Dispatch(message, critical: true);

    public async Task<bool> ConfirmAsync(
        string title,
        string message,
        string primaryButton = "允许",
        string secondaryButton = "拒绝",
        bool isWarn = true,
        CancellationToken cancellationToken = default)
    {
        int result = await ChoiceAsync(
                title,
                message,
                primaryButton,
                secondaryButton,
                thirdButton: string.Empty,
                isWarn,
                cancellationToken)
            .ConfigureAwait(false);
        return result == 1;
    }

    public Task<int> ChoiceAsync(
        string title,
        string markdown,
        string primaryButton,
        string secondaryButton = "",
        string thirdButton = "",
        bool isWarn = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Func<string, string, string, string, string, bool, Task<int>>? handler = _choiceHandler;
        if (handler is null)
        {
            _captured.Enqueue($"[choice-unhandled] {title}: {markdown}");
            return Task.FromResult(0);
        }

        return handler(title, markdown, primaryButton, secondaryButton, thirdButton, isWarn);
    }

    private void Dispatch(string message, bool critical)
    {
        string text = message ?? string.Empty;
        Action<string, bool>? handler = _handler;
        if (handler is not null)
        {
            try
            {
                handler(text, critical);
                return;
            }
            catch
            {
                // fall through to capture
            }
        }

        _captured.Enqueue((critical ? "[warn] " : "[info] ") + text);
    }
}
