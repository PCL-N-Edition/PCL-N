# XSR-212 generated PXML control catalog

## Outcome

The PXML compiler no longer owns a hand-written list of UI controls. The build explicitly supplies `PxmlControlCatalogDirectory`; that directory contains every PXML-visible control model, and `PCL.Pxml.Generators` expands the complete catalog into a compiler-generated C# file during the earliest C# compilation stage.

This unit supersedes XSR-208's hand-written element switch and static property sets. The generated result remains closed and AOT-safe for each build, while control ownership moves out of the compiler and back to UI.Next.

## Ownership and build flow

```text
PCL.UI.Next/PxmlControls/*.pxml-control
                  |
                  | PxmlControlCatalogDirectory (required MSBuild input)
                  v
         PCL.Pxml.Generators
                  |
                  | PxmlControlCatalog.g.cs (obj, compile-time only)
                  v
          PCL.Pxml.Compiler
                  |
                  v
             typed PXML IR
```

- UI.Next owns the catalog directory because it owns the renderer component recipes and semantic roles.
- `Directory.Build.props` selects the repository catalog location. `PCL.Pxml.Compiler` contains no fallback path; another build must explicitly provide its own directory.
- MSBuild enumerates `*.pxml-control` files as `AdditionalFiles`. Missing configuration, a missing directory, or an empty catalog fails before `CoreCompile`.
- The selected directory and descriptor file set participate in the compiler dependency fingerprint, so changing catalogs invalidates an incremental build even when the replacement files have older timestamps.
- The incremental source generator validates every file, sorts controls by their explicit numeric ID, and emits the node-kind enum plus the immutable expanded lookup table. Generated files are materialized under `obj` for inspection and never committed.
- Catalog files are the only source of control names, valid properties, defaults, required properties, semantic roles, and state-binding targets. Compiler code contains only generic value parsers and typed IR-slot assignment.
- Runtime loading remains a closed AOT mapping over generated node kinds and UI.Next components. No catalog file is read at runtime.

## Control descriptor format

Each `.pxml-control` file is a deterministic line-oriented document:

```text
schema=1
id=4
name=Button
role=Button
recipe=CommandInput
children=false
property=Focusable|Boolean|Focusable|None|None|false|true
```

`recipe` maps a control onto one of the loader's finite renderer-component recipes (`Element`, `StackLayout`, `Text`, `CommandInput`, or `Image`); the loader never branches on a control name. `children` declares whether the control accepts child elements. A property row is `Name|ValueKind|IrTarget|BindingProperty|DirtyKinds|Required|Default`. Empty defaults are allowed. Names and IDs are unique; IDs are positive and contiguous. Invalid kinds, targets, roles, recipes, bindings, binding-to-target/dirty-kind combinations, defaults, duplicate properties, and duplicate IR targets are generator errors.

## Review fixes folded into the model

The catalog explicitly declares binding support. A `{state ...}` value on a literal-only property is now a compile error instead of silently becoming the property's default. Required properties cannot be satisfied by an unsupported binding.

## Verification

Build and tests prove that the five initial UI.Next controls come from generated metadata, generated IDs are deterministic, all property/default/required rules are applied through the catalog, literal-only binding attempts fail, and an unknown element still fails during compilation. A substitute catalog containing only a new `Card` control builds without compiler source changes, while an invalid catalog fails with a generator diagnostic. Architecture tests enforce the required directory input, AdditionalFiles wiring, generator direction, generated-file emission, incremental input fingerprint, and absence of hand-written control-name switches in the compiler.
