# XSR-201 UI.Next entity kernel

## Outcome

Wave 2 starts the renderer kernel: the entity-component tree of `PCL.UI.Next` with deterministic hierarchy, an open closed-type component set, dirty tracking, and the state bridge that turns applied state changes into entity dirt.

## Narrow boundary decision

The renderer model left the state and intent contracts to freeze after the X kernel proved them. Wave 1 did: the renderer consumes `XsrStateStore` reads as its state source (the dependency graph already allows `PCL.UI.Next -> PCL.Xsr.State`) and emits commands through `IXsrUiIntentSink`, an interface owned by UI.Next that the composition root bridges into the command router. UI.Next references no runtime, services, backend, or Avalonia assembly.

## Locked contract

- Entities are handles (`XsrUiEntityId`) owned by one `XsrUiTree`. Handles recycle on destroy and never cross the plugin compatibility boundary. Trees are single-threaded (render-thread owned).
- The hierarchy is a forest: attach, detach, and destroy-subtree semantics with cycle rejection. Children keep attach order, so every traversal and produced scene is deterministic.
- Components are a closed set of concrete classes (text, element constraints, stack panel, semantic role, command binding, state binding, input flags) held per entity; no reflection, no open component registration. Setting or removing a component marks structure dirty. Mutating component contents directly is allowed for owners; they mark paint dirty explicitly.
- Dirty tracking has per-entity own flags (`Structure`, `Layout`, `Paint`, `State`) and a subtree flag; marking bubbles the subtree flag to ancestors, clearing recomputes it exactly. Dirty enumeration is ascending by entity ID regardless of the order flags were raised.
- A state binding component automatically maintains the dependency table; destroying an entity or replacing its binding updates the table. `XsrUiStateBridge` implements the store's state observer and marks exactly the bound entities dirty with the `State` kind on every applied change.
- Renderer intent: `IXsrUiIntentSink.Emit(command, source, correlationId)` carries semantic command IDs and renderer-produced correlation IDs only.

## Non-goals

This unit does not introduce layout, scene production, hit testing, navigation, overlay, animation, text shaping, or any backend contract. XSR-202 and XSR-203 layer those on this tree.

## Verification

`PCL.UI.Next.Tests` (new executable test project) covers handle recycling, deterministic child order, cycle rejection, subtree destruction, component set/get/remove, structure-dirty marking, dirty bubbling and precise clearing, deterministic dirty enumeration, state-bridge scoping, dependency table updates on destroy, and depth-first walks. The project graph, AOT compatibility of `PCL.UI.Next`, the architecture gate, and the CI wiring follow in XSR-204's gate update.
