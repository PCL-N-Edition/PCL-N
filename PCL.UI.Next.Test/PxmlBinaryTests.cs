// Copyright (c) 2026 PCL N contributors.

using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.UI.Next.Test;

[TestClass]
public sealed class PxmlBinaryTests
{
    // Produced by the C PXML Compiler from samples/RuntimeInterop.pxml with
    // --predefined-dir components/predefined --strict --release.
    private const string RuntimeInteropPxb =
        "UFhCMQEAAAABAAAADqf05YcQOa+5OpTexKGFIgYAAAAkAAAAU1RSUwAAAADwAAAAAAAAAEoBAAAAAAAAEAAAAAAAAABOT0RFAAAAAEACAAAAAAAAtAAAAAAAAAAQAAAAAAAAAFBST1AAAAAAAAMAAAAAAAA8AQAAAAAAABAAAAAAAAAAQklORAAAAABABAAAAAAAACAAAAAAAAAAEAAAAAAAAABERVBTAAAAAGAEAAAAAAAABAAAAAAAAAAQAAAAAAAAAE1FVEEAAAAAcAQAAAAAAAAoAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAFwAAAAAAAAABAAAABwAAAA4AAAAYAAAAQAAAAE8AAABhAAAAbgAAAHMAAAB7AAAAgQAAAIcAAACTAAAAmgAAAKEAAACkAAAArgAAAL0AAADMAAAA0QAAANUAAADdAAAAAENsYXNzAEJ1dHRvbgBCZWhhdmlvcnMASG92ZXJhYmxlIFByZXNzYWJsZSBDbGlja2FibGUgRm9jdXNhYmxlAEFjY2Vzc2libGVSb2xlAEFjY2Vzc2libGVBY3Rpb25zAEludm9rZSBGb2N1cwBLaW5kAFRleHRCb3gAVmFsdWUAcXVlcnkAUGxhY2Vob2xkZXIAU2VhcmNoAEhlaWdodAAzNgBGb2N1c2FibGUAQWNjZXNzaWJsZU5hbWUARm9jdXMgU2V0VmFsdWUAVGV4dABSdW4AQ29tbWFuZABMYXVuY2hlci5SdW4AAAAAAAAABAAAAP////8BAAAAAgAAAAMAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAIAAAABAAAAAAAAAAMAAAABAAAAAwAAAAAAAAAEAAAAAAAAAAEAAAAAAAAABQAAAAMAAAAAAAAA/////wAAAAAMAAAABAAAAAgAAAABAAAAAAAAAAAAAAAFAAAAAwAAAAEAAAD/////AAAAAAQAAAAMAAAAAQAAAAEAAAAAAAAAAAAAAAsAAAAFAAAAAAAAAAAAAAAAAAAADQAAAAEAAAAcptODAQAAAAEAAAAHAAAAAAAAAAEAAACUz/JMAQAAAA4AAAAYAAAAAAAAAAEAAAD02b+6AQAAAEAAAAAHAAAAAAAAAAEAAACSFsdHAQAAAE8AAABhAAAAAAAAAAIAAACVdIV4AQAAAG4AAABzAAAAAAAAAAIAAADatxMwAQAAAHsAAACBAAAAAAAAAAIAAAB0K/kVAQAAAIcAAACTAAAAAAAAAAIAAAAxKtbbBgAAAJoAAAChAAAAAAAAAAIAAACUz/JMAQAAAA4AAACkAAAAAAAAAAIAAAD02b+6AQAAAEAAAABzAAAAAAAAAAIAAADyzp7VAQAAAK4AAACTAAAAAAAAAAIAAACSFsdHAQAAAE8AAAC9AAAAAAAAAAMAAAC9BwBXAQAAAMwAAADRAAAAAAAAAAAAAAABAAAAAQAAAECNqRcCAAAA1QAAAN0AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAAAAAAAAABAAAAAAAAAAEAAAABAAAABAAAAA0AAAABAAAAAAAAAAAAAAA=";

    private const string RuntimeBindingInteropPxb =
        "UFhCMQEAAAABAAAAs+4M/MY00sTCUQRqzMQ9RgYAAAAkAAAAU1RSUwAAAADwAAAAAAAAABwAAAAAAAAAEAAAAAAAAABOT0RFAAAAABABAAAAAAAAXAAAAAAAAAAQAAAAAAAAAFBST1AAAAAAcAEAAAAAAAAEAAAAAAAAABAAAAAAAAAAQklORAAAAACAAQAAAAAAACAAAAAAAAAAEAAAAAAAAABERVBTAAAAAKABAAAAAAAADAAAAAAAAAAQAAAAAAAAAE1FVEEAAAAAsAEAAAAAAAAoAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAwAAAAAAAAABAAAABgAAAABUZXh0AFRpdGxlAAAAAAACAAAA/////wEAAAABAAAAAwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgAAAAEAAAAAAAAA/////wAAAAAEAAAAAAAAAAAAAAAAAAAAAQAAAAAAAAADAAAAAwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAAAAEAAAC9BwBXAQAAAAEAAAAGAAAAAAAAAAEAAAABAAAAdRxw6n11IHkAAAAAAAAAAAEAAAAAAAAAAQAAAAAAAAABAAAAAQAAAAIAAAAAAAAAAQAAAAAAAAAAAAAA";

    [TestMethod]
    public void CompilerPxb_ExpandsPredefinedControlsToRuntimePrimitives()
    {
        UiBlueprint blueprint = UiBlueprint.FromPxmlBinary(
            Convert.FromBase64String(RuntimeInteropPxb),
            "RuntimeInterop");

        Assert.AreEqual(4, blueprint.NodeCount);
        Assert.IsFalse(blueprint.NodesCore.Any(node => node.Kind == UiNodeKind.Reserved));
        Assert.AreEqual(UiNodeKind.Container, blueprint.GetNode(0).Kind);
        Assert.AreEqual(UiNodeKind.Container, blueprint.GetNode(1).Kind);
        Assert.AreEqual(UiNodeKind.NativeHost, blueprint.GetNode(2).Kind);
        Assert.AreEqual(UiNodeKind.Text, blueprint.GetNode(3).Kind);

        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        var instantiator = new BlueprintInstantiator(world, new PresentationStore());
        BlueprintInstance instance = instantiator.Instantiate(blueprint, scope);
        UiEntity button = instance.EntityAt(1);
        UiEntity textBox = instance.EntityAt(2);

        Assert.IsTrue(world.Components.Get<StyleClassSet>(button).Contains(UiClass.Button.Id));
        Assert.IsTrue((world.Components.Get<BehaviorComponent>(button).Flags & UiBehavior.Clickable) != 0);
        Assert.AreNotEqual(0, world.Components.Get<CommandBindingComponent>(button).CommandId);
        Assert.AreEqual(UiSemanticRole.Button, world.Components.Get<SemanticRole>(button).Value);
        Assert.AreEqual(UiNativeHostKind.TextBox, world.Components.Get<NativeHostComponent>(textBox).Kind);
        Assert.AreEqual("Search", world.Components.Get<NativeHostComponent>(textBox).Placeholder);
    }

    [TestMethod]
    public void CorruptPxb_IsRejectedBeforeBlueprintCreation()
    {
        byte[] binary = Convert.FromBase64String(RuntimeInteropPxb);
        binary[^1] ^= 0x5a;

        Assert.ThrowsExactly<PxmlBinaryException>(() => UiBlueprint.FromPxmlBinary(binary));
    }

    [TestMethod]
    public void CompilerPxb_BindingResolverDrivesIncrementalTextUpdate()
    {
        const int titleSlice = 71;
        var resolver = new TestBindingResolver(titleSlice);
        UiBlueprint blueprint = UiBlueprint.FromPxmlBinary(
            Convert.FromBase64String(RuntimeBindingInteropPxb),
            bindingResolver: resolver);
        var presentation = new PresentationStore();
        presentation.Set(titleSlice, "Before");
        UiWorld world = new(new DeterministicUiClock());
        var instantiator = new BlueprintInstantiator(world, presentation);
        BlueprintInstance instance = instantiator.Instantiate(blueprint, world.CreateRootScope());
        UiEntity text = instance.EntityAt(1);

        Assert.AreEqual("Before", world.Components.Get<TextContent>(text).Value);
        Assert.AreEqual("Title", resolver.LastBinding?.Expression);
        Assert.AreEqual(1, resolver.LastBinding?.Dependencies.Length);

        presentation.Set(titleSlice, "After");
        instantiator.Update(instance);
        Assert.AreEqual("After", world.Components.Get<TextContent>(text).Value);
    }

    [TestMethod]
    public void PublicAuthoringSurface_AcceptsPxbWithoutExposingCSharpNodeBuilders()
    {
        Assembly assembly = typeof(UiBlueprint).Assembly;
        Type? ui = assembly.GetType("PCL.UI.Next.Ui", throwOnError: false);
        Type? node = assembly.GetType("PCL.UI.Next.UiNode", throwOnError: false);

        Assert.IsNotNull(ui);
        Assert.IsNotNull(node);
        Assert.IsFalse(ui.IsPublic);
        Assert.IsFalse(node.IsPublic);
        Assert.IsNotNull(typeof(UiBlueprint).GetMethod(
            nameof(UiBlueprint.FromPxmlBinary),
            BindingFlags.Public | BindingFlags.Static));
    }

    private sealed class TestBindingResolver(int titleSlice) : IPxmlBindingResolver
    {
        public PxmlBindingDescriptor? LastBinding { get; private set; }

        public UiSelector<string> ResolveString(PxmlBindingDescriptor binding)
        {
            LastBinding = binding;
            return new UiSelector<string>(101, titleSlice, store => store.Get<string>(titleSlice));
        }

        public UiSelector<bool> ResolveBoolean(PxmlBindingDescriptor binding) =>
            throw new InvalidOperationException("The test PXB has no boolean bindings.");
    }
}
