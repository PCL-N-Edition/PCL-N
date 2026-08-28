using System.Threading.Channels;

namespace PCL.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static ValueTask RoutersAssignDeterministicTypedIdentifiers()
    {
        XsrSemanticId alpha = XsrSemanticId.Parse("command.alpha");
        XsrSemanticId zeta = XsrSemanticId.Parse("command.zeta");
        RecordingDispatchObserver observer = new();

        XsrCommandRouterBuilder firstBuilder = new();
        firstBuilder.Register<TestCommand>(zeta, SuccessfulCommand);
        firstBuilder.Register<TestCommand>(alpha, SuccessfulCommand);

        XsrCommandRouterBuilder secondBuilder = new();
        secondBuilder.Register<TestCommand>(alpha, SuccessfulCommand);
        secondBuilder.Register<TestCommand>(zeta, SuccessfulCommand);

        XsrCommandRouter first = firstBuilder.Build(observer);
        XsrCommandRouter second = secondBuilder.Build(observer);

        AssertTrue(first.TryResolve(alpha, out XsrCommandId firstAlpha));
        AssertTrue(second.TryResolve(alpha, out XsrCommandId secondAlpha));
        AssertTrue(first.TryResolve(zeta, out XsrCommandId firstZeta));
        AssertEqual(firstAlpha, secondAlpha);
        AssertEqual(1u, firstAlpha.Value.Value);
        AssertEqual(2u, firstZeta.Value.Value);
        return ValueTask.CompletedTask;
    }

    private static async ValueTask CommandAcceptanceIsSeparateFromCompletion()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingDispatchObserver observer = new();
        XsrCommandRouterBuilder builder = new();
        XsrSemanticId semanticId = XsrSemanticId.Parse("command.delayed");

        builder.Register<TestCommand>(semanticId, async (_, cancellationToken) =>
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return XsrResult.Success();
        });

        XsrCommandRouter router = builder.Build(observer);
        AssertTrue(router.TryResolve(semanticId, out XsrCommandId commandId));

        XsrCommandDispatch dispatch = router.Dispatch(commandId, new TestCommand(7));
        AssertTrue(dispatch.Acceptance.IsSuccess);
        AssertTrue(dispatch.CorrelationId.IsAssigned);
        AssertFalse(dispatch.Completion.IsCompleted);

        gate.SetResult();
        XsrResult completion = await dispatch.Completion.ConfigureAwait(false);
        XsrDispatchObservation observation = await observer.TakeAsync().ConfigureAwait(false);

        AssertTrue(completion.IsSuccess);
        AssertTrue(observation.IsSuccess);
        AssertEqual(dispatch.CorrelationId, observation.CorrelationId);
        AssertEqual(commandId.Value, observation.RuntimeId);
    }

    private static async ValueTask DetachedCommandFailuresRemainObservable()
    {
        RecordingDispatchObserver observer = new();
        XsrCommandRouterBuilder builder = new();
        XsrSemanticId semanticId = XsrSemanticId.Parse("command.detached");
        XsrError rejected = new(
            XsrErrorKind.Rejected,
            XsrSemanticId.Parse("test.command_rejected"),
            "The test command was rejected.");

        builder.Register<TestCommand>(semanticId, async (_, _) =>
        {
            await Task.Yield();
            return XsrResult.Failure(rejected);
        });

        XsrCommandRouter router = builder.Build(observer);
        AssertTrue(router.TryResolve(semanticId, out XsrCommandId commandId));

        _ = router.Dispatch(commandId, new TestCommand(1));
        XsrDispatchObservation observation = await observer.TakeAsync().ConfigureAwait(false);

        AssertFalse(observation.IsSuccess);
        AssertEqual(rejected.Code, RequiredError(observation.Error).Code);
    }

    private static async ValueTask CommandCancellationHasAStableError()
    {
        RecordingDispatchObserver observer = new();
        XsrCommandRouterBuilder builder = new();
        XsrSemanticId semanticId = XsrSemanticId.Parse("command.cancel");

        builder.Register<TestCommand>(semanticId, async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return XsrResult.Success();
        });

        XsrCommandRouter router = builder.Build(observer);
        AssertTrue(router.TryResolve(semanticId, out XsrCommandId commandId));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        XsrCommandDispatch dispatch = router.Dispatch(
            commandId,
            new TestCommand(1),
            cancellationToken: cancellation.Token);
        XsrResult completion = await dispatch.Completion.ConfigureAwait(false);

        AssertTrue(dispatch.Acceptance.IsSuccess);
        AssertErrorCode(XsrRuntimeErrors.CancelledCode, completion.Error);
    }

    private static async ValueTask QueryResultsAndTimeoutsRemainDistinct()
    {
        RecordingDispatchObserver observer = new();
        XsrQueryRouterBuilder builder = new();
        XsrSemanticId valueId = XsrSemanticId.Parse("query.value");
        XsrSemanticId timeoutId = XsrSemanticId.Parse("query.timeout");

        builder.Register<TestQuery, int>(valueId, (query, _) =>
            ValueTask.FromResult(XsrResult.Success(query.Value * 2)));
        builder.Register<SlowQuery, int>(timeoutId, async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return XsrResult.Success(0);
        });

        XsrQueryRouter router = builder.Build(observer);
        AssertTrue(router.TryResolve(valueId, out XsrQueryId valueQueryId));
        AssertTrue(router.TryResolve(timeoutId, out XsrQueryId timeoutQueryId));

        XsrResult<int> valueResult = await router.QueryAsync<TestQuery, int>(
            valueQueryId,
            new TestQuery(21)).ConfigureAwait(false);
        XsrResult<int> timeoutResult = await router.QueryAsync<SlowQuery, int>(
            timeoutQueryId,
            new SlowQuery(),
            TimeSpan.FromMilliseconds(30)).ConfigureAwait(false);

        AssertTrue(valueResult.IsSuccess);
        AssertEqual(42, valueResult.Value);
        AssertErrorCode(XsrRuntimeErrors.TimedOutCode, timeoutResult.Error);
    }

    private static async ValueTask QueryContractMismatchesHaveAStableError()
    {
        RecordingDispatchObserver observer = new();
        XsrQueryRouterBuilder builder = new();
        XsrSemanticId semanticId = XsrSemanticId.Parse("query.contract");
        builder.Register<TestQuery, int>(semanticId, (query, _) =>
            ValueTask.FromResult(XsrResult.Success(query.Value)));

        XsrQueryRouter router = builder.Build(observer);
        AssertTrue(router.TryResolve(semanticId, out XsrQueryId queryId));

        XsrResult<int> mismatch = await router.QueryAsync<OtherQuery, int>(
            queryId,
            new OtherQuery()).ConfigureAwait(false);
        XsrResult<int> missing = await router.QueryAsync<TestQuery, int>(
            new XsrQueryId(new XsrRuntimeId(999)),
            new TestQuery(1)).ConfigureAwait(false);

        AssertErrorCode(XsrRuntimeErrors.ContractMismatchCode, mismatch.Error);
        AssertErrorCode(XsrRuntimeErrors.RouteNotFoundCode, missing.Error);
    }

    private static async ValueTask HandlerExceptionsDoNotEscapeRouting()
    {
        RecordingDispatchObserver observer = new();
        XsrCommandRouterBuilder builder = new();
        XsrSemanticId semanticId = XsrSemanticId.Parse("command.fault");
        builder.Register<TestCommand>(semanticId, (_, _) =>
            throw new InvalidOperationException("private handler detail"));

        XsrCommandRouter router = builder.Build(observer);
        AssertTrue(router.TryResolve(semanticId, out XsrCommandId commandId));

        XsrResult completion = await router.Dispatch(commandId, new TestCommand(1)).Completion
            .ConfigureAwait(false);
        XsrDispatchObservation observation = await observer.TakeAsync().ConfigureAwait(false);

        AssertErrorCode(XsrRuntimeErrors.HandlerFaultedCode, completion.Error);
        AssertEqual(typeof(InvalidOperationException).FullName, observation.FaultType);
        AssertFalse(RequiredError(completion.Error).Message.Contains("private handler detail", StringComparison.Ordinal));
    }

    private static async ValueTask CommandDispatchSupportsConcurrentCallers()
    {
        RecordingDispatchObserver observer = new();
        XsrCommandRouterBuilder builder = new();
        XsrSemanticId semanticId = XsrSemanticId.Parse("command.concurrent");
        int handled = 0;
        builder.Register<TestCommand>(semanticId, (_, _) =>
        {
            Interlocked.Increment(ref handled);
            return ValueTask.FromResult(XsrResult.Success());
        });

        XsrCommandRouter router = builder.Build(observer);
        AssertTrue(router.TryResolve(semanticId, out XsrCommandId commandId));

        Task<XsrResult>[] completions = Enumerable.Range(0, 256)
            .Select(index => router.Dispatch(commandId, new TestCommand(index)).Completion)
            .ToArray();
        XsrResult[] results = await Task.WhenAll(completions).ConfigureAwait(false);

        AssertEqual(256, handled);
        AssertTrue(results.All(result => result.IsSuccess));
    }

    private static ValueTask<XsrResult> SuccessfulCommand(
        TestCommand _,
        CancellationToken __) => ValueTask.FromResult(XsrResult.Success());

    private static void AssertErrorCode(XsrSemanticId expected, XsrError? error) =>
        AssertEqual(expected, RequiredError(error).Code);

    private static XsrError RequiredError(XsrError? error) =>
        error ?? throw new InvalidOperationException("Expected an XSR error.");

    private readonly record struct TestCommand(int Value);

    private readonly record struct TestQuery(int Value);

    private readonly record struct SlowQuery;

    private readonly record struct OtherQuery;

    private sealed class RecordingDispatchObserver : IXsrDispatchObserver
    {
        private readonly Channel<XsrDispatchObservation> _observations =
            Channel.CreateUnbounded<XsrDispatchObservation>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });

        public void OnCompleted(XsrDispatchObservation observation)
        {
            if (!_observations.Writer.TryWrite(observation))
            {
                throw new InvalidOperationException("The observation channel rejected a value.");
            }
        }

        public async ValueTask<XsrDispatchObservation> TakeAsync()
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            return await _observations.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
        }
    }
}
