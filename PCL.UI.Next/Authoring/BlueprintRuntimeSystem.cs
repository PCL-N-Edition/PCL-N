// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Pipeline system that drives all live blueprint instances each reactive frame.
/// Structural reconcile + binding dispatch stay ordered inside
/// <see cref="BlueprintInstantiator.Update"/>.
/// </summary>
public sealed class BlueprintRuntimeSystem : IUiSystem
{
    private readonly BlueprintInstantiator _instantiator;

    public BlueprintRuntimeSystem(BlueprintInstantiator instantiator)
    {
        _instantiator = instantiator ?? throw new ArgumentNullException(nameof(instantiator));
    }

    // After drain phases; before layout. Structural+binding run as one unit so If remounts bind same frame.
    public UiSystemPhase Phase => UiSystemPhase.BindingUpdate;

    public string Name => "authoring.blueprint-runtime";

    public void Update(UiWorld world, in UiFrameContext frame)
    {
        _ = world;
        _ = frame;
        _instantiator.UpdateAll();
    }
}
