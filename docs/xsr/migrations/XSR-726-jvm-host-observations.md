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
