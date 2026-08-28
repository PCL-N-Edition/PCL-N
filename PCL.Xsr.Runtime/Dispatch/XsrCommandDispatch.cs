namespace PCL.Xsr.Runtime;

/// <summary>
/// Separates immediate command acceptance from asynchronous business completion.
/// </summary>
public sealed class XsrCommandDispatch
{
    internal XsrCommandDispatch(
        XsrCorrelationId correlationId,
        XsrCommandId commandId,
        XsrResult acceptance,
        Task<XsrResult> completion)
    {
        CorrelationId = correlationId;
        CommandId = commandId;
        Acceptance = acceptance;
        Completion = completion;
    }

    public XsrCorrelationId CorrelationId { get; }

    public XsrCommandId CommandId { get; }

    public XsrResult Acceptance { get; }

    public Task<XsrResult> Completion { get; }
}
