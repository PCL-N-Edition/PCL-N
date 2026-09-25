# Alpha 5 — controlled modpack benchmarks for model seeding

Status: design accepted for implementation; no benchmark workflow or collected seed dataset exists yet.
This extends `online-resource-learning.md`; existing user-session telemetry must not be relabeled or
silently mixed with benchmark runs. GitHub Actions orchestrates runs, but a hosted VM is not a
representative physical gaming PC.

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
