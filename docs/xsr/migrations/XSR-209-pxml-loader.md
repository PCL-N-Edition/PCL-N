# XSR-209 PXML runtime loader

## Outcome

Wave 3 closes the pipeline: the runtime loader turns a compiled IR into live entities, bindings, and commands in a `XsrUiTree`, and parity tests prove a PXML-loaded page renders identically to the same page built by hand.

## Locked contract

- `PxmlUiLoader.Load(ir, tree, state, parent)` maps IR nodes onto entities through finite runtime recipes carried in the IR: typed presentation values become components, stack recipes become stack components (with scroll when compiled), text recipes attach text state, command-input recipes create input plus command bindings, image recipes become the media slot, and role/label become semantics. The loader never branches on a generated control name.
- State paths resolve against the concrete store at load time; an unregistered path is a load failure (`PxmlLoadException`) naming the path. Commands become semantic-ID command bindings and resolve through the intent sink at activation time — never through the loader.
- Bindings become the binding records of XSR-205, so loaded pages participate in the same thread-safe bridge drain and dirty propagation as hand-built pages.
- The loader is a static mapping over closed IR data: no reflection, no runtime XML parsing, no property-path evaluation. `PCL.Pxml.Runtime` references the compiler for the IR types — the dependency direction stays acyclic.

## Non-goals

Incremental reload (diffing a recompiled IR into a live tree), per-page instance registries, and plugin UI IR interop are later units; this loader is the single-instance runtime target.

## Verification

`PCL.Pxml.Tests` covers scene parity between a PXML-loaded page and a hand-built page (depth, rects, text, labels, roles across the whole node list), loaded text and visibility state bindings driving rendering through the bridge after publication, and unknown-state-path rejection. The runtime project is AOT-compatible; the architecture gate carries the new Runtime→Compiler edge.
