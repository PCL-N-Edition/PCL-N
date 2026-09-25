using Nexa.Xsr;

namespace Nexa.UI.Next;

[Flags]
public enum XsrUiFileDragEffects { None = 0, Copy = 1, Move = 2, Link = 4 }

/// <summary>Immutable local file transfer projection; native backends own the OS operation.</summary>
public sealed class XsrUiFileDrag
{
    public XsrUiFileDrag(IEnumerable<string> paths, XsrUiFileDragEffects effects, XsrSemanticId completed)
    {
        Paths = Array.AsReadOnly(paths.ToArray());
        Effects = effects;
        Completed = completed;
    }
    public IReadOnlyList<string> Paths { get; }
    public XsrUiFileDragEffects Effects { get; }
    public XsrSemanticId Completed { get; }
}
