using Nexa.Services.Setup;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Services.Composition;

public sealed record FirstRunRuntime(XsrCommandRouter Commands, XsrQueryRouter Queries);
public static class FirstRunRuntimeComposer
{
    private sealed class Observer : IXsrDispatchObserver
    {
        public void OnCompleted(XsrDispatchObservation observation) { }
    }
    public static FirstRunRuntime Compose(FirstRunService service)
    {
        XsrCommandRouterBuilder commands = new();
        commands.Register<FirstRunCompleteCommand>(FirstRunContract.Complete, (command, token) => new ValueTask<XsrResult>(service.CompleteAsync(command, token)));
        XsrQueryRouterBuilder queries = new();
        queries.Register<FirstRunQuery, FirstRunStatus>(FirstRunContract.Status, (_, _) => ValueTask.FromResult(XsrResult.Success(service.Read())));
        var observer = new Observer();
        return new(commands.Build(observer), queries.Build(observer));
    }
}
