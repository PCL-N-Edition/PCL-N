# XSR-604 — Minecraft runtime composition

The Minecraft capability now has a real composition edge. `PCL.Services` owns semantic route
IDs and handler factories, while `PCL.Services.Composition` creates sealed Runtime routers and
injects the version-discovery and process ports. Desktop constructs that runtime alongside the
Foundation runtime, so production startup exercises the same route graph as the acceptance
tests.

The query surface includes `minecraft.versions.read` and `minecraft.crash.analyze`; the command
surface includes `minecraft.launch`. A launch request is planned and handed to the process port,
and failures are converted to stable `minecraft.launch_failed` errors. No service assembly
references `PCL.Xsr.Runtime` directly.
