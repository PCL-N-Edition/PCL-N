# XSR-210 PXML gates and Wave 3 acceptance

## Outcome

Wave 3 closes with its gates wired into CI: the generated control catalog and the full PXML parser/compiler/loader pipeline run under CoreCLR and NativeAOT alongside the runtime and renderer gates.

## Locked contract

- CI runs, in order: full solution build, generated-catalog acceptance, XSR runtime tests and NativeAOT publish, UI.Next tests and NativeAOT publish, PXML tests and NativeAOT publish, UI.Next benchmark gates, architecture and source-format gates, and the trimmed Desktop publish.
- Catalog acceptance proves the default directory materializes `PxmlControlCatalog.g.cs`, a substitute directory containing only a new `Card` control compiles without compiler changes, an invalid descriptor fails with `PXMLGEN002`, a missing directory fails before compilation, and switching back restores the default generated catalog through the normal incremental build path.
- `PCL.Pxml.Compiler` and `PCL.Pxml.Runtime` are AOT-compatible. The no-runtime-reflection-binding gate scans Compiler and Runtime source for reflection APIs, rejects handwritten control-name branches in the compiler/loader, and enforces the generated project direction. Unknown elements, properties, unsupported bindings, and malformed values remain compile-time errors.
- `PCL.Pxml.Generators` is an active build-only Roslyn component targeting `netstandard2.0`. It consumes only the required UI.Next-owned descriptor directory and emits a closed typed catalog; neither generator nor runtime reads product assemblies or catalog files dynamically. The analyzer project reference strips publish/AOT/RID properties so NativeAOT applies only to the product graph, never to the compiler-host tool.

## Non-goals

Continuous profiling, PXML compiler throughput benchmarks, incremental page reload, and Plugin UI IR interop are later units.

## Verification

The full gate set (Release build, all three test executables, all three NativeAOT publishes and executions, generated-catalog positive/negative cases, renderer benchmark, architecture/reflection scan, format verification, and trimmed Desktop publish) executed locally before the Wave 3 acceptance commit. CI runs the same acceptance sequence on every push.
