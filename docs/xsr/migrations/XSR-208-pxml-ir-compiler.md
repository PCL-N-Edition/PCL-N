# XSR-208 PXML to UI.Next IR compilation

## Outcome

Wave 3 adds the compiler that turns the structural PXML DOM into the typed UI.Next IR: closed element kinds, typed presentation values, and the compiled binding table that the runtime loader consumes. XSR-212 supersedes the original hand-written element/property table with an externally configured, generated control catalog.

## Locked contract

- Elements map onto a closed, generated `PxmlIrNodeKind` set through the catalog selected by `PxmlControlCatalogDirectory`. Unknown elements and unknown properties are compile errors naming the failing construct. There is no reflection or runtime catalog lookup.
- Presentation properties compile to typed IR fields: invariant-culture doubles for sizes and spacing, strict `true`/`false` booleans, one-or-four-number thicknesses, and case-sensitive enum names. Malformed values are compile errors.
- The compiled binding table is a list of `(StatePath, Property, DirtyKinds)` records per node: `Content="{state path}"` on Text binds the text slot with paint dirt, and `IsVisible="{state path}"` binds the visibility slot with state dirt so layout and paint are recomputed. Commands compile on Button only, as plain semantic-ID references — a command is never a state binding.
- Button defaults: focusable and clickable true unless overridden. Image requires `Source`. Page and StackPanel carry no command or content.
- Semantic IDs are parsed and validated at compile time: the IR carries `XsrSemanticId` values for bindings and commands, so load time only resolves IDs through the registry (XSR-209) and never re-parses strings. The artifact is the host-internal `PxmlHostIr` — deliberately not the Plugin UI IR v1 stable ABI, which arrives with the Plugin SDK carrying format/schema versions, unknown-field skipping, resource references, serialization, compatibility, and security validation.
- All failures throw `PxmlCompileException` with the failing element and property in the message.

## Non-goals

Style resources, inline expressions, converters, loops/templates, and nested-page composition are not in the grammar. The generated UI.Next control catalog is a Host compilation surface; it does not freeze or imply the future Plugin UI IR compatibility schema.

## Verification

`PCL.Pxml.Tests` covers simple-page compilation with orientation and spacing, text state bindings, visibility bindings with scroll flags, button defaults and command capture, thickness and size parsing, unknown-element and unknown-property rejection, invalid-property placement rejection, and malformed number/enum/command/value rejection. The compiler project is AOT-compatible and covered by the architecture gate.
