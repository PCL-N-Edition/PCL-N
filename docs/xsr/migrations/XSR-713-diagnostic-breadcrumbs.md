# XSR-713 English diagnostic breadcrumbs

## Scope locked before implementation

- Keep the existing host `LogService`, redacted state ring and console/file sinks. No global
  logger, UI dependency in Services, or request/credential serialization is introduced.
- Launcher-authored log templates are English. Product UI localization is unchanged; paths,
  identifiers and external data are not translated. Exception diagnostics use type, HRESULT,
  HTTP status and stack rather than localized, potentially credential-bearing response text.
- Low-volume operations record start, named stages and terminal outcome with an operation ID,
  elapsed milliseconds and source location. Failure records retain the last stage. Item/byte
  loops remain Debug/RealTime; expected cancellation is not reported as an unexpected fault.
- Connect sinks before Foundation services load persisted data. Wire the dispatch observer to
  Foundation, account onboarding and Minecraft routers. Record route start and completion with
  the same correlation ID, without inspecting payloads. Observer failures cannot break dispatch.
- Scheduler faults remain visible at Error under the default Info gate; normal executions stay
  RealTime. Bootstrap, shell validation, GUI lifetime and shutdown failures have named stages.
- Instrument profile/settings persistence, login provider exchanges, instance discovery, launch
  preparation/Java selection, native extraction, process lifecycle and download commit/failover.
  Never log passwords, device codes, tokens, settings values, HTTP bodies or complete launch argv.

## Acceptance

- Operation stages/outcomes are correlated, durations are culture-invariant, and source locations
  identify the call site. Failure and cancellation cannot accidentally acquire a success record.
- Startup load errors reach a sink attached by the production composition API.
- Production account/Minecraft routes emit dispatch diagnostics. Faulted scheduled work is visible
  at the release log level. Successful operations and injected faults exercise real service paths.
- Security regressions cover secrets in quoted assignments, authorization codes and settings;
  diagnostic summaries do not serialize raw provider exceptions or credentials.
- Managed suites, formatting, architecture and Desktop NativeAOT/trim smoke remain required.

## Reading a report

- Desktop appends UTF-8 entries to `<AppFolders.Root>/logs/launcher.log`. On Windows the default
  is `%LOCALAPPDATA%/PCL Nexa/logs/launcher.log`; `PCL_NEXA_DATA_DIR` overrides the data root.
  Folder/schema failures before host composition use that same file when the logs directory is
  writable, otherwise stderr is the last-resort diagnostic channel.
- Search for `failed`, `rejected` or `[Error]`, then follow `op=<id>` for service stages or
  `cid=<id>` for route start/completion. Process records also carry `session=<id>` and `pid=`.
  These IDs have separate scopes; do not assume one `op` equals a route correlation ID.
- `stage=` identifies the last operation step; `source=File.cs:line` identifies its declaration;
  `elapsed_ms=` uses invariant formatting. Failure summaries include exception type/HRESULT,
  stack and HTTP status when available. UI error messages and raw provider bodies stay out.
- Alpha/beta/CI builds and console-attached launches retain RealTime detail. Detached release
  launches default to Info; failures still retain operation context when Debug starts are hidden.
- HTTP transport diagnostics contain only method, host, status and duration. Expected OAuth
  polling responses remain Debug; service-level rejection explains the failed authentication
  stage. Settings log keys/types, never raw values; profile logs use index/kind, not credentials.

## Compatibility and coverage

`IXsrDispatchObserver.OnStarted` has a default no-op implementation so existing observers keep
working. The observation contains identifiers only; Plugin/Sidecar wire contracts are unchanged.
Optional logger parameters and the Foundation `configureLogging` hook preserve existing callers.
Logging stays owned by Foundation and does not change the PXML/UI.Next rendering boundary.

Regressions live in `DiagnosticLogTests`, the production native-launch/Microsoft/Java-installer
fixtures, `OperationLogTests`, and the cross-capability state-binding test. The latter now expects
both the setting and its log binding to invalidate, while unrelated state remains untouched.

## Validation evidence

- Managed: Services 194, Desktop 38, Runtime 89, UI.Next 69, PXML 37, Sidecar 19, and the
  Avalonia backend suite pass. Architecture checks pass for 29 projects; source formatting passes.
- NativeAOT Services: all 194 tests pass, including stage/source diagnostics, quoted-secret
  redaction, production router observation, HTTP behavior and injected persistence/process faults.
- Desktop NativeAOT and trimmed publishes both execute `--validate-shell` successfully and write
  English version/startup/validation/shutdown records to an isolated data root.
- An executable smoke with a regular file occupying the settings directory exits 1 and writes
  `stage=ensure_settings_folder` plus the exception facts to `launcher.log`, before Foundation exists.
