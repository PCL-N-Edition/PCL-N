# XSR-207 PXML grammar and parser

## Outcome

Wave 3 starts the PXML pipeline: the structural grammar and parser that turn PXML authoring text into a DOM. The parser enforces grammar only; element and attribute semantics belong to the compiler (XSR-208).

## Locked contract

- PXML is XML with one namespace: `https://pcln.dev/pxml/2026`. Documents declaring any other namespace are rejected; documents without a namespace declaration are accepted.
- A document has exactly one root element. Elements carry properties (attributes) only — text content is rejected, so the surface is unambiguous. Property names are unique per element; duplicates are rejected.
- Property values are literals or state bindings. A state binding is exactly `{state path}` where the path is non-empty with no whitespace or control characters; braces in any other form are rejected. There is no command binding syntax — commands are plain semantic-ID references on command-bearing elements and resolve at compile time.
- The DOM is structural: `PxmlDocument` → `PxmlElement` → `PxmlProperty(name, value)` with `PxmlValue` as literal or binding path. Children keep document order. Comments and insignificant whitespace are ignored.
- All failures throw `PxmlParseException` with a deterministic message naming the failing construct. No XML exception details escape beyond the well-formedness note.
- The parser uses `System.Xml.XmlDocument` only — no reflection, AOT-safe.

## Non-goals

Element/attribute semantics, unknown-element rejection, binding resolution against the state store, and the UI.Next IR are XSR-208; the runtime loader is XSR-209. XSR-212 later moved the control schema surface into the UI.Next-owned catalog and implemented its compile-time generator; the structural parser remains independent of that catalog.

## Verification

`PCL.Pxml.Tests` (new executable test project) covers structural parsing with literals, state-binding recognition, document-order children, comment and whitespace handling, duplicate-property rejection, malformed-binding rejection (empty path, multi-word path, unclosed, unknown directive), text-content rejection, missing and multiple roots, and foreign namespaces. The compiler project is AOT-compatible and covered by the architecture gate.
