# Alpha 5 — controlled modpack benchmarks for model seeding

Status: the initial console harness and a single-pack Actions pilot are implemented; no eligible seed
dataset exists yet. Real-run acceptance and scenario adapters remain incomplete.
This extends `online-resource-learning.md`; existing user-session telemetry must not be relabeled or
silently mixed with benchmark runs. GitHub Actions orchestrates runs, but a hosted VM is not a
representative physical gaming PC.

Architecture: `tools/Nexa.Minecraft.Benchmarks` is an intentional console host referencing only
Services and XSR State. It composes existing installation and launch services; it neither depends on
Desktop/UI nor introduces a second installer or JVM launcher. Its local artifacts are not telemetry
and cannot be admitted to training until scenario/provenance validation is implemented.

Run `dotnet build tools/Nexa.Minecraft.Benchmarks -c Release`, then pass archive, SHA-256, Java
executable, published Nexa.Jvm.Host executable, a nonexistent output directory, heap MiB and duration
seconds (60..1800) to the generated apphost. The harness verifies the pack before inspecting Java,
uses isolated game storage, drains Jvm.Host sample windows, and records terminal/continuity/forced-stop
status in `run.json` alongside `samples.csv`. It deliberately sets `trainingEligible=false`: phase
adapters, full hardware provenance, and real-pack validation are still pending. `context.json` carries
the existing Jvm.Host context's loader/components and bounded mod IDs, versions and dependency ranges,
including explicit unknown/incomplete flags. No context is represented as unavailable, not an empty
complete inventory. The shared reader follows bounded Fabric nested JAR candidates; this is not
proof of runtime activation. Forge JarJar and Quilt nesting remain incomplete.
The created `game` directory is local scratch data and must not be uploaded as a CI artifact.
`eng/xsr/Test-MinecraftBenchmark.ps1` checks argument/hash/storage boundaries without downloading games.

The pilot pins Fabulously Optimized 6.5.0 / Modrinth N276l2ON (Minecraft 1.21.1, Fabric 0.19.3).
The archive's published SHA-512 and computed SHA-256 are recorded in `eng/benchmarks/packs.json`.
The Actions job uses a non-root, read-only Linux container, software rendering, two CPUs and a 6 GiB
memory cap. It mounts only published tools, Java, the pack and a fresh output directory, without CI
credentials. The container is stopped before a separate bounded no-follow collector normalizes the
three allowed measurement files. The context has a 16 MiB actual-read ceiling, up to 4096 mod entries,
and explicit metadata allowlists; the collector strips extra fields and validates identifiers/ranges.
Each mod retains up to 64 dependencies, matching the shared metadata reader. The collector checks
integer counters, ordered windows, terminal/exit consistency, measured peaks and summary counts.
Unknown observations retain their -1 sentinel; empty windows must not fabricate zero measurements.
These integrity checks do not establish scenario eligibility or make container output trustworthy.
No recursive game-directory/log upload is allowed. The job can be
triggered manually and runs on changes to its reviewed catalog/workflow; there is no nightly large
matrix yet. Container/runner performance is not a hardware-rendered player baseline.

Pilot run 36103488629 timed out before producing the output directory or printing the install
stage; it produced no training sample. Independent 20-second native-host, Java and virtual-display
probes now precede installation, and fixed stage labels distinguish archive inspection, Java probing
and launch-file completion. A successful environment probe does not establish a working game.

Run 36105255472 passed native-host, Java and first virtual-display probes, then stalled before
the measured host printed archive verification. Local Linux NativeAOT enters archive inspection
with minimal inputs. Keep one X server across the display probe and measured client, rather than
tearing it down and starting a second unobserved server. Separate display-ready, process-entry and
cancellation-registration markers narrow any remaining stall; the cause is not yet proven.

Run 36107466153 narrowed the stall to virtual-display startup, before display-run.sh or the
measured host ran. Reproduced locally with Linux `unshare --mount --pid --fork --mount-proc`:
running xvfb-run as namespace PID 1 timed out (137); running it under a supervising timeout process
completed (0). Docker now uses `--init`; the entrypoint refuses PID 1 immediately (78), and CI
checks actual Xvfb startup in the restricted container before importing a pack. This fixes the
identified startup structure, but real game/scenario acceptance still requires a successful pilot.

## Dataset and runner selection

Maintain a reviewed, versioned pack catalog with provider project/version IDs, archive hashes,
Minecraft/loader/Java versions, expected resource limits and supported scenario adapters. Download
from supported provider APIs and use Nexa's bounded archive importer. Honor unavailable download
URLs and client/server environment metadata. Do not mirror pack binaries in benchmark artifacts.
Select diverse lightweight, medium and heavy packs, not just the most downloaded packs. Expand the
catalog after the first end-to-end runs succeed; a small pilot is not completion of the requested
large dataset. Pin all inputs so later results can reproduce the same pack.

Use disposable GitHub-hosted Linux machines for an initial client-start/resource-load lane, with
virtual display and an explicitly recorded software renderer when necessary. Record actual runner
CPU/RAM/architecture/virtualization and GPU/driver, not only the workflow label. The standard hosted
runner's disk/RAM budget excludes some large packs. A limit failure is an incomplete experiment,
not a low-memory observation. Never substitute a dedicated server for a failed client run.

Use a separate, opt-in ephemeral self-hosted VM/physical-machine lane for hardware-rendered world
scenarios. Availability of this hardware is not yet established. Do not execute community mods on
the maintainer's persistent workstation. Execution workers have no release/deployment secrets,
no persisted checkout credentials, and no model-publishing permission. Privileged ingestion and
publication run separately and treat uploaded artifacts as untrusted bounded input.

## Real scenarios and observation

Install using Nexa Services and start with the production Jvm.Host path. A benchmark adapter must
report explicit phase readiness; elapsed wall time alone is not proof that a world loaded. Support
each Minecraft/loader family intentionally, and record unsupported adapters instead of claiming
coverage. No synthetic Java fixture may count as a Minecraft memory run.

Phases: process start -> client menu/resource load -> fixed saved world ready -> settled idle ->
fixed traversal/chunk loading -> controlled resource reload -> normal shutdown. Record a fixed seed,
world artifact hash, traversal script version, view/simulation distances, graphics/FPS, Xmx, Java
identity, loader and complete mod inventory. Menu-only, world and reload peaks remain separate.
Distinguish first-launch/cold-cache from warm repeats. Vary heap/settings across separate runs with
the same scenario and repeat measurements; do not count repeated runs as independent packs/users.

Reuse Jvm.Host process sampling for working set/private bytes/CPU/timing. Missing samples remain
unavailable. Java heap/native/commit need their own reliable collectors; do not infer these from
working set. Timeout, forced termination, missing phase markers, OOM and incomplete mod inventories
are recorded as experiment outcomes, never zero resource use or successful stable baselines.

## Training and release admission

Add an explicit benchmark provenance contract before ingestion: source, suite/catalog revision,
pack/version/hash, scenario, repeat, runner hardware/rendering class, collector version, phase
completeness, settings and observations. Existing `diagnostic.run` sessions do not carry this
information; do not upload benchmark runs through that route under a fabricated user session.

Build a bounded prior model from eligible benchmark cohorts. Keep software-rendered VM priors
separate from hardware-rendered client priors. Group train/validation splits by pack family/version
and runner family: near-identical repeats and pack revisions must not leak across the split.
Report eligible pack count, repetitions and scenario coverage, not just the total number of samples.
Evaluate on independent user cohorts before allowing a benchmark prior to affect production
recommendations. Its influence must be explicitly capped and decrease as matching user evidence
grows. Missing matching hardware/scenario evidence keeps the existing local estimator.

Benchmark seeds are estimates, not verified compatibility or hard minimums. They must never create
hard launch blocks. A mod's marginal cost cannot be inferred by dividing pack memory by mod count;
mod-level contributions need controlled differences or sufficiently supported regularized estimates,
and must carry uncertainty. Existing v1 working-set models cannot express this provenance: define a
versioned successor and its admission tests before publishing benchmark-derived parameters.

## Implementation and acceptance sequence

1. CLI harness reusing import, launch planning, Jvm.Host and sampling; smoke a real pinned pack.
2. Explicit phase adapter and deterministic saved-world scenario with a real rendered client;
   preserve measurements as local bounded artifacts and verify failure classification.
3. Manual Actions pilot with reviewed pack matrix, per-job disk/memory/time caps and limited
   concurrency. Artifacts contain normalized measurements, no credentials/paths or bundled mods.
4. Separate ingestion, provenance validation, deduplication and grouped training/holdout tests.
5. Client model-version support, bounded prior blending and administrator benchmark-quality summary.
6. Expand the catalog and runner matrix, then enable scheduled collection with a measured budget.

Do not mark these complete based on YAML validity or a Java fixture. Required evidence includes a
real imported pack reaching each claimed game phase, sampler output, repeatability, CI artifacts,
held-out evaluation and a verified client fallback when no applicable prior exists.

References: GitHub hosted runner specifications and limits, Modrinth `.mrpack` specification, and
CurseForge file distribution/download availability. Consult current provider documentation when
implementing the workflow; quotas and runner specifications can change.
