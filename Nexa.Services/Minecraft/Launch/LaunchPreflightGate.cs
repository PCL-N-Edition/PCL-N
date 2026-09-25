using Nexa.Services.Capabilities;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Launch;

public sealed record LaunchPreflightPrompt(Guid Attempt, CapabilityPreflightReport Report);
public sealed record LaunchPreflightDecision(Guid Attempt, bool Continue);

/// <summary>A single attempt's asynchronous, non-bypassable final launch gate.</summary>
public sealed class LaunchPreflightGate(XsrStateStore store,
    Func<string, string, MinecraftLaunchPlan, CancellationToken, Task<CapabilityPreflightReport>> evaluate)
{
    public static readonly XsrSemanticId StateKey = XsrSemanticId.Parse("minecraft.launch.preflight");
    public static readonly XsrSemanticId DecisionCommand = XsrSemanticId.Parse("minecraft.launch.preflight.decide");
    private readonly object _gate = new();
    private (Guid Id, bool Blocked, TaskCompletionSource<bool> Completion)? _pending;
    public static void DeclareState(XsrStateStoreBuilder builder) => builder.Cell<LaunchPreflightPrompt?>(StateKey, "Nexa.Services.Minecraft.Launch");

    public async Task<bool> CheckAsync(string root, string instance, MinecraftLaunchPlan plan, CancellationToken token)
    {
        CapabilityPreflightReport report = await evaluate(root, instance, plan, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (report.OverallSeverity < PreflightSeverity.Warning) return true;
        Guid attempt = Guid.NewGuid();
        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_pending is not null) throw new InvalidOperationException("A preflight decision is already pending.");
            _pending = (attempt, report.Issues.Any(static issue => issue.Severity == PreflightSeverity.Blocked), completion);
        }
        try
        {
            store.Publish(store.Resolve(StateKey), new LaunchPreflightPrompt(attempt, report), token);
            return await completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            store.Publish<LaunchPreflightPrompt?>(store.Resolve(StateKey), null, CancellationToken.None);
            lock (_gate) if (_pending?.Id == attempt) _pending = null;
        }
    }
    public bool Decide(LaunchPreflightDecision decision)
    {
        lock (_gate)
        {
            if (_pending is not { } pending || pending.Id != decision.Attempt || decision.Continue && pending.Blocked) return false;
            return pending.Completion.TrySetResult(decision.Continue);
        }
    }
}
