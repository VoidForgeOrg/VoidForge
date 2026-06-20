# Phase 3 — Production Chain

**Goal:** The full production chain works: generators produce energy, buildings consume it, refineries convert ore into ingots, shipyards build ships. Construction costs resources over time. The economy ticks.

```
ore extracted → refined into ingots → consumed by construction & shipyard → ships produced
                              ↑ energy underpins all operational buildings ↑
```

**Tracking:** Epic [#28](https://github.com/VoidForgeOrg/VoidForge/issues/28) · Issues [#24](https://github.com/VoidForgeOrg/VoidForge/issues/24), [#25](https://github.com/VoidForgeOrg/VoidForge/issues/25), [#26](https://github.com/VoidForgeOrg/VoidForge/issues/26), [#27](https://github.com/VoidForgeOrg/VoidForge/issues/27)

> Dependency order: **#24 → #25 → #26 → #27** (energy is the foundation; each builds on the previous).

## Decisions (settled during refinement)

- **Energy overload throttles operational buildings only** (Drill extraction, Refinery conversion). Construction and ship-building consume ingots — not energy — so they slow only *indirectly* via reduced ingot supply.
- **Scalar `IronOre` / `IronIngot` pools retained** — no `Dictionary<ResourceType, ResourcePool>` refactor in MVP. `Planet.IronIngot` already exists from Phase 2.
- **Ship roster** (`ShipType` enum + minimal completed-ship list on `Planet`) is introduced in #27; full fleet assembly/travel/missions remain **Phase 4**.
- **Homeworld starting buildings** stay placed directly as `Operational` (bypass construction) so new players' homeworlds are immediately functional.

## ✅ Decided — completion via durable scheduled messages ([ADR 0001](../technical-design/adr/0001-completion-event-resolution.md), accepted 2026-06-20)

Construction/ship completion (and later, depletion) is resolved with **durable Wolverine scheduled messages** (Option B): schedule `BuildingCompleted` / `ShipCompleted` at the completion time; the handler checkpoints at that time, appends the real transition events, recomputes energy/rates, and reschedules downstream deadlines. Stale messages are handled by "schedule optimistically, validate on arrival." This keeps the event stream truthful (async Leaderboard projection + post-MVP push react to real events) and reuses the machinery Phase 4 sagas / Phase 5 cascades need anyway.

The infra is already wired — `DurabilityMode.Solo` is **not** in-memory; with `IntegrateWithWolverine()` + `AutoApplyTransactions()` (both in `Program.cs`) scheduled messages persist to Postgres and survive restarts. No durability change, no new infra. Two implementation consequences for #26: introduce an injectable clock (**`TimeProvider`** — none today, code reads `DateTimeOffset.UtcNow`) and keep completion/cascade logic in **pure aggregate methods** that thin, idempotent handlers call.

---

## Issues

### #24 — Energy grid
**Labels:** `phase:3-production-chain`, `domain:buildings`, `domain:resources`

Generators produce energy (MW); operational buildings consume it. Overload degrades productivity. Foundation of the phase.

**Scope:**
- `BuildingSpecs.EnergyOutputMw(type)` (Generator) and `EnergyDrawMw(type)` (Drill/Refinery/Shipyard). Values TBD.
- Energy is a **flow** resource — computed from operational building composition, **not** a `ResourcePool`.
- `generation = Σ` operational Generator output; `consumption = Σ` operational consumer draw.
- Productivity multiplier `m = generation >= consumption ? 1 : generation / consumption`.
- `m` scales **operational** building output rates (Drill, and Refinery in #25); the stored pool `Rate` bakes in `m` so lazy calc stays linear. Construction/ship-build consumption is **not** scaled by `m`.
- Recompute `m` and re-checkpoint affected pools at the change instant whenever building composition/status changes.
- `PlanetResponse` surfaces generation, consumption, multiplier.
- Homeworld generator output covers starting Drill + Refinery with headroom.
- Integration test: add buildings until overloaded → rates drop proportionally; add a generator → recovers.

**Depends on:** #10 (buildings — ✅ closed). No intra-phase dependency.

---

### #25 — Refinery: ore → ingots
**Labels:** `phase:3-production-chain`, `domain:buildings`, `domain:resources`

Refineries consume Iron Ore and produce Iron Ingots at a 1:2 ratio.

**Scope:**
- `BuildingSpecs.RefineryOreConsumptionPerSecond` (TBD); ingot output = `2×` consumption.
- On becoming operational: checkpoint `IronOre` + `IronIngot` (pool already exists), apply `−consumption` to ore rate and `+2×consumption` to ingot rate. Additive across refineries (mirrors drill wiring).
- Energy productivity multiplier (#24) scales throughput.
- Even-split distribution (steady-state) when refinery demand exceeds ore supply; the dynamic clamp at **zero ore** (halt at 5% energy) is **Phase 5**.
- Homeworld Refinery becomes functional.
- Integration test: place refinery → ore decreasing, ingots increasing at 2× the ore consumption rate.

**Depends on:** #24 (energy affects rates), #10 (buildings — ✅ closed).

---

### #26 — Building construction with resource cost
**Labels:** `phase:3-production-chain`, `domain:buildings`, `domain:resources`

Buildings are no longer instant — they cost Iron Ingots consumed over time.

**Scope:**
- Add `BuildingStatus.UnderConstruction`.
- `BuildingSpecs`: `IngotCost(type)`, `BuildDuration(type)`; consumption rate = `cost / duration`. TBD.
- Placement: slot taken immediately, status `UnderConstruction`; checkpoint `IronIngot`, add `−consumption` rate. No energy draw, no production rate yet.
- Completion (scheduled `BuildingCompleted` message — ADR 0001): at the scheduled time, status → `Operational`; checkpoint, remove construction rate, begin energy draw + production rate, recompute energy multiplier (#24). Handler is thin/idempotent; logic lives in pure aggregate methods.
- Halting if ingots run out, resume when available (recompute completion time); happy path here, zero-ingot detail coordinates with Phase 5.
- `POST /api/planets/{planetId}/buildings` returns `UnderConstruction`; `PlanetResponse`/`BuildingSlotResponse` surface status (+ optional ETA).
- Events: `BuildingConstructionStarted`, `BuildingCompleted` (decide whether to rename `BuildingPlaced` — note event-stream migration).
- Homeworld starting buildings stay `Operational` (bypass construction).
- Integration test: start building → ingot consumption → advance time → becomes operational, drain stops, effects switch on.

**Depends on:** #25 (ingots must be produced to fund construction), #24 (energy on completion).

---

### #27 — Shipyard & ship construction
**Labels:** `phase:3-production-chain`, `domain:buildings`, `domain:fleets`

Shipyards build ships from ingots.

**Scope:**
- `ShipType` enum: `ColonyShip`, `CargoVessel`.
- Per-ship `IngotCost` + `BuildDuration` (`BuildingSpecs` or new `ShipSpecs`); TBD.
- Ship construction consumes ingots continuously — same model as #26 (scheduled `ShipCompleted` message — ADR 0001).
- Up to 3 parallel builds per shipyard, unlimited queue, queued ships auto-start as slots free.
- Completed ships appended to a minimal planet ship roster (`RosterShip(Id, Type)`); fleet assembly is Phase 4.
- Shipyard energy: full draw when ≥1 build active, **5% when idle** (first appearance of the 5% rule Phase 5 generalizes); ships add no extra draw.
- `POST /api/planets/{planetId}/shipyards/{slotIndex}/queue` — queue a ship build.
- Events: `ShipConstructionQueued`, `ShipConstructionStarted`, `ShipCompleted`.
- Integration test: queue >3 ships → 3 parallel, rest queued → ingot consumption → ships complete in order and appear on roster.

**Depends on:** #26 (construction model), #24 (energy).

---

## Phase Completion

- Energy grid works: generators power buildings, overload reduces productivity.
- Refineries convert ore → ingots at 1:2 ratio.
- Building construction consumes ingots over time and completes on schedule.
- Shipyards build ships (Colony Ship, Cargo Vessel) and add them to the roster.
- The full chain is visible: ore extracted → refined into ingots → consumed by construction/shipyard → ships produced.
