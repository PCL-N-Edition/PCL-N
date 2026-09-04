# XSR-712 Defer the LiquidGlass presentation

## Scope locked before implementation

The user has deferred the LiquidGlass redesign. Experimental is the only current product shell
style. Remove the alternative palette, selectable enum member, style-toggle intent handler and
Desktop command-line selection. Old style arguments no longer enable an alternate presentation.
An invalid numeric style must be rejected without changing the existing shell palette or route.

Retain the experimental layout, account functionality, capsule controls, independent staggered
entry, title navigation and reduced-motion behavior from XSR-710. This is not a rollback of
Apple-inspired interaction polish. Backend-neutral surface/material primitives remain available
to the renderer; a future visual redesign will have a separate contract and acceptance pass.

Historical dual-style validation in older migration notes describes those commits, not the
current available styles. Update the active documentation and product overview accordingly.

## Acceptance

- Only Experimental is advertised/composable; the removed numeric style cannot mutate a shell.
- An old style-toggle intent cannot activate an alternate palette.
- Default Desktop and old-style-argument smoke runs both render the Experimental PXML shell.
- Existing page, capsule, accessibility, close and stagger regressions remain green.
- Run managed UI/PXML/Desktop/native-backend tests, architecture/format/benchmark gates,
  and Desktop NativeAOT/trim smoke. Console detection in the Desktop bootstrap must guard the
  Windows API so the Linux CI smoke can enter composition.

## Verified result (2026-09-04)

Release build completed with zero warnings/errors. UI.Next 69, PXML 37, Desktop 38 and all six
native-backend scenarios passed, including the independent motion regressions. The 29-project
architecture gate, renderer benchmark gates and source formatting verification passed.

Windows x64 Desktop NativeAOT and trimmed/link publishing passed. For each artifact, both
`--validate-shell` and `--validate-shell --liquid-glass --ui-style=liquid-glass` reported
`UI style: Experimental` and rendered 56 semantic nodes. No alternate-style references remain
in production source. Prior dual-style evidence remains in historical migration records.
