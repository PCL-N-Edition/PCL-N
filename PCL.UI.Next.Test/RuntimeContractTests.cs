// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.ComponentModel;
using System.Reflection;

namespace PCL.UI.Next.Test;

[TestClass]
public sealed class RuntimeContractTests
{
    [TestMethod]
    public void ContractVersion_RequiresSameMajorAndAvailableMinor()
    {
        UiContractVersion runtime = new(1, 2);

        Assert.IsTrue(runtime.Supports(new UiContractVersion(1, 0)));
        Assert.IsTrue(runtime.Supports(new UiContractVersion(1, 2)));
        Assert.IsFalse(runtime.Supports(new UiContractVersion(1, 3)));
        Assert.IsFalse(runtime.Supports(new UiContractVersion(2, 0)));
        Assert.IsFalse(default(UiContractVersion).Supports(new UiContractVersion(1, 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new UiContractVersion(0, 1));
    }

    [TestMethod]
    public void RuntimeAssembly_DoesNotReferencePlatformOrBusinessAssemblies()
    {
        string[] forbiddenPrefixes =
        [
            "Avalonia",
            "PCL.Application",
            "PCL.Core",
            "PCL.Desktop",
            "PCL.Platform"
        ];
        string[] references = typeof(UiRuntimeContract).Assembly
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name ?? string.Empty)
            .ToArray();

        for (int i = 0; i < references.Length; i++)
        {
            Assert.IsFalse(
                forbiddenPrefixes.Any(prefix => references[i].StartsWith(prefix, StringComparison.Ordinal)),
                "Forbidden Runtime reference: " + references[i]);
        }
    }

    [TestMethod]
    public void RuntimeTypes_DoNotImplementPropertyChanged()
    {
        Type[] runtimeTypes = typeof(UiRuntimeContract).Assembly.GetTypes();

        Assert.IsFalse(runtimeTypes.Any(static type =>
            typeof(INotifyPropertyChanged).IsAssignableFrom(type)));
    }

    [TestMethod]
    public void AuthoringFacade_DoesNotExposeRuntimeImplementationTypes()
    {
        Type[] facadeTypes =
        [
            typeof(Ui),
            typeof(UiNode),
            typeof(UiBlueprint),
            typeof(UiClass),
            typeof(UiCommand),
            typeof(UiGridDefinition),
            typeof(UiSelectors),
            typeof(UiSelector<>)
        ];
        HashSet<Type> forbidden =
        [
            typeof(UiEntity),
            typeof(UiWorld),
            typeof(ComponentStore),
            typeof(ComponentPool<>),
            typeof(DirtyTracker),
            typeof(RenderMutation),
            typeof(RenderNodeId)
        ];

        for (int i = 0; i < facadeTypes.Length; i++)
        {
            foreach (MemberInfo member in facadeTypes[i].GetMembers(
                         BindingFlags.Public |
                         BindingFlags.Instance |
                         BindingFlags.Static |
                         BindingFlags.DeclaredOnly))
            {
                foreach (Type signatureType in GetSignatureTypes(member))
                {
                    foreach (Type expanded in Expand(signatureType))
                    {
                        Type normalized = Normalize(expanded);
                        Assert.IsFalse(
                            forbidden.Contains(normalized),
                            $"{facadeTypes[i].Name}.{member.Name} exposes {normalized.Name}.");
                    }
                }
            }
        }
    }

    [TestMethod]
    public void Blueprint_PublicSurfaceIsOpaque()
    {
        Assert.IsNull(typeof(UiBlueprint).GetProperty("RootIndex"));
        Assert.IsNull(typeof(UiBlueprint).GetProperty("NodeCount"));
        Assert.IsNull(typeof(UiBlueprint).GetProperty("BindingCount"));
        Assert.IsNull(typeof(UiBlueprint).GetProperty("DependencyIndex"));
        Assert.IsNull(typeof(UiBlueprint).GetMethod("GetNode"));
        Assert.IsNull(typeof(UiBlueprint).GetMethod("GetBinding"));
    }

    [TestMethod]
    public void RenderingRuntime_RejectsIncompatibleBackendBeforeInitialization()
    {
        UiWorld world = new(new DeterministicUiClock());
        using UiInteractiveRuntime runtime = new(
            world,
            new DeterministicTextEngine(),
            new UiSize(100f, 100f));
        UiScopeId scope = world.CreateRootScope();
        IncompatibleBackend backend = new();

        Assert.ThrowsExactly<NotSupportedException>(() =>
            new UiRenderingRuntime(
                world,
                backend,
                runtime.TextCache,
                scope,
                new UiSize(100f, 100f)));
        Assert.AreEqual(0, backend.InitializeCount);
    }

    [TestMethod]
    public void RenderingRuntime_DisposeShutsBackendDownExactlyOnce()
    {
        UiWorld world = new(new DeterministicUiClock());
        using UiInteractiveRuntime runtime = new(
            world,
            new DeterministicTextEngine(),
            new UiSize(100f, 100f));
        UiScopeId scope = world.CreateRootScope();
        HeadlessUiBackend backend = new();
        UiRenderingRuntime rendering = new(
            world,
            backend,
            runtime.TextCache,
            scope,
            new UiSize(100f, 100f));

        rendering.Dispose();
        rendering.Dispose();

        Assert.IsTrue(backend.IsShutdown);
        Assert.AreEqual(1, backend.ShutdownCount);
    }

    private sealed class IncompatibleBackend : IUiBackend
    {
        public UiContractVersion RequiredContractVersion { get; } = new(2, 0);
        public UiBackendCapabilities Capabilities => UiBackendCapabilities.None;
        public int InitializeCount { get; private set; }
        public void Initialize(in UiBackendContext context)
        {
            _ = context;
            InitializeCount++;
        }
        public void Commit(in UiCommitBatch batch) => _ = batch;
        public void RequestFrame() { }
        public void Shutdown() { }
    }

    private static IEnumerable<Type> GetSignatureTypes(MemberInfo member)
    {
        switch (member)
        {
            case MethodInfo method:
                yield return method.ReturnType;
                foreach (ParameterInfo parameter in method.GetParameters())
                    yield return parameter.ParameterType;
                break;
            case ConstructorInfo constructor:
                foreach (ParameterInfo parameter in constructor.GetParameters())
                    yield return parameter.ParameterType;
                break;
            case PropertyInfo property:
                yield return property.PropertyType;
                break;
            case FieldInfo field:
                yield return field.FieldType;
                break;
            case EventInfo eventInfo:
                yield return eventInfo.EventHandlerType!;
                break;
        }
    }

    private static Type Normalize(Type type)
    {
        while (type.HasElementType)
            type = type.GetElementType()!;
        return type.IsGenericType ? type.GetGenericTypeDefinition() : type;
    }

    private static IEnumerable<Type> Expand(Type type)
    {
        while (type.HasElementType)
            type = type.GetElementType()!;
        yield return type;
        if (!type.IsGenericType)
            yield break;
        Type[] arguments = type.GetGenericArguments();
        for (int i = 0; i < arguments.Length; i++)
        {
            foreach (Type nested in Expand(arguments[i]))
                yield return nested;
        }
    }
}
