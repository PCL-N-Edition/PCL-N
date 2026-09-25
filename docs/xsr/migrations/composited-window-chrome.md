# Composited window chrome

The main window may use hardware-composited transparent edge pixels (user-approved supersession
of the earlier opaque-window requirement). Application content stays opaque. Windows keeps its
caption/resizing styles and prefers WinUIComposition / DirectComposition. A WS_EX_LAYERED fallback
is rejected for the main window; unsupported systems retain the opaque/native-region fallback.

Windows/Linux restored windows reserve 24 DIPs outside the scene for the soft shadow. Initial
scene size remains 850×500 and minimum scene size remains 810×470. The host Border owns this
margin; XSR content inset stays zero, avoiding double padding and input-coordinate offsets.
Maximized/fullscreen windows remove the margin, shadow and radius. macOS currently retains zero
outer margin so native traffic lights stay aligned; separate traffic-light relocation is pending.

Composited edges use a 24-DIP antialiased rounded clip without SetWindowRgn. Circular open/close
masks remain compositor geometry all the way to radius zero, with the icon outside the mask.
Native regions are used only for opaque fallback. System animation, mixed-DPI behavior and visual
quality still require native acceptance; headless layout tests cannot prove those properties.

The DWM non-client rendering policy is disabled to remove the native rectangular shadow around
the expanded HWND. Composition stays enabled, caption/resize/minimize/maximize styles are retained,
and the transitions-disabled attribute is not set. Reapply after platform state changes because
Avalonia restores its frame policy during maximize/restore. Windows may report a rectangular native
region in this mode; it must contain all client corners, leaving rounded clipping to the compositor.
Native regression checks non-client rendering disabled and native capability styles retained through
resize/maximize/minimize/restore. Perceived system animation still requires visual acceptance.

Validation: backend console tests and the 31-project architecture regression pass. Windows native
`--native-corner-smoke` passes at 125% scale: actual transparency is Transparent, WS_EX_LAYERED
is absent, no native clipping region, scene size preserved within one physical pixel, input origin
offset exactly 24 DIPs. Resize, maximize/minimize restoration and opaque/composited fallback changes
preserve the viewport. This tests native state and layout, not subjective animation or edge quality.
