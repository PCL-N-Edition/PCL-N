using Nexa.Services.Updates;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Services.Composition;

public static class NexaUpdateRuntimeComposer
{
    private sealed class Observer : IXsrDispatchObserver
    {
        public void OnCompleted(XsrDispatchObservation observation) { }
    }
    public static XsrQueryRouter Compose(NexaUpdateService service)
    {
        XsrQueryRouterBuilder queries = new();
        queries.Register<NexaUpdateQuery, NexaUpdateStatus>(NexaUpdateContract.Check,
            (query, token) => new(service.CheckAsync(query, token)));
        return queries.Build(new Observer());
    }
}
