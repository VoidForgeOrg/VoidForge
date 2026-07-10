# Phase 3 — Production Chain: Design Spec

**Date:** 2026-07-10
**Scope:** Epic [#28](https://github.com/VoidForgeOrg/VoidForge/issues/28) — issues [#24](https://github.com/VoidForgeOrg/VoidForge/issues/24) (energy grid), [#25](https://github.com/VoidForgeOrg/VoidForge/issues/25) (refinery), [#26](https://github.com/VoidForgeOrg/VoidForge/issues/26) (building construction), [#27](https://github.com/VoidForgeOrg/VoidForge/issues/27) (shipyard), plus the parts of [#29](https://github.com/VoidForgeOrg/VoidForge/issues/29) (pagination) that #27 requires.
**Builds on:** [ADR 0001](../../../technical-design/adr/0001-completion-event-resolution.md) (durable scheduled messages, accepted 2026-06-20), `plans/phase-3-production-chain.md`, the Phase 2 domain (`Planet` aggregate, lazy-calc `ResourcePool`).

## 1. Decisions made in this design

Decisions already fixed by the issues/ADR (scalar pools, no combat, no refunds, homeworld bypass, checkpoint-at-scheduled-time, etc.) are not repeated. New decisions:

| # | Decision | Rationale |
|---|---|---|
| D1 | **Whole-phase design, per-issue PRs** (sequencing in §10) | Mechanics interlock (energy ↔ rates ↔ completion); PRs stay reviewable |
| D2 | **`TimeProvider` lands first (PR 0)**, not with #26 as ADR 0001 suggested | #24 already needs re-checkpoint-at-change-instant tests; small mechanical refactor unblocks the whole phase |
| D3 | **Ship-build cancel in Phase 3; building-construction cancel deferred to Phase 5** (demolition) | Ship cancel is natural with a queue and is the only real exerciser of the validate-on-arrival guard; building cancel adds scope without new coverage |
| D4 | **Keep `BuildingPlaced` as "placed directly Operational"; add `BuildingConstructionStarted` / `BuildingCompleted`** | Zero event-stream migration; each event states one fact; homeworld seeding keeps its exact current semantics |
| D5 | **Planet-level ship queue with fungible bays** — builds are not assigned to a specific shipyard; capacity = `3 × operational shipyards` | Simpler API (no `slotIndex` routing), simpler player model, auto-scaling throughput, energy falls out arithmetically. **Revises #27's "per-shipyard, not per-planet"** — updating the issue + phase plan is part of PR 5 |
| D6 | **Enqueue is unconditional** — a ship may be queued with zero operational shipyards; it starts when capacity exists | Simplest rule; queued ships consume nothing until started |
| D7 | **Command/event naming split** — scheduled messages are commands (`CompleteBuildingConstruction`, `CompleteShipConstruction`); stream events record outcomes (`BuildingCompleted`, `ShipCompleted`) | Messages ask; events record. Keeps the stream truthful when a command no-ops |
| D8 | **Refinery steady-state clamp:** effective refinery consumption = `min(refinery demand, drill inflow)`, both ×`m` | Even-split falls out (pools are planet scalars); ore rate never negative in Phase 3, avoiding ingots-from-nothing when the ore pool floors at zero |
| D9 | **`RosterShip` carries `CompletedAt`** (deviation from #27's minimal `(Id, Type)`) | Gives the paginated roster a meaningful stable sort (completion order) instead of Guid order |
| D10 | **Balance values are config-backed statics** — `BuildingSpecs`/`ShipSpecs` stay static but load values once at startup from a `Balance` config section | Marten `Apply` can't use DI and `RebaseRates` needs whole-composition specs; config-backing enables short-duration end-to-end tests and recompile-free balance tuning |
| D11 | **Wholesale `RebaseRates` replaces Phase 2's incremental rate wiring** | `m` rescales every operational consumer when any building changes; incremental deltas would need to un-apply old `m` |

## 2. Domain design

### 2.1 Energy grid (#24)

Energy is a **flow** — derived, never stored, no events, no checkpoints. `Planet` gains computed properties:

```
EnergyGenerationMw     = Σ EnergyOutputMw over operational Generators
EnergyConsumptionMw    = Σ EnergyDrawMw over operational consumers
                         (Shipyard: state-dependent, see §2.5)
ProductivityMultiplier m = consumption == 0 ? 1 : min(1, generation / consumption)
```

`m ∈ (0, 1]`. Construction and ship-build ingot drains are **not** scaled by `m` (per the epic — they consume ingots, not energy).

### 2.2 The rate-rebase mechanism (replaces incremental wiring)

Pool rates become a pure function of building composition, recomputed wholesale at every change instant:

```csharp
private void RebaseRates(DateTimeOffset at)
{
    // 1. Checkpoint both pools at `at` — accrual before the change locks in under old rates.
    // 2. Recompute m from current composition.
    // 3. Set rates from scratch:
    //    oreRate   = drillExtraction×m − effectiveRefineryConsumption
    //    ingotRate = 2 × effectiveRefineryConsumption
    //                − Σ building-construction drains − Σ active ship-build drains
}
```

Every `Apply` that changes building/ship-build composition ends with `RebaseRates(event timestamp)`: `BuildingPlaced`, `BuildingConstructionStarted`, `BuildingCompleted`, `ShipConstructionStarted`, `ShipCompleted`, `ShipConstructionCancelled`. Lazy `GetCurrentValue` stays plain linear accrual between checkpoints — `m` is baked into `Rate`.

The Phase 2 incremental wiring in `Apply(BuildingPlaced)` (`Rate = Rate + extractionRate`) is removed in favor of `RebaseRates` — a small targeted refactor in PR 1.

### 2.3 Refinery semantics (#25)

```
refineryDemand       = Σ RefineryOreConsumptionPerSecond × m   (operational refineries)
oreInflow            = Σ drill extraction × m                  (operational drills)
effectiveConsumption = min(refineryDemand, oreInflow)
ingotProduction      = 2 × effectiveConsumption                 (1:2 ratio, derived — never configured independently)
```

- **Even-split needs no code:** pools are planet-level scalars; per-refinery attribution (`effective / n`) is display-only, post-MVP.
- **Refineries do not drain the stored ore buffer.** Consequence (documented): a planet with stored ore but no operational drill has idle refineries. Buffer-draining, depletion detection, and halt-at-5%-energy are Phase 5.
- The 1:2 ratio lives in one place: `RefineryIngotOutputFactor = 2`.

### 2.4 Building construction (#26)

**State:**

```csharp
public enum BuildingStatus { Operational, UnderConstruction }   // Halted arrives Phase 5

public sealed record BuildingSlot(
    BuildingType Type,
    BuildingStatus Status,
    DateTimeOffset? CompletesAt);   // fixed at start (happy path); null for operational slots
```

Events gain a `SlotIndex` (position in the `Buildings` list) so completion can address its slot.

**Start flow** — `POST /api/planets/{planetId}/buildings` (same endpoint, new behavior):

1. `Planet.StartConstruction(type, now)` — pure; validates a free slot; returns `BuildingConstructionStarted(SlotIndex, Type, StartedAt, CompletesAt)` with `CompletesAt = now + BuildDuration(type)`.
2. `Apply`: slot added as `UnderConstruction`; `RebaseRates(now)` — ingot drain `−IngotCost/BuildDuration` begins. No energy draw, no production during construction.
3. Endpoint schedules `CompleteBuildingConstruction(PlanetId, SlotIndex, CompletesAt)` at `CompletesAt` via `IMessageBus` — **same transaction** as the event append (transactional outbox, already wired).

No upfront ingot check, no reservation. **Phase 3 simplification (documented):** if the ingot pool floors at zero, it clamps there and construction still completes on schedule. Halting/resume with completion recompute is the Phase 5 cascade.

**Completion flow** — the first Wolverine message handler; the template for Phase 4/5:

```csharp
public static async Task Handle(
    CompleteBuildingConstruction cmd, IDocumentSession session, IMessageBus bus)
{
    var planet = await session.Events.AggregateStreamAsync<Planet>(cmd.PlanetId);
    var events = planet?.CompleteBuilding(cmd.SlotIndex, cmd.CompletesAt) ?? [];  // pure; empty = stale
    if (events.Count > 0)
        session.Events.Append(cmd.PlanetId, events.ToArray());
    foreach (var started in events.OfType<ShipConstructionStarted>())             // Shipyard completed →
        await bus.ScheduleAsync(/* CompleteShipConstruction */, started.CompletesAt); // queued builds start
}
```

**Pure methods return event lists; callers append and schedule.** `Apply` methods only fold events into state — they never append events or schedule messages. So any transition that can start queued ship builds (`CompleteBuilding` for a Shipyard, `CompleteShipBuild`, `CancelShipBuild`) returns the transition event *plus* any resulting `ShipConstructionStarted` events; the calling endpoint/handler appends them all and schedules a `CompleteShipConstruction` for each started build.

- **Validate on arrival:** `CompleteBuilding` returns an empty list unless the slot is `UnderConstruction` **and** its `CompletesAt` equals the command's. Stale/superseded messages no-op. (Near-unreachable for buildings in Phase 3 since building-cancel is deferred — but it is the reusable guard pattern, and ship cancel exercises it for real.)
- **Checkpoint at the scheduled time:** the `BuildingCompleted(SlotIndex, CompletedAt)` event carries `CompletedAt = cmd.CompletesAt`; `Apply` flips the slot to `Operational` and calls `RebaseRates(CompletedAt)` — drain stops, energy draw + production begin, `m` recomputed. Values stay exact despite ~5 s scheduler-polling latency.

**Homeworld seeding is unchanged:** `BuildingPlaced` keeps its current meaning — placed directly `Operational` — and remains the seeding event (D4).

**Building-construction cancel is deferred to Phase 5** (D3). Stub note goes in `technical-design/domain-model.md`; the stale guard exists from day one regardless.

### 2.5 Planet ship queue, fungible bays & roster (#27)

**State (on `Planet`):**

```csharp
public enum ShipType { ColonyShip, CargoVessel }
public enum ShipBuildStatus { Queued, Active }

public sealed record ShipBuild(
    Guid Id, ShipType Type, ShipBuildStatus Status,
    DateTimeOffset QueuedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletesAt);

public sealed record RosterShip(Guid Id, ShipType Type, DateTimeOffset CompletedAt);

IList<ShipBuild> ShipQueue;      // Queued + Active builds
IList<RosterShip> Ships;         // completed roster
```

**Capacity model (D5):** builds are never assigned to a shipyard. `ShipyardParallelBuilds = 3` is a config-backed balance constant like everything in §6.

```
capacity        = ShipyardParallelBuilds × (operational Shipyards)
activeShipyards = min(shipyardCount, ceil(activeBuilds / 3))
shipyardDraw    = activeShipyards × EnergyDrawMw(Shipyard)
                + (shipyardCount − activeShipyards) × 5% × EnergyDrawMw(Shipyard)
```

The `ceil` rule implicitly concentrates work into as few shipyards as possible — the energy-optimal reading — with zero bookkeeping. Ships under construction add no draw of their own.

**Mechanics** (pure methods on `Planet`, mirroring #26):

- **Enqueue** (`POST /api/planets/{planetId}/ship-queue`): always accepted (D6). Emits `ShipConstructionQueued(BuildId, Type, QueuedAt)`; if `activeBuilds < capacity`, also `ShipConstructionStarted(BuildId, StartedAt, CompletesAt)` — uniform history: every ship is queued-then-started. The endpoint schedules `CompleteShipConstruction(PlanetId, BuildId, CompletesAt)` for any build that started.
- **Auto-start:** any transition that frees or adds capacity starts the next `Queued` build(s) FIFO by `QueuedAt`: ship completion, ship cancel (of an active build), and a Shipyard finishing construction. Per §2.4, the pure method returns the resulting `ShipConstructionStarted` events alongside the transition event; the calling endpoint/handler appends them and schedules the completion command for each newly started build.
- **Completion handler:** same shape as §2.4 — `Planet.CompleteShipBuild(buildId, at)` returns an empty list unless the build is still `Active` with matching `CompletesAt` (stale no-op). On success it returns `ShipCompleted(BuildId, CompletedAt)` (→ roster, build removed) plus `ShipConstructionStarted` for the next queued build if any.
- **Cancel** (`DELETE /api/planets/{planetId}/ship-queue/{buildId}`): queued → removed; active → removed and the next queued build auto-starts. No refund. Emits `ShipConstructionCancelled(BuildId, CancelledAt)`. The cancelled build's already-scheduled completion command later fires and no-ops via the stale guard — **the real Phase 3 test of validate-on-arrival**.

Every one of these `Apply`s ends with `RebaseRates(at)`: active ship builds drain `IngotCost/BuildDuration` each (not ×`m`), and the shipyard idle/active draw shifts `m`.

## 3. Events & messages catalog

**Stream events (new):**

| Event | Payload | Appended by |
|---|---|---|
| `BuildingConstructionStarted` | `SlotIndex, Type, StartedAt, CompletesAt` | Place endpoint |
| `BuildingCompleted` | `SlotIndex, CompletedAt` | Completion handler |
| `ShipConstructionQueued` | `BuildId, Type, QueuedAt` | Enqueue endpoint |
| `ShipConstructionStarted` | `BuildId, StartedAt, CompletesAt` | Enqueue/cancel endpoints, completion handlers |
| `ShipCompleted` | `BuildId, CompletedAt` | Completion handler |
| `ShipConstructionCancelled` | `BuildId, CancelledAt` | Cancel endpoint |

`BuildingPlaced` is unchanged (homeworld seeding only). Existing streams need no migration (D4).

**Scheduled command messages (new, durable via outbox):**

| Command | Scheduled at | Scheduled by |
|---|---|---|
| `CompleteBuildingConstruction(PlanetId, SlotIndex, CompletesAt)` | `CompletesAt` | Place endpoint |
| `CompleteShipConstruction(PlanetId, BuildId, CompletesAt)` | `CompletesAt` | Whichever append started the build |

Handlers are thin, idempotent, stale-aware; all domain logic lives in pure aggregate methods (ADR 0001).

## 4. API surface

| Change | Endpoint |
|---|---|
| Energy block `EnergyResponse { generationMw, consumptionMw, productivityMultiplier }` | `GET /api/planets/{id}` |
| `BuildingSlotResponse` gains `status`, lazy `etaCompletionUtc`, `progress` (0–1) | `GET /api/planets/{id}` |
| Bounded ship summaries: `shipCount`, `activeBuilds`, `queueLength` (counts only — no inline lists) | `GET /api/planets/{id}` |
| Returns slot in `UnderConstruction` | `POST /api/planets/{id}/buildings` |
| **New:** enqueue ship `{ shipType }` — always accepted; returns the created build (`id`, `status`, `etaCompletionUtc` if started) | `POST /api/planets/{id}/ship-queue` |
| **New:** cancel active or queued build, no refund | `DELETE /api/planets/{id}/ship-queue/{buildId}` |
| **New:** paginated queue — active builds first (with ETA/progress), then queued FIFO | `GET /api/planets/{id}/ship-queue` |
| **New:** paginated roster, `type` filter, sorted `(completedAt, id)` | `GET /api/planets/{id}/ships` |
| **Retrofit (#29):** paginated envelope, ordered by `Name` — breaking; regenerate frontend client | `GET /api/solar-systems` |

Auth follows existing conventions: mutations require planet ownership (403 otherwise); reads are universe-visible (full visibility).

## 5. Pagination infrastructure (#29 subset)

`src/Voidforge.Api/Pagination/` (one public type per file, MA0048):

- `PaginationParameters` — `page` default 1, `pageSize` default 50 clamped at 200; `page < 1` or `pageSize < 1` → 400.
- `PagedResponse<T>` — `items, page, pageSize, totalItems, totalPages, hasPrevious, hasNext`.
- **Two producer paths:**
  1. `IQueryable<T>.ToPagedResponseAsync(...)` wrapping Marten `ToPagedListAsync` — document queries (solar systems; future #30 endpoints).
  2. In-memory `IReadOnlyList<T>.ToPagedResponse(...)` — aggregate child collections (ship roster/queue live inside the `Planet` document). Same envelope; flagged as the keyset-migration candidate if rosters grow large.
- Convention documented in `technical-design/api-conventions.md` (new file, PR 3) with the definition-of-done note for future collection endpoints.

The rest of #29/#30 (new list endpoints, summary DTOs, leaderboard) stays outside this phase.

## 6. Balance placeholders (config-backed — D10)

Defaults live in code; a `Balance` configuration section overrides them, loaded **once** in `Program.cs` via `BuildingSpecs.Configure(...)` / `ShipSpecs.Configure(...)`. All values are balancing placeholders (TBD per CLAUDE.md).

| Building | Energy | Ingot cost / duration → drain |
|---|---|---|
| Generator | +100 MW output | 240 / 60 s → 4/s |
| Drill | 20 MW draw | 300 / 60 s → 5/s |
| Refinery | 30 MW draw | 450 / 90 s → 5/s |
| Shipyard | 40 MW draw (idle: 5% = 2 MW) | 600 / 120 s → 5/s |

Drill extraction: 10 ore/s (existing). Refinery: 5 ore/s → 10 ingots/s.

| Ship | Ingot cost / duration → drain |
|---|---|
| ColonyShip | 1000 / 300 s → ~3.33/s |
| CargoVessel | 400 / 120 s → ~3.33/s |

`ShipyardParallelBuilds = 3` (parallel bays per operational Shipyard).

Sanity: homeworld (Generator + Drill + Refinery) draws 50 MW of 100 MW — headroom ✓; adding a Shipyard + second Drill (110 MW) produces the first overload. Drill inflow 10/s ≥ refinery demand 5/s — no clamp on the homeworld; a third refinery triggers the steady-state split.

## 7. Error handling

- Existing conventions carry over: 404 unknown planet, 403 non-owner mutation, 409 no free building slot.
- New: 404 unknown `buildId` on cancel; 400 invalid pagination params or unknown `shipType` (binding-level).
- Completion handlers never produce HTTP errors — their failure mode is the stale no-op.
- **Out of scope (pre-existing):** optimistic concurrency on stream appends (same gap as the registration race noted in `PlayerEndpoints.cs`). Phase 3 does not widen it; fixing it is its own issue.

## 8. Testing strategy

Layered per ADR 0001 — logic in pure methods, so most coverage needs no clock and no scheduler:

1. **Unit (bulk):** pure aggregate methods with explicit timestamps — multiplier trio (`gen>con`, `==`, `<`), `RebaseRates` checkpoint-first, steady-state clamp, `StartConstruction`/`CompleteBuilding` incl. stale no-op, queue FIFO/capacity/auto-start, `ceil` energy rule.
2. **Integration, handler-invoked:** call completion handlers directly with a crafted command — verifies handler → append → projection wiring without waiting for delivery.
3. **Scheduling persistence:** assert the scheduled envelope exists in Wolverine's Postgres envelope table with the correct execution time.
4. **One end-to-end test:** short-duration build (1–2 s via test `Balance` config), poll until `Operational` (~10 s budget; scheduler polls every ~5 s).

`TimeProvider` (PR 0) replaces every `DateTimeOffset.UtcNow` in endpoints; tests use `FakeTimeProvider` where the domain timestamp matters. Wolverine's scheduler runs on real time — hence the short-duration approach for (4).

## 9. PR breakdown & sequencing

| PR | Content | Merge gate (tests) |
|---|---|---|
| 0 | `TimeProvider` injection everywhere | Existing suite green on injected clock |
| 1 | #24 energy grid: specs, computed properties, `RebaseRates`, energy block in `PlanetResponse` | Multiplier units; integration: overload drops drill rate by exactly `gen/con`, generator restores |
| 2 | #25 refinery: consumption spec, steady-state clamp, homeworld refinery functional | Wiring units (−r/+2r, additive, ×m, clamp); integration: homeworld ore +5/s net, ingots +10/s |
| 3 | #29 subset: `Pagination/`, solar-systems retrofit, `api-conventions.md` | Envelope/validation units; integration: deterministic order, 400s |
| 4 | #26 construction: `UnderConstruction`, start/complete flow, first handler, config-backed specs | Pure-method units + stale guard; place→drain integration; envelope-persistence; end-to-end short-duration |
| 5 | #27 shipyard: queue/roster, cancel, paginated endpoints; update issue #27 + phase plan for D5 | Queue/capacity/energy units; integration: >3 queued → 3 active, cancel → auto-start → stale no-op, pagination |

Dependency chain: PR 0 → 1 → 2 → 4 → 5, with PR 3 independent but merged before PR 5. Each PR updates `technical-design/` (`domain-model.md`, `architecture.md` API list; `api-conventions.md` in PR 3).

## 10. Out of scope / Phase 5 coordination notes

- Building-construction cancel (with demolition), `Halted` status, depletion detection, storage-full events, buffer-draining refineries, halt-at-5%-energy cascades — Phase 5. Stub notes in `domain-model.md`.
- Zero-ingot behavior: pool clamps at 0, construction completes on schedule (documented simplification, revisited by Phase 5 halting).
- Fleet assembly, travel, missions — Phase 4 (roster is the only fleet-adjacent state introduced here).
- #30 read endpoints (summary DTOs, leaderboard, empire views) — separate work; the energy block and ship counts added here are designed to slot into #30's summary DTO later.
