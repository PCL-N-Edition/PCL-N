# XSR renderer model

`PCL.UI.Next` is the canonical semantic renderer of XSR. Avalonia is a backend, not the application architecture.

## Pipeline

```text
PXML
  -> semantic analysis
  -> UI.Next IR
  -> entity/component template
  -> state dependency table + command binding table
  -> UI.Next runtime
  -> render scene
  -> backend commit
```

Runtime rendering does not parse property paths, discover converters, invoke reflection binding, resolve a concrete service, or call a Sidecar.

## Narrow XSR boundary

The renderer consumes narrow contracts equivalent to:

```csharp
public interface IUiStateSource
{
    StateSnapshot Snapshot { get; }
}

public interface IUiCommandSink
{
    void Emit(CommandId command, in XsrValue argument);
}
```

The X kernel proved the contract in Wave 1, so the renderer consumes `XsrStateStore` reads as its state source and emits commands through `IXsrUiIntentSink` (owned by UI.Next; the composition root bridges it into the command router). The architectural rule is stable: UI reads local state and emits intent.

## Responsibility split

UI.Next owns semantic entities, layout, animation, input routing, navigation, overlay, accessibility semantics, text/media abstractions, dirty tracking, and render-scene production.

The state boundary is thread-safe by construction: state publishers run on arbitrary threads and only enqueue changed state IDs into the UI state bridge; the render thread drains the queue at frame start, resolves derived entries through the store's dependency index, and marks bound entities dirty. Entity handles are generational, so destroyed entities cannot be impersonated by recycled indexes.

Backends own native windows and surfaces, final drawing submission, device resources, native input/IME bridges, clipboard integration, and platform accessibility bridges. Backend types do not appear in UI.Next public contracts.

Services own business facts and effects. Renderer-local state is limited to ephemeral presentation mechanics such as hover, focus, an in-progress gesture, or animation progress. It cannot become a duplicate account, download, launch, or selection model.

## Plugin UI

Plugin PXML compiles to a stable Plugin UI IR. The Host registers and locally instantiates that IR. Opening a registered plugin page performs zero Sidecar IPC; bindings read the Host state mirror and commands resolve to runtime IDs.

UI.Next internals are not Plugin SDK. ECS entities, components, scene nodes, and backend controls never cross the plugin compatibility boundary.

## Performance and accessibility gates

Frame work is bounded, deterministic where practical, and free from blocking I/O. Dirty propagation touches only entities that depend on a changed state. Reduced motion, keyboard navigation, focus visibility, semantic roles, contrast, IME, and native accessibility remain contract requirements across backends.
