# XSR-211 PXML review hardening

## Outcome

A focused review of the first parser/compiler/loader implementation found input-boundary and failure-atomicity gaps before Wave 3 acceptance. This unit fixes the independent parser and loader findings before the control-catalog architecture is replaced.

## Findings and locked fixes

- XML parsing previously used `XmlDocument.LoadXml` directly, included platform-localized `XmlException` text in the public error, and did not make the DTD policy explicit. PXML now parses through an `XmlReader` with DTD processing prohibited and resolvers disabled, and exposes one deterministic well-formedness error.
- A prefixed attribute could be accepted by its local name. PXML properties must now be unqualified; namespace declarations remain the only namespaced attributes consumed by the parser.
- Document-level comments after the root were mistaken for a second root. XML already enforces one document element, so comments before and after that element are accepted.
- A runtime load failure previously left created entities attached to the target tree. The loader now builds a detached subtree and attaches it only after success; every failure destroys the complete temporary subtree.

The compiler review also found that state bindings on literal-only properties could be silently reduced to defaults. That issue is resolved by XSR-212's generated control catalog, where every property declares whether and where it binds.

## Verification

`PCL.Pxml.Tests` adds DTD rejection, qualified-property rejection, document-level comment acceptance, and tree-count/parent-child invariants after an unknown-state load failure. Existing parser, compiler, scene parity, and live-binding tests remain green.
