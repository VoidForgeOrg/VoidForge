# Phase 5 — Hardening: Design Spec

**Date:** 2026-08-15
**Scope:** Storage caps & halting, resource depletion, cascading event resolution, building cancellation & demolition, fleet recall, even-split proof, and API polish with a capstone end-to-end test — plus the adjacent bug backlog (#44, #45/#61, #46, #58, #60, #62, #63). Scoring & leaderboard are **descoped** from the phase into standalone issues [#67](https://github.com/VoidForgeOrg/VoidForge/issues/67) and [#68](https://github.com/VoidForgeOrg/VoidForge/issues/68) (see D13).
**Builds on:** [ADR 0001](../technical-design/adr/0001-completion-event-resolution.md) (durable scheduled messages, validate-on-arrival, let-it-fire-and-no-op), `Planet.RebaseRates` (Phase 3's deterministic re-derivation), [#39](https://github.com/VoidForgeOrg/VoidForge/issues/39) (optimistic concurrency + retry), [#44](https://github.com/VoidForgeOrg/VoidForge/issues/44) (non-regressing checkpoints), Phase 4's `Fleet` aggregate and cross-aggregate single-commit arrival handling.
**Supersedes:** `plans/phase-5-hardening.md` item 21's ship-cancel route (shipped in Phase 3 as `DELETE /api/planets/{planetId}/ship-queue/{buildId}` against a planet-level FIFO queue — the per-shipyard-slot path is stale) and item 23 (scoring — descoped, D13).

## 1. Decisions made in this design

| # | Decision | Rationale |
|---|---|---|
| D1 | **Halts and resumes are explicit domain events (`BuildingHalted`, `BuildingResumed`) appended by scheduled check messages** — not state derived at read time | A halt changes production rates at a specific instant (storage hits cap), and the lazy model needs a checkpoint exactly there. That is ADR 0001's job: schedule `CheckStorageFull` at the predicted time, validate on arrival, append, `RebaseRates`. Stale checks no-op; outbox messages are never cancelled |
| D2 | **`BuildingStatus` gains `Halted`; buildings carry a `HaltReason` (`OutputStorageFull`, `InputStarved`, `ResourceDepleted`)**; halted buildings draw energy via `BuildingSpecs.HaltedDrawFactor = 0.05m` | The reason distinguishes resumable halts (storage/input) from permanent ones (depletion) and feeds the API. The factor is a new constant even though it equals `ShipyardIdleDrawFactor` — idle-shipyard and halted-building are different rules that happen to share a number today |
| D3 | **Deadline prediction is a pure `Planet` method; scheduling stays in endpoints/handlers** | After every `RebaseRates` the planet reports upcoming deadlines — time-to-full per pool `(capacity − current) / netRate`, time-to-empty for inputs, time-to-depletion `remaining / extractionRate`. Callers `ScheduleAsync` check messages after commit, same split as construction completions today. Rate changes schedule fresh checks; superseded ones fire and no-op |
| D4 | **`IronOrePool` joins the checkpoint math and drains at the extraction rate**; depletion halts all drills with `ResourceDepleted`, permanently | The pool is currently written once at `PlanetCreated` and never decremented. Pools cannot refill in MVP, so a depletion halt has no resume path — the building still draws 5% and demolition is the player's remedy |
| D5 | **Cascades resolve inside the existing `RebaseRates` re-derivation, committed in one `SaveChangesAsync`; the cascading-resolution issue is mostly tests** | `RebaseRates` already re-derives energy → productivity → rates from scratch on every composition change, so multi-step cascades (drill halts → energy freed → overload resolves) collapse into a single deterministic re-derivation. What Phase 5 adds is the building-states step (halt/resume) feeding it, then proves the four `engine.md` scenarios plus edge cases as integration tests |
| D6 | **Resume evaluation rides existing mutation paths** | Everything that frees storage or changes the balance (cargo loaded, building demolished, construction cancelled) already calls `RebaseRates`; those same commits evaluate halted buildings and append `BuildingResumed` atomically. No polling, no new trigger mechanism |
| D7 | **`SlotIndex` becomes a stable monotonic identifier, never a reusable position.** Cancelled/demolished buildings remain in the append-only `Buildings` list as tombstones; slot availability = count of non-tombstone entries < capacity | `StartConstruction` derives `SlotIndex = Buildings.Count`; freeing list positions would break that and let a stale `CompleteBuildingConstruction` land on a *different* building occupying a recycled index. Tombstones keep every in-flight message forever valid — it finds the tombstone and no-ops |
| D8 | **Cancel construction frees the slot immediately, no refund** (`BuildingConstructionCancelled`) | Per the game rules. The cancelled build's ingot draw disappears in the same commit, so D6's resume evaluation can un-halt a starved neighbor atomically |
| D9 | **Demolition is a scheduled two-step mirroring construction**: `BuildingDemolitionStarted` (immediate shutdown — zero draw, zero production, `RebaseRates`) → scheduled `CompleteBuildingDemolition` → `BuildingDemolished` (tombstone, slot freed). Duration is a `BuildingSpecs` placeholder. No cancel-of-demolition | Matches the plan ("stops functioning immediately, demolition takes time, slot freed on completion") using the one completion pattern the codebase already trusts. Cancelling a demolition is not in the plan — YAGNI |
| D10 | **Fleet recall is a single `FleetRecalled` event plus an ordinary arrival.** `POST /api/fleets/{fleetId}/cancel`, valid only `InTransit`, 409 if already returning. The event carries a fresh return `TravelPlan` (destination = origin, travel time = time already traveled); a new `CompleteFleetArrival` is scheduled and the original goes stale via the existing validate-on-arrival stamp | `architecture.md` §306-314 already prescribes this shape. The return arrival is a normal arrival — fleet ends `Stationed` at origin, cargo intact, colony ship unconsumed, per Phase 4's D6 ("arrival always leaves the fleet Stationed"). Collapses the plan's three events into one fact plus an event that already exists |
| D11 | **Ownership checks unify into one shared helper/endpoint filter** | Three ad-hoc implementations exist (`ShipEndpoints.IsOwner`, inline claim parsing in `BuildingEndpoints`, `FleetEndpoints.PlayerId`); Phase 5 adds several more mutation endpoints. Consolidate before multiplying the pattern |
| D12 | **Every error response becomes a real ProblemDetails** | `AddProblemDetails()` is registered but the concurrency handler writes a bare `{ detail }` anonymous object and validations use `BadRequest<string>`. One error shape across the surface; invalid `?status=` binding (#63 → 400) is fixed under this umbrella, and wave 0's registration-race 409 gets its shape unified here |
| D13 | **Scoring & leaderboard are descoped from Phase 5 into standalone issues [#67](https://github.com/VoidForgeOrg/VoidForge/issues/67) (lazy score) and [#68](https://github.com/VoidForgeOrg/VoidForge/issues/68) (async Leaderboard projection)** | Owner's call during this design session: the phase concentrates on engine hardening; scoring is additive read-side work with no dependency on the domain spine. The capstone e2e therefore verifies the full loop and API consistency without a score assertion — that assertion moves to #67 |
| D14 | **#44's unresolved remainder is real wave-1 work, sequenced before depletion**: enforce non-decreasing event `at` per stream structurally (clamp at append), move `Apply(PlanetColonized)` onto the guarded non-regressing checkpoint path, and re-derive the inversion-window bound for scheduled fleet arrivals. Rewind-and-reapply stays post-MVP | Kickoff review of #44 found the post-#47 analysis documents unresolved corruption-adjacent gaps, and the depletion issue's pool-drain checkpoint math sits directly on this invariant — hardening the foundation before building on it |

## 2. Storage caps & halting

New messages and events, all following ADR 0001:

```
CheckStorageFull(PlanetId, ResourceType, PredictedAt)   -- scheduled message
BuildingHalted(SlotIndex, Reason, At)                   -- domain event
BuildingResumed(SlotIndex, At)                          -- domain event
```

Flow:
1. Any commit that changes rates calls `RebaseRates`, then asks the planet for deadline predictions (D3) and schedules `CheckStorageFull` at each predicted time.
2. On arrival the handler runs validate-on-arrival: recompute the pool at `now`; if it is not actually at capacity (rates changed since prediction), no-op.
3. If full: append `BuildingHalted(reason: OutputStorageFull)` for each producer of that resource, `RebaseRates`, commit once. Halted producers stop filling the pool and drop to the 5% draw, which may resolve an energy overload in the same re-derivation (D5).
4. Resume (D6): commits that free storage (cargo loaded onto a fleet, consumer built/resumed) evaluate halted buildings; any whose output has headroom again gets `BuildingResumed` in that same commit, followed by `RebaseRates` and fresh deadline scheduling.

`InputStarved` works the same way from the consumer side: a refinery with zero ore inflow and empty ore storage halts (this is the "zero-ingot halting" `domain-model.md` defers to Phase 5); it resumes when input reappears.

## 3. Resource depletion

```
CheckPoolDepleted(PlanetId, PredictedAt)                -- scheduled message
PlanetResourceDepleted(ResourceType, At)                -- domain event
```

- Checkpoint math extends to drain `IronOrePool` at the current extraction rate between checkpoints (D4), non-regressing per #44's rules.
- Deadline = `remaining / extractionRate`, rescheduled on every rate change like storage checks.
- On confirmed depletion: `PlanetResourceDepleted`, then `BuildingHalted(reason: ResourceDepleted)` for every drill, `RebaseRates`, one commit. Refineries starve next via the normal `InputStarved` path once buffered ore runs out — that is the depletion cascade test.
- No resume path; `ResourceDepleted` halts are permanent (D4).

## 4. Cascading resolution & even-split proof

No new machinery (D5). The issue delivers integration tests for the four `engine.md` scenarios:

1. Ore depletion → drills halt → energy freed → overload resolves → productivity recovers
2. Ore storage empties → refinery halts → ingot production stops → shipyard starves
3. New building online → energy overload → productivity drops
4. Demolition → energy freed → overload resolves

Plus edge cases (simultaneous depletion + storage-full at the same instant, all buildings halted) and the even-split contention tests: two refineries sharing insufficient ore each run at reduced throughput; shipyard vs. building construction split ingots evenly. Even-split itself falls out of planet-level scalar pools — these tests are the proof, not new code.

## 5. Building cancellation & demolition — API surface

```
DELETE /api/planets/{planetId}/buildings/{slotIndex}/construction   -- cancel, 204
POST   /api/planets/{planetId}/buildings/{slotIndex}/demolish       -- start demolition, 202
```

Events: `BuildingConstructionCancelled`, `BuildingDemolitionStarted`, `BuildingDemolished`. Guards: ownership (D11), 404 unknown slot, 409 wrong state (cancelling a completed building, demolishing one already demolishing). Both paths run `RebaseRates` + resume evaluation + deadline rescheduling in their commit.

## 6. Fleet recall — API surface

```
POST /api/fleets/{fleetId}/cancel    -- 200 with updated fleet, 409 if not recallable
```

Event: `FleetRecalled` (new `TravelPlan`, new arrival stamp). The stale original `CompleteFleetArrival` no-ops via the existing stamp validation. Mission-specific effects simply never happen (no colonize claim, no cargo delivery); the fleet arrives `Stationed` at origin with cargo aboard.

Folded into this issue because they touch the same validation and test surface:
- [#60](https://github.com/VoidForgeOrg/VoidForge/issues/60) — colonize-in-place: relax the same-destination 400 for the Colonize mission
- [#58](https://github.com/VoidForgeOrg/VoidForge/issues/58) — deterministic rework of the flaky concurrent-disband test

## 7. API polish & capstone

- Shared ownership filter (D11), ProblemDetails everywhere (D12), OpenAPI review for the new Phase 5 endpoints, status-code sweep (400/401/403/404/409).
- Bug fixes under this umbrella: #63 (invalid `?status=` → 400); the wave-0 registration-race 409 (#45/#61) gets its error shape aligned with ProblemDetails here.
- Capstone e2e extends `FullLoopEndToEndTests`: register → build economy → let storage fill and halt → transport ore away → watch resume → cancel a build → recall a fleet → colonize → verify final state via the read API. No score assertion (D13).
- Frontend client regen (#64/#41) only if the OpenAPI churn makes it near-free; otherwise it stays parked.

## 8. Issue breakdown & sequencing

Spine-first, three waves. Tracked by epic [#75](https://github.com/VoidForgeOrg/VoidForge/issues/75); epic + issues carry the `phase:5-hardening` label; PRs target the `phase-5` integration branch per the established workflow.

| Wave | Issue | Contents | Depends on |
|---|---|---|---|
| 0 | [#62](https://github.com/VoidForgeOrg/VoidForge/issues/62) Shared test helpers | Relabeled into the phase — every later issue profits | — |
| 0 | [#45](https://github.com/VoidForgeOrg/VoidForge/issues/45) + [#46](https://github.com/VoidForgeOrg/VoidForge/issues/46) Startup & registration races | Dup-name 409 (#61 closed as duplicate of #45) + seeder double-seed; [#40](https://github.com/VoidForgeOrg/VoidForge/issues/40) verify-and-close (Planet split shipped in Phase 4 — finish the class-size audit) | — |
| 1 | [#69](https://github.com/VoidForgeOrg/VoidForge/issues/69) Storage caps & halting | §2 (D1–D3, D6) | wave 0 helpers |
| 1 | [#44](https://github.com/VoidForgeOrg/VoidForge/issues/44) Event-ordering invariant | Remainder (D14): clamp `at` at append, guard `PlanetColonized` checkpoint, re-derive arrival inversion bound | — |
| 1 | [#70](https://github.com/VoidForgeOrg/VoidForge/issues/70) Resource depletion | §3 (D4) | halting (#69); ordering invariant (#44) |
| 1 | [#71](https://github.com/VoidForgeOrg/VoidForge/issues/71) Cascading scenarios & even-split proof | §4 (D5) | #69, #70 |
| 2 | [#72](https://github.com/VoidForgeOrg/VoidForge/issues/72) Building cancellation & demolition | §5 (D7–D9) | halting (#69, cascade hooks) |
| 2 | [#73](https://github.com/VoidForgeOrg/VoidForge/issues/73) Fleet recall (+ #60, #58) | §6 (D10) | — (parallel to spine) |
| 3 | [#74](https://github.com/VoidForgeOrg/VoidForge/issues/74) API polish & capstone e2e | §7 (D11–D12) + #63 | all previous |

Descoped: **Scoring & leaderboard** → standalone issues [#67](https://github.com/VoidForgeOrg/VoidForge/issues/67) and [#68](https://github.com/VoidForgeOrg/VoidForge/issues/68) (D13).

## 9. Docs to update alongside implementation

- `technical-design/domain-model.md` — `Halted` status, new events, tombstone slot model
- `technical-design/architecture.md` — mark §306-314 (recall) implemented; cascade section becomes descriptive rather than prescriptive
- `technical-design/api-conventions.md` — ProblemDetails as the single error shape
- `game-design/fleets.md` / `buildings.md` — recall and demolition player-facing rules if wording drifts

## 10. Testing notes

- Deterministic time via the injected `TimeProvider` throughout; no real waits for scheduled checks
- The quality-gate Stop hook auto-runs the suite — avoid concurrent manual `dotnet test` runs (shared test DB)
- Wave 0's helper extraction (#62) is the enabler for the volume of integration tests in waves 1–3
