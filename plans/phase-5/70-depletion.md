# #70 — Resource Depletion + Refinery Ore-Starvation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development — fresh subagent per task, review between.

**Goal:** The finite `IronOrePool` drains as drills extract; when it empties, every drill halts **permanently** (`ResourceDepleted`). Refineries draw down the stored `IronOre` buffer when drill inflow is insufficient, and halt `InputStarved` when both inflow and buffer are exhausted — resuming when ore returns. Delivers the depletion cascade (pool → drills → refineries) that #71 will test.

**Scope (re-scoped — see #70 comments):** depletion + **refinery-ore** starvation only. **Zero-ingot** in-flight-build halting is **#83** (needs new per-build remaining-work state + a whole new ship-build halt machinery — separable, harder).

**Builds on #69 (merged):** `BuildingStatus.Halted`, `HaltReason` (`ResourceDepleted`/`InputStarved` already defined), `BuildingHalted`/`BuildingResumed` + `Apply`, the `CheckStorageFull` scheduled-message + validate-on-arrival + `StorageHaltScheduling` pattern, `PredictStorageDeadlines`, `EvaluateStorageHalts`/`EvaluateStorageResumes`.

**Tech Stack:** .NET 9, Marten, Wolverine durable messages, xUnit + Alba.

**Spec:** `plans/phase-5-hardening-design.md` §3, D4.

## Global Constraints
- `TreatWarningsAsErrors`; MA0048/MA0051. Branch `feat/70-depletion` off `phase-5` (after #44 merges). Commits suffixed `(#70)`.
- Verify locally with BOTH `dotnet build -warnaserror` AND `dotnet format --verify-no-changes` (the lint CI job runs the format check — IDE1006 etc.). Do NOT run `dotnet test` (shared DB; CI verifies).
- Reuse `ResourcePool` for the pool so the #44 floored-elapsed/non-regressing invariant is inherited (D14's structural clamp-at-append stays unshipped, so drain math must stand on its own against out-of-order `at` — `ResourcePool` already does).

## Plan-level decisions
1. **`IronOrePool` becomes a draining `ResourcePool`** (`Rate = -extractionRate`, `CheckpointValue = remaining`, `StorageCapacity =` the seeded initial value so `GetCurrentValue` clamps `[0, initial]`). Required by the "pool remaining drains over time in the read API" acceptance criterion (a static `long` can't). `PlanetCreated.IronOrePool` stays a `long` seed; `Apply(PlanetCreated)` constructs the `ResourcePool` from it. `PlanetResponse` reports `GetCurrentValue(now)` (still surfaced as an integer-ish decimal — keep the API field shape sensible).
2. **Extraction rate = `oreInflow`** (Σ drill extraction × productivity multiplier) — the same quantity `RebaseRates` already computes. The pool's drain `Rate = -oreInflow`; recomputed every `RebaseRates`.
3. **Buffer-drain = relax the `Math.Min` clamp.** `effectiveConsumption` may exceed `oreInflow` while `IronOre.GetCurrentValue(at) > 0`, so `IronOre.Rate = oreInflow − refineryDemand` can go **negative** (buffer draining). This introduces a **buffer-empty crossing instant** — a new scheduled check (symmetric to #69's time-to-full): predict time-to-empty for a negative-rate ore buffer, schedule `CheckInputStarved`, and at that instant re-derive (refinery re-clamps to inflow, or halts `InputStarved` if inflow is 0).
4. **Depletion halts are permanent, for free.** `EvaluateStorageResumes` already filters `HaltReason == OutputStorageFull`, so a `ResourceDepleted` drill is skipped by every resume evaluator. No extra work for "depleted never resume."
5. **Refinery InputStarved resume is composition-driven**, not external-commit-driven like #69's storage resume. Fold input-starvation evaluation into the halt/resume path that every `RebaseRates`-committing handler runs, so a freed input (a resumed/new drill → ore flows) un-starves its refinery in the same commit. Introduce a unified `Planet.EvaluateHaltsAndResumes(at)` the handlers call (supersedes calling only `EvaluateStorageHalts`/`Resumes` piecemeal).

## Task breakdown (subagent-driven, review between)

### Task 1 — `IronOrePool` drains (type change + read API + RebaseRates drain; NO depletion halt yet)
- Convert `Planet.IronOrePool` (`long`) → a `ResourcePool` (new field, e.g. `IronOreDeposit`), constructed in `Apply(PlanetCreated)` from `@event.IronOrePool` with `Rate = 0` initially, `StorageCapacity = @event.IronOrePool`.
- In `RebaseRates`: checkpoint the deposit at `at` and set `Rate = -oreInflow` (drains as drills extract). Ensure `GetCurrentValue` floors at 0 (already does).
- `PlanetResponse`: report `deposit.GetCurrentValue(now)` for `IronOrePool` (keep the response field name/shape; adjust type if needed — decimal is fine).
- Fix the affected tests: `PlanetAggregateTests` (IronOrePool reads), `PlanetEndpointTests:65`. Add a unit test: pool drains at `-oreInflow` over time; `GetCurrentValue` floors at 0.
- Build + format clean. Commit: `feat: IronOrePool drains at the extraction rate (ResourcePool) (#70)`.

### Task 2 — Depletion event + scheduled check + drill halt (permanent)
- New `PlanetResourceDepleted(ResourceType Resource, DateTimeOffset At)` event + `Apply` (checkpoint the deposit to 0 at `At`).
- New `CheckPoolDepleted(Guid PlanetId, DateTimeOffset PredictedAt)` message + `CheckPoolDepletedHandler` (clone `CheckStorageFullHandler`: FetchForWriting → `EvaluateDepletion(PredictedAt)` → AppendMany → SaveChanges → FetchLatest → reschedule).
- `Planet.EvaluateDepletion(at)`: if `IronOreDeposit.GetCurrentValue(at) <= 0` and any Operational Drill exists, emit `PlanetResourceDepleted(IronOre, at)` + one `BuildingHalted(slot, ResourceDepleted, at)` per operational Drill.
- `Planet.PredictDepletionDeadline(now)`: `now + remaining / extractionRate` when `extractionRate > 0` and `remaining > 0` (else none). Wire into the reschedule sites (Task 4).
- Unit tests (mirror `PlanetHaltingTests`, add a `DrainDepositToNearEmpty` helper): depletion emits the event + drill halts; depleted drill never resumes (run a storage-resume eval, assert it stays halted); `PredictDepletionDeadline` math.
- Build + format clean. Commit: `feat: ore-pool depletion halts all drills permanently (#70)`.

### Task 3 — Buffer-drain + refinery InputStarved
- Relax `RebaseRates`' `effectiveConsumption = Math.Min(refineryDemand, oreInflow)` so refineries draw the stored buffer: `effectiveConsumption = refineryDemand` when `IronOre.GetCurrentValue(at) > 0` (buffer available), clamped to `oreInflow` only when the buffer is empty. Set `IronOre.Rate = oreInflow − effectiveConsumption` (may be negative). **Revisit `PlanetAggregateTests.ApplyBuildingPlacedRefineryDemandClampedToDrillInflow`** — it asserts the old clamp; update it to the buffer-drain semantics with a clear comment.
- Buffer-empty predictor: `Planet.PredictBufferEmpty(now)` → time-to-empty for a negative-rate `IronOre` (symmetric to `PredictStorageDeadlines`). Feed a `CheckInputStarved(PlanetId, PredictedAt)` message + handler that calls `EvaluateInputStarvation(at)`.
- `Planet.EvaluateInputStarvation(at)`: an Operational Refinery with `oreInflow == 0` AND `IronOre.GetCurrentValue(at) <= 0` halts `InputStarved`.
- Unit + integration tests: after drills deplete, the refinery draws the buffer down, then halts `InputStarved` at the predicted instant; ingot production stops.
- Build + format clean. Commit: `feat: refineries drain the ore buffer and halt InputStarved when dry (#70)`.

### Task 4 — Unified halt/resume evaluation + scheduling wiring + resume
- Introduce `Planet.EvaluateHaltsAndResumes(at)` returning the union of storage-full halts, storage resumes, depletion, and input-starvation halts/resumes appropriate at `at`. Refinery InputStarved **resume**: a starved refinery with `oreInflow > 0` again (a drill resumed/was built) resumes — composition-driven, evaluated on every `RebaseRates`-committing handler.
- Wire `CheckPoolDepleted` + `CheckInputStarved` scheduling into the six reschedule sites already calling `StorageHaltScheduling.ScheduleDeadlinesAsync` (from a fresh `FetchLatest`): `BuildingEndpoints`, `ShipEndpoints`, `PlayerEndpoints` (seeded homeworld), `FleetEndpoints` (cargo path), both completion handlers, and each check handler's self-reschedule. Extend `StorageHaltScheduling` (or add a sibling) to schedule all deadline kinds.
- Integration test — the depletion cascade e2e: deplete the ore pool → all drills halt (`ResourceDepleted`) → refinery draws the buffer down → refinery halts (`InputStarved`) → ingot rate 0. Deterministic via direct handler invocation + pinning the deposit low via a seed event (mirror `StorageHaltingTests`).
- Build + format clean. Commit: `feat: unified halt/resume evaluation + depletion/starvation scheduling; cascade e2e (#70)`.

### Task 5 — docs + PR
- Update `technical-design/domain-model.md`: deposit drain, `PlanetResourceDepleted`, `CheckPoolDepleted`/`CheckInputStarved`, buffer-drain semantics, permanent-depletion-via-resume-filter. Note `game-design/resources.md`/`engine.md` if wording drifts.
- PR `feat/70-depletion` → `phase-5`, "Closes #70" (note zero-ingot → #83). Self-merge on green CI.

## Hardest decisions (from survey) — flag for reviewers
1. **Buffer-empty crossing (Task 3)** is genuinely new rate-engine behavior (negative ore rate + a scheduled re-derivation instant). Watch even-split under a draining shared buffer (two refineries).
2. **`IronOrePool` type change (Task 1)** ripples to the `PlanetCreated` payload/API/tests — keep the seed a `long`, model the live value as `ResourcePool`.
3. **Unified evaluation (Task 4)** — make sure composition-driven refinery resume fires in the same commit as the drill resume that feeds it.
