using Nexa.Xsr;

namespace Nexa.UI.Next;

[Flags]
public enum XsrUiClickModifiers { None = 0, Toggle = 1, Extend = 2 }

/// <summary>Optional modifier-click intents, separate from normal activation.</summary>
public sealed record XsrUiModifiedClick(XsrSemanticId Toggle, XsrSemanticId Extend, XsrSemanticId AddRange);
