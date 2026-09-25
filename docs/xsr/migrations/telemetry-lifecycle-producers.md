# Anonymous lifecycle event producers

The composition root subscribes the telemetry session to the sealed Minecraft process
collection and registers it as a classification-only log sink. Services do not read Desktop
state. `game.started` means a JVM process was created, not that its window was confirmed.
Each session records creation and at most one terminal event. Repeated state publications
do not duplicate outcomes. Session IDs are used only for local deduplication, never uploaded.

Error log entries and GUI lifetime exceptions produce `app.failure/failed`; exception text,
module names, paths, account names and logs never enter telemetry. All four existing event
names retain the same version/OS/architecture/result whitelist and channel policy. Stable
builds remain opt-in; CI/Alpha/Beta collection remains mandatory and disclosed during setup.
Transport is best effort on its existing background timer. A fatal crash, offline session
or immediate exit may prevent delivery; no promise of synchronous crash reporting is made.
