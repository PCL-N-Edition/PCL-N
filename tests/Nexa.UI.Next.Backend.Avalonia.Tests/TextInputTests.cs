using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Input.TextInput;
using Avalonia.VisualTree;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static void VerifyNativeTextEditing(AvaloniaUiShellWindow window, XsrUiShell shell, AvaloniaUiSceneSurface surface)
    {
        XsrUiEntityId previous = shell.Stage.Navigation.Current;
        XsrUiEntityId page = shell.Tree.Create("text-input-test");
        shell.Tree.SetComponent(page, new XsrUiPager(XsrUiOrientation.Horizontal));
        shell.Tree.SetComponent(page, new XsrUiInput { Focusable = true });
        XsrUiEntityId form = shell.Tree.Create("text-input-form");
        shell.Tree.SetComponent(form, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        shell.Tree.SetComponent(form, new XsrUiScroll());
        shell.Tree.Attach(form, page);
        XsrUiEntityId Add(string name, bool password)
        {
            XsrUiEntityId entity = shell.Tree.Create(name);
            shell.Tree.SetComponent(entity, new XsrUiElement { Height = 40 });
            shell.Tree.SetComponent(entity, new XsrUiTextInput { IsPassword = password, Placeholder = name });
            shell.Tree.SetComponent(entity, new XsrUiInput { Focusable = true });
            shell.Tree.SetComponent(entity, new XsrUiSemantic(XsrUiSemanticRole.TextInput, name));
            shell.Tree.Attach(entity, form);
            return entity;
        }
        XsrUiEntityId name = Add("名称", false), password = Add("密码", true);
        shell.Stage.Navigation.Replace(page);
        surface.CommitScene(); window.UpdateLayout();
        var rect = Node(surface.Scene!, name).Rect;
        Point start = surface.TranslatePoint(new Point(rect.X + 4, rect.Y + 20), window)!.Value;
        window.MouseDown(start, MouseButton.Left);
        window.MouseUp(start, MouseButton.Left);
        AssertEqual(name, shell.Renderer.Focused);
        window.KeyTextInput("你好");
        AssertEqual("你好", shell.Tree.GetComponent<XsrUiTextInput>(name)!.ReadDraft());
        AvaloniaUiSceneNodeControl Control(XsrUiEntityId entity) => surface.GetVisualDescendants()
            .OfType<AvaloniaUiSceneNodeControl>().Single(control => control.Node.Entity == entity);
        IValueProvider namePeer = (IValueProvider)ControlAutomationPeer.CreatePeerForElement(Control(name))!;
        AssertEqual("你好", namePeer.Value);
        namePeer.SetValue("Player");
        AssertEqual("Player", shell.Tree.GetComponent<XsrUiTextInput>(name)!.ReadDraft());
        // A second press on an already focused input must retain capture for selection.
        Point end = start.WithX(start.X + 160);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        window.MouseUp(end, MouseButton.Left);
        AssertEqual(name, shell.Renderer.Focused);
        AssertEqual("Player", shell.Renderer.CopySelectedText()!);
        AssertFalse(shell.Tree.GetComponent<XsrUiPager>(page)!.IsDragging);
        AssertEqual(0d, shell.Tree.GetComponent<XsrUiPager>(page)!.Position);
        window.KeyTextInput("Alex");
        AssertEqual("Alex", shell.Tree.GetComponent<XsrUiTextInput>(name)!.ReadDraft());
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        AssertEqual(password, shell.Renderer.Focused);
        window.KeyTextInput("secret");
        AssertEqual("secret", shell.Tree.GetComponent<XsrUiTextInput>(password)!.ReadDraft());
        IValueProvider secretPeer = (IValueProvider)ControlAutomationPeer.CreatePeerForElement(Control(password))!;
        AssertEqual(string.Empty, secretPeer.Value);
        TextInputMethodClientRequestedEventArgs request = new() { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
        Control(password).RaiseEvent(request);
        AssertTrue(request.Client is not null);
        AssertFalse(request.Client!.SupportsSurroundingText);
        AssertEqual(string.Empty, request.Client.SurroundingText);
        request.Client.SetPreeditText("密码");
        AssertEqual("••", Control(password).Node.TextInput!.Value.Preedit);
        window.KeyTextInput("中");
        AssertEqual("secret中", shell.Tree.GetComponent<XsrUiTextInput>(password)!.ReadDraft());
        AssertFalse(Control(password).Node.TextInput.ToString()!.Contains("secret", StringComparison.Ordinal));
        window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
        AssertEqual("secret", shell.Tree.GetComponent<XsrUiTextInput>(password)!.ReadDraft());
        window.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        AssertEqual(name, shell.Renderer.Focused);
        if (previous.IsAssigned)
        {
            shell.Stage.Navigation.Replace(previous);
            surface.CommitScene();
            shell.Tree.Destroy(page);
        }
        Console.WriteLine("PASS: native text, IME, keyboard and password-safe automation");
    }
}
