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

This slice wires directory drag out. Multi-selection and complete cross-platform native target
acceptance remain separate work; ordinary incoming imports still advertise Copy only.
