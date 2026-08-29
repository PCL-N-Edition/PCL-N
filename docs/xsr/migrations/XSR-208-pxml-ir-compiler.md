# XSR-208 PXML to UI.Next IR compilation

## Outcome

Wave 3 adds the compiler that turns the structural PXML DOM into the typed UI.Next IR: closed element kinds, typed presentation values, and the compiled binding table that the runtime loader consumes.

## Locked contract

- Elements map onto a closed `PxmlIrNodeKind` set (Page, StackPanel, Text, Button, Image) through a static table. Unknown elements and unknown properties are compile errors naming the failing construct — there is no reflection anywhere in the pipeline, and no open extensibility point that could introduce it.
- Presentation properties compile to typed IR fields: invariant-culture doubles for sizes and spacing, strict `true`/`false` booleans, one-or-four-number thicknesses, and case-sensitive enum names. Malformed values are compile errors.
- The compiled binding table is a list of `(StatePath, Property, DirtyKinds)` records per node: `Content="{state path}"` on Text binds the text slot with paint dirt, and `IsVisible="{state path}"` binds the visibility slot with paint dirt. Commands compile on Button only, as plain semantic-ID references — a command is never a state binding.
- Button defaults: focusable and clickable true unless overridden. Image requires `Source`. Page and StackPanel carry no command or content.
- State paths remain unresolved strings in the IR; resolution against a concrete state store happens at load time (XSR-209), keeping the compiler free of store dependencies.
- All failures throw `PxmlCompileException` with the failing element and property in the message.

## Non-goals

Style resources, inline expressions, converters, loops/templates, and nested-page composition are not in the grammar. The source generator over PXML schemas is deferred until the Plugin UI IR unit defines the surface it generates from; the no-reflection gate does not depend on it.

## Verification

`PCL.Pxml.Tests` covers simple-page compilation with orientation and spacing, text state bindings, visibility bindings with scroll flags, button defaults and command capture, thickness and size parsing, unknown-element and unknown-property rejection, invalid-property placement rejection, and malformed number/enum/command/value rejection. The compiler project is AOT-compatible and covered by the architecture gate.
