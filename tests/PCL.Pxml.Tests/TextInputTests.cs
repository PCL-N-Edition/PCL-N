using PCL.UI.Next;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Pxml.Tests;

internal static partial class Program
{
    private static void TextInputDraftsNeverExposePasswords()
    {
        XsrStateStoreBuilder builder = new();
        builder.Cell<bool>(XsrSemanticId.Parse("form.enabled"), "test");
        XsrStateStore store = builder.Build();
        XsrStateId enabled = store.Resolve(XsrSemanticId.Parse("form.enabled"));
        store.Publish(enabled, true);
        XsrUiTree tree = new();
        XsrUiEntityId root = tree.Create("root");
        XsrUiEntityId field = PxmlUiLoader.Load(Compile("""
            <TextInput xmlns="N" Label="密码" IsPassword="true" Placeholder="密码" Enabled="{state form.enabled}" />
            """), tree, store, root);
        XsrUiRenderer renderer = new(tree, store);
        renderer.SetRoot(root);
        _ = renderer.Render();
        AssertTrue(renderer.Focus(field));
        AssertTrue(renderer.InsertText("secret123"));
        XsrUiTextInputSnapshot snapshot = renderer.Render().Nodes.Single(node => node.Entity == field).TextInput!.Value;
        AssertEqual("•••••••••", snapshot.DisplayText);
        AssertTrue(renderer.SetTextPreedit(field, "秘密"));
        AssertFalse(renderer.Render().Nodes.Any(node => node.ToString().Contains("secret123", StringComparison.Ordinal)
            || node.ToString().Contains("秘密", StringComparison.Ordinal)));
        AssertTrue(renderer.EditText(XsrUiTextEdit.SelectAll));
        AssertTrue(renderer.CopySelectedText() is null);
        AssertTrue(renderer.InsertText("e\u0301😀"));
        AssertTrue(renderer.EditText(XsrUiTextEdit.Backspace));
        AssertEqual("e\u0301", tree.GetComponent<XsrUiTextInput>(field)!.ReadDraft());
        AssertTrue(renderer.EditText(XsrUiTextEdit.Backspace));
        AssertEqual(string.Empty, tree.GetComponent<XsrUiTextInput>(field)!.ReadDraft());
        store.Publish(enabled, false);
        AssertFalse(renderer.InsertText("blocked"));
        AssertFalse(renderer.Focus(field));
        AssertThrows<PxmlCompileException>(() => Compile("<TextInput xmlns=\"N\" Label=\"密码\" Content=\"must-not-be-a-template-secret\" />"));
    }
}
