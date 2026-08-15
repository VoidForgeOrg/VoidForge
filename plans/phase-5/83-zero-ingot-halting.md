# #83 — Zero-Ingot In-Flight-Build Halting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development — fresh subagent per task, review between.

**Goal:** When the ingot buffer empties and no ingots are being produced, pause in-flight ingot consumers — `UnderConstruction` buildings AND `Active` ship builds — and resume them (recomputing completion times) when ingot production returns. Completes `engine.md` cascade scenario 2's tail (refinery halts → ingot production stops → construction & shipyard builds halt).

**Architecture:** the ingot-consumer mirror of #70's ore-side `InputStarved`. A distinct `ConstructionHalted` building status and a new `ShipBuildStatus.Halted` (NOT the `Halted` producer status — those hard-set `Operational` on resume and levy the 5% draw). Each halted build captures a `HaltedAt` so resume recomputes `CompletesAt = resumeAt + (CompletesAt − HaltedAt)` and schedules a fresh completion. **Halt only in the clean zero-production case** (ingot production == 0 AND buffer empty) — mirroring `EvaluateInputStarvation` exactly, which avoids oscillation and the ill-defined even-split of fixed-drain consumers. The partial-production imperfection (production > 0 but < drain) retains today's clamp-only behavior; out of scope.

**Tech Stack:** .NET 9, Marten, Wolverine durable messages, xUnit + Alba.

**Spec:** #83 (split from #70); `plans/phase-5-hardening-design.md` §3/§4.

## Global Constraints
- `TreatWarningsAsErrors`; MA0048/MA0051. Branch `feat/83-zero-ingot-halting` off `phase-5`. Commits suffixed `(#83)`.
- BOTH `dotnet build -warnaserror` AND `dotnet format --verify-no-changes`. No local `dotnet test`.

## Plan-level decisions (from survey)
1. **Distinct statuses, dedicated events.** `BuildingStatus.ConstructionHalted`; `ShipBuildStatus.Halted`. Events `ConstructionHalted(SlotIndex, At)` / `ConstructionResumed(SlotIndex, At)`; `ShipBuildHalted(BuildId, At)` / `ShipBuildResumed(BuildId, At)`. Their `Apply`s handle status + `CompletesAt` recompute — NOT the #69 `BuildingHalted/Resumed` (which are producer-only and hard-set Operational). The existing resume evaluators filter `Status == Halted`, so `ConstructionHalted` is auto-skipped (no cross-talk); `Planet.Energy.cs`'s 5% draw only hits `Halted`, so a rating-less paused build correctly draws nothing.
2. **`DateTimeOffset? HaltedAt`** added to both `BuildingSlot` and `ShipBuild`. Halt sets it (keeps `CompletesAt`); resume computes `CompletesAt = resumeAt + (CompletesAt − HaltedAt)`, clears `HaltedAt`.
3. **Halt condition (zero-production only, mirrors ore):** `EvaluateIngotStarvation(at)` returns `[]` if `IngotProduction() > 0` OR `IronIngot.GetCurrentValue(at) > 0`; else halts every `UnderConstruction` building and every `Active` ship build. `IngotProduction()` = `RefineryIngotOutputFactor × effectiveConsumption` (a new helper, factored from `RebaseRates` like `CurrentOreInflow`). One trigger halts both consumer kinds (single planet-level ingot scalar).
4. **Ship-build bay accounting.** A halted ship build drops out of `ActiveShipBuildCount()` (so it draws no full-power energy — correct), but must NOT let a queued build auto-start into the same starvation: `StartQueuedBuilds`/capacity must treat `ConstructionHalted`+`Halted` ship builds as occupying a bay for auto-start purposes. Add an `OccupiedBayCount()` (Active + Halted) distinct from `ActiveShipBuildCount()` (Active only, energy).
5. **Reschedule on resume:** stale completions no-op on halt (validate-on-arrival). Resume emits the resume event AND schedules a fresh `CompleteBuildingConstruction`/`CompleteShipConstruction` at the recomputed `CompletesAt`.
6. **New 4th cascade check:** `PredictIngotBufferEmpty(now)` (mirror `PredictBufferEmpty` against `IronIngot`) + `CheckIngotStarved` message + `CheckIngotStarvedHandler` (clone of `CheckInputStarvedHandler`, self-reschedules off `PredictIngotBufferEmpty`), armed in `ScheduleAllChecksAsync`.
7. **Resume is composition-driven:** chained off the refinery-resume commit (`CompleteBuildingConstructionHandler`, where ore returns → refinery un-starves → ingots flow) — evaluate ingot-consumer resumes after the refinery resume is applied in-memory (the `ApplyCompletionsForResumeEvaluation` double-apply pattern). Cargo-ingot-delivery resume path is a documented follow-up (the ore analogue is also unwired).

## Task 1 — Building construction ingot-halt/resume (domain, unit-tested)
**Files:** `Domain/BuildingStatus.cs` (`ConstructionHalted`), `Domain/BuildingSlot.cs` (`HaltedAt`), `Domain/Events/ConstructionHalted.cs` + `ConstructionResumed.cs` (new), `Domain/Planet.Halting.cs` (`IngotProduction`, `EvaluateIngotStarvation` buildings-only for now, `PredictIngotBufferEmpty`, Applys), `Domain/Planet.cs` (audit `LiveBuildingCount`/status filters for `ConstructionHalted`). Tests: `PlanetIngotStarvationTests.cs`.
- `ConstructionHalted` status (comment: paused mid-build, occupies its slot for `LiveBuildingCount`, draws/produces nothing, NEVER `Halted`).
- `HaltedAt` nullable on `BuildingSlot`.
- `IngotProduction()` helper (factor from RebaseRates); `PredictIngotBufferEmpty(now)`.
- `EvaluateIngotStarvation(at)` (buildings only in Task 1): `[]` if production>0 OR ingot buffer>0; else `ConstructionHalted(slot, at)` per `UnderConstruction` slot.
- `Apply(ConstructionHalted)`: status `ConstructionHalted`, `HaltedAt = at`, `RebaseRates` (drain drops). `Apply(ConstructionResumed)`: status `UnderConstruction`, `CompletesAt = at + (CompletesAt − HaltedAt)`, `HaltedAt = null`, `RebaseRates`.
- Audit: `LiveBuildingCount` must count `ConstructionHalted` as occupied (it's not `Cancelled`/`Demolished` — confirm it's included); RebaseRates `constructionDrain` filters `UnderConstruction` so a `ConstructionHalted` slot correctly drops its drain; energy filters don't touch it.
- Unit tests: halt an under-construction slot at zero-ingot → `ConstructionHalted`, drain removed; resume → `UnderConstruction`, `CompletesAt` pushed out by the paused duration; `EvaluateIngotStarvation` no-op when production>0 or buffer>0; stale `CompleteBuildingConstruction` no-ops on a `ConstructionHalted` slot.
- Build + format. Commit: `feat: construction ingot-halt/resume — ConstructionHalted status, remaining-work recompute (#83)`.

## Task 2 — Ship-build ingot-halt/resume (domain, unit-tested)
**Files:** `Domain/ShipBuildStatus.cs` (`Halted`), `Domain/ShipBuild.cs` (`HaltedAt`), `Domain/Events/ShipBuildHalted.cs` + `ShipBuildResumed.cs` (new), `Domain/Planet.Ships.cs` (`OccupiedBayCount`, auto-start audit, Applys), `Domain/Planet.Halting.cs` (extend `EvaluateIngotStarvation` to ships + a resume evaluator). Tests: `PlanetShipyardTests.cs`/`PlanetIngotStarvationTests.cs`.
- `ShipBuildStatus.Halted`; `HaltedAt` on `ShipBuild`.
- Extend `EvaluateIngotStarvation(at)` to also emit `ShipBuildHalted(buildId, at)` per `Active` ship build.
- `Apply(ShipBuildHalted)`: status `Halted`, `HaltedAt = at`, `RebaseRates` (drops from `shipBuildDrain`). `Apply(ShipBuildResumed)`: status `Active`, `CompletesAt = at + (CompletesAt − HaltedAt)`, `HaltedAt = null`, `RebaseRates`.
- `OccupiedBayCount()` = Active + Halted; use it in `StartQueuedBuilds`/`QueueShip`/`CompleteShipBuild` auto-start capacity so a queued build does NOT start into the starvation; keep `ActiveShipBuildCount()` (Active only) for the `Planet.Energy.cs` fungible-bay math (halted builds draw no energy). Audit every `ActiveShipBuildCount`/`ShipyardCapacity` call site.
- `EvaluateIngotStarvationResumes(at)`: if production>0 OR buffer>0, emit `ConstructionResumed`/`ShipBuildResumed` for every `ConstructionHalted`/ship-`Halted` build.
- Unit tests: halt active ship build → `Halted`, ship drain removed, draws no energy, does NOT free a bay for a queued build; resume → `Active`, `CompletesAt` pushed out; auto-start still works normally when not starved.
- Build + format. Commit: `feat: ship-build ingot-halt/resume + bay accounting (#83)`.

## Task 3 — CheckIngotStarved message, handler, scheduling wiring
**Files:** `Domain/Events/CheckIngotStarved.cs` (new), `Endpoints/CheckIngotStarvedHandler.cs` (new), `Endpoints/StorageHaltScheduling.cs` (arm the 4th check). Tests: integration.
- `CheckIngotStarved(Guid PlanetId, DateTimeOffset PredictedAt)` message.
- `CheckIngotStarvedHandler` (clone `CheckInputStarvedHandler`): FetchForWriting → `EvaluateIngotStarvation(PredictedAt)` → AppendMany → SaveChanges → FetchLatest → self-reschedule off `PredictIngotBufferEmpty`.
- `ScheduleAllChecksAsync`: add the ingot-buffer-empty deadline (`PredictIngotBufferEmpty` → `CheckIngotStarved`).
- Integration test (direct-handler, deterministic — mirror `PlanetInputStarvationTests`/`StorageHaltingTests`, pin ingot buffer empty via a seed/load event): drive a planet to zero ingot production + empty buffer → `CheckIngotStarvedHandler` halts the in-flight construction and ship build; stale completions no-op.
- Build + format. Commit: `feat: CheckIngotStarved scheduled check + handler + scheduling (#83)`.

## Task 4 — Resume wiring + reschedule + cascade e2e
**Files:** `Endpoints/CompleteBuildingConstructionHandler.cs` (chain ingot-consumer resume after the refinery resume), a resume-reschedule helper (schedule fresh `CompleteBuildingConstruction`/`CompleteShipConstruction` for resumed builds), `CheckIngotStarvedHandler` (also evaluate resumes? — no: resumes are composition-driven, not check-driven; keep them on the ore-return commit). Tests: cascade e2e.
- In `CompleteBuildingConstructionHandler` (and the ore-resume path), after `EvaluateInputStarvationResumes` restores a refinery in-memory, evaluate `EvaluateIngotStarvationResumes(at)` and append the resume events; for each resumed build, schedule a fresh completion at its recomputed `CompletesAt` (a `ScheduleResumedBuildsAsync` helper that scans `ConstructionResumed`/`ShipBuildResumed` events and issues the right `bus.ScheduleAsync`).
- Cascade e2e (deterministic, direct-handler; the full scenario-2 chain): deplete ore → drills halt → refinery starves (`InputStarved`) → ingot production stops → in-flight construction + ship build halt (`CheckIngotStarved`) → (restore ore, e.g. via a new drill completing) → refinery resumes → ingots flow → construction + ship build resume with pushed-out completion → they complete. (Note: the #71 cascade-scenario tests also cover this; the machinery-level e2e lands here.)
- Build + format. Commit: `feat: ingot-consumer resume + reschedule; scenario-2 cascade e2e (#83)`.

## Task 5 — Docs + PR
- `technical-design/domain-model.md`: `ConstructionHalted`/`ShipBuildStatus.Halted`, `HaltedAt` remaining-work, the four new events + `CheckIngotStarved`, the zero-production halt condition, bay accounting, reschedule-on-resume. `game-design/engine.md`/`resources.md` if wording drifts.
- PR `feat/83-zero-ingot-halting` → `phase-5`, "Closes #83". Self-merge on green CI.

## Hardest decisions (flag for review)
1. **Distinct-status vs. reason discriminator** (decision 1) — a `ConstructionHalted` slot must never be picked up by the producer resume evaluators or the 5% draw. Audit every status filter.
2. **Ship bay accounting** (decision 4) — the Active-vs-Occupied split; a queued build must not auto-start into the starvation, and halted builds must draw no energy. This is the subtlest ripple.
3. **Resume recompute + reschedule** (decisions 5,7) — `CompletesAt` recompute is per-build; the fresh completion must be scheduled at the new time, chained off the ore-return commit with the double-apply pattern.
