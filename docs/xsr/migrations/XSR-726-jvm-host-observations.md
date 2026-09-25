# XSR-726 JVM host and runtime observations

## Boundary

`IJvmHost` is the launch executor's process boundary. `JvmHostService` adapts the existing,
tested `MinecraftProcessService`; it does not duplicate process ownership. It exposes a typed
environment description and `jvmhost.*` capability projection for arguments, classpath, native
path, process controls, redirected I/O, metrics and crash evidence.

The host samples working set, private bytes, thread count, CPU utilization and Windows I/O while
a JVM session is alive. It retains a bounded rolling sample window and calculates runtime P95.
At exit it publishes one bounded `JvmHostObservation` through the shared XSR state store.
The observation contains launch duration, runtime peaks, exit code and a redacted stderr tail.
The complete §58 `observation.*` projection converts this record back into typed metrics for
later estimator calibration. Metrics without a real host collector, currently JVM heap and GPU,
are `DependencyMissing`; zero never masquerades as a measured value.

Observation is best effort. Sampling, metric access and publication never alter process state or
turn a successful launch into a failure. The session remains the authority for start, wait,
process-tree termination and crash analysis.

## Compatibility

The planner records an explicit main-class index in the final argument vector. Host requests
must use this boundary rather than infer it from a classpath option (which can occur earlier
in manifest/custom JVM arguments). Legacy hand-built plans remain valid for subprocess launch,
but cannot be exported to an isolated host without this explicit boundary.

The isolated-host bootstrap contract is a versioned, length-prefixed binary frame over a private
stdin pipe, never a JSON request file or command-line credential payload. Strict UTF-8, a 4 MiB
frame budget, a 1 MiB string budget and 16,384 arguments per vector apply before allocation.
Truncated, oversized, unknown-version and trailing data are rejected. This is a one-time child
bootstrap, not synchronous Sidecar dispatch. The codec alone does not enable JNI execution;
the existing subprocess executor remains active until the native host is validated.

The dedicated `Nexa.Jvm.Host` executable references Services for the bootstrap protocol only,
without Desktop or renderer references. It reads exactly one bootstrap frame from stdin and
invokes JNI on a dedicated thread. JVM exceptions
are described to stderr and produce a nonzero exit; DestroyJavaVM waits for non-daemon threads.
The JVM library remains loaded until process exit. macOS first-thread/run-loop integration is
not yet available and this mode rejects macOS explicitly. Normal launch routing is unchanged
until end-to-end host transport and platform smoke tests are in place.

Windows .NET apphost CET conflicts with HotSpot initialization on supported hardware (reproduced
as 0xC0000409 before VM creation). Only the isolated Host opts out via CETCompat=false; Desktop
retains its default CET protection. See Microsoft's .NET 9 CET compatibility notice:
https://learn.microsoft.com/en-us/dotnet/core/compatibility/interop/9.0/cet-support.

`MinecraftLaunchExecutor(MinecraftProcessService, ...)` remains available and wraps the process
service in `JvmHostService`. A second constructor accepts `IJvmHost` for contract tests and future
platform hosts. Process state composition declares observation state alongside the existing
session and failure collections, so every existing composed process store remains valid.

## Validation

- JVM and game arguments split at the classpath/main-class boundary;
- classpath and native path survive the typed environment projection;
- the capability projection advertises supported and unsupported host operations explicitly;
- observation collections are bounded to 32 completed sessions;
- runtime physical, commit and CPU values are P95 values from bounded samples;
- unavailable heap/GPU metrics cannot calibrate the estimator down to zero;
- host observation errors remain isolated from launch success.
