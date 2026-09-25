# Version directory drag out

Desktop projects local directory paths into an immutable UI.Next file-drag component. The
renderer validates the input target and cancels competing click/scroll gestures before a native
transfer. The Avalonia host resolves storage items and offers standard File data to the OS; it
does not resolve Services or read ECS components. Touch continues to scroll. Mouse movement must
cross a drag threshold; row action buttons remain independent input targets.

Copy, Move and Link are negotiated by the native target, including its Ctrl/Shift/Alt conventions.
The source never deletes paths after a reported Move: file-system targets perform the move, and
deleting by the old path could delete newly recreated data. Completion emits a refresh intent.
Escape/cancellation must not activate the original row. A running instance offers Copy/Link only.

Modifier clicks use renderer intents: Ctrl (Command on macOS) toggles a transfer selection,
Shift replaces it with the visible range from the anchor, Ctrl+Shift adds that range. They never
change the current launch version or navigate. Highlight indicates transfer selection; the check
still indicates the current launch version. Dragging a highlighted row transfers the selected set
in visible order; an unselected row transfers only itself. A running member disables Move for the
whole transfer. Filtering prunes hidden selections; changing root or leaving the page clears them.
Ordinary clicks still select the launch version and immediately return home. Complete cross-platform
native target acceptance remains pending; incoming imports still advertise Copy only.
