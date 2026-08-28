# XSR-204 renderer gates and CI

## Outcome

Wave 2 closes with its exit gates wired into CI: the UI.Next test suite runs in the pipeline including a NativeAOT publish, and the benchmark executable enforces deterministic renderer invariants on every push.

## Locked contract

- `PCL.UI.Next.Benchmarks` is an executable gate, not a reporting tool. It enforces machine-independent invariants: a clean re-render of the full tree allocates zero managed bytes and reuses the cached scene; a paint-only change triggers zero layout visits; a structural leaf change relayouts strictly fewer entities than the whole tree; and the produced scene carries exactly the entity tree in deterministic depth-first order. Timing throughput is reported informationally and never gated, so the gate is deterministic across machines.
- CI runs, in order: full solution build, XSR runtime kernel tests, NativeAOT runtime tests, UI.Next renderer kernel tests, NativeAOT renderer tests, UI.Next benchmark gates, the architecture boundary scan, and the trimmed Desktop publish.
- `PCL.UI.Next` is AOT-compatible; the renderer kernel is exercised by the same NativeAOT and trim gates as the X kernel.

## Non-goals

This unit does not add benchmark frameworks, continuous profiling, or performance regression tracking infrastructure. The gate is deliberately dependency-free so it cannot rot with tooling.

## Verification

The benchmark executable runs locally and in CI with exit code semantics; the full gate set (build, runtime tests, NativeAOT runtime tests, renderer tests, NativeAOT renderer tests, benchmark gates, architecture scan, trim publish) was executed locally before the Wave 2 acceptance commit.
