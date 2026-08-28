using System.Diagnostics.CodeAnalysis;

namespace PCL.Xsr;

/// <summary>
/// Represents success or a documented XSR error without a return value.
/// </summary>
public sealed class XsrResult
{
    private static readonly XsrResult SuccessfulResult = new(null);

    private XsrResult(XsrError? error)
    {
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public XsrError? Error { get; }

    public static XsrResult Success() => SuccessfulResult;

    public static XsrResult Failure(XsrError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new XsrResult(error);
    }

    public static XsrResult<T> Success<T>(T value) => new(value, null);

    public static XsrResult<T> Failure<T>(XsrError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new XsrResult<T>(default, error);
    }
}

/// <summary>
/// Represents success with a typed value or a documented XSR error.
/// </summary>
public sealed class XsrResult<T>
{
    private readonly T? _value;

    internal XsrResult(T? value, XsrError? error)
    {
        _value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public XsrError? Error { get; }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("A failed XSR result does not contain a value.");

    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = _value;
        return IsSuccess;
    }
}
