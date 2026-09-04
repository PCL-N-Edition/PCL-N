using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Desktop.Ui;

internal static class AccountFormState
{
    private const string Owner = "PCL.Desktop.AccountForm";
    private static readonly string[] BoolNames = ["open", "providers", "offline", "third-party", "import", "device", "busy", "submit", "code", "characters", "feedback-visible", "status-visible"];
    private static readonly string[] TextNames = ["mode", "title", "status", "user-code", "import-path", "feedback", "submit-label"];
    private static readonly Dictionary<string, XsrSemanticId> Keys = BoolNames.Concat(TextNames)
        .ToDictionary(name => name, name => XsrSemanticId.Parse("account.form." + name), StringComparer.Ordinal);
    public static XsrSemanticId Key(string name) => Keys[name];
    public static readonly XsrSemanticId Open = Key("open");
    public static readonly XsrSemanticId Mode = Key("mode");
    public static readonly XsrSemanticId ImportPath = Key("import-path");
    public static readonly XsrSemanticId Feedback = Key("feedback");
    public static void DeclareState(XsrStateStoreBuilder builder)
    {
        foreach (string name in BoolNames)
            builder.Cell<bool>(Key(name), Owner);
        foreach (string name in TextNames)
            builder.Cell<string>(Key(name), Owner);
    }
}
