# Phase 4 — Fleets & Expansion: Design Spec

**Date:** 2026-07-26
**Scope:** Fleet assembly, travel, and the three MVP missions (Move, Transport, Colonize), plus per-planet coordinates. Tracked by epic [#52](https://github.com/VoidForgeOrg/VoidForge/issues/52) — issues [#48](https://github.com/VoidForgeOrg/VoidForge/issues/48) (assembly), [#49](https://github.com/VoidForgeOrg/VoidForge/issues/49) (coordinates + travel + Move), [#50](https://github.com/VoidForgeOrg/VoidForge/issues/50) (cargo + Transport), [#51](https://github.com/VoidForgeOrg/VoidForge/issues/51) (Colonize), plus prep [#40](https://github.com/VoidForgeOrg/VoidForge/issues/40).
**Builds on:** [ADR 0001](../technical-design/adr/0001-completion-event-resolution.md) (durable scheduled messages), Phase 3's `Planet` aggregate (ship roster, `ResourcePool`, `RebaseRates`), [#39](https://github.com/VoidForgeOrg/VoidForge/issues/39) (optimistic concurrency + retry), [#44](https://github.com/VoidForgeOrg/VoidForge/issues/44) (non-regressing checkpoints).
**Supersedes:** `technical-design/architecture.md` §4's `FleetMission : Saga` sketch (see D2).

## 1. Decisions made in this design

| # | Decision | Rationale |
|---|---|---|
| D1 | **`Fleet` is a new event-sourced aggregate** with its own Marten stream and inline snapshot | Fleets outlive any single mission (stationed, idling after a failed colonize, holding undeliverable cargo). Event-sourcing keeps Phase 5 scoring of in-transit assets on the same read path as everything else |
| D2 | **Arrival is a durable scheduled message, not a Wolverine Saga** — supersedes `architecture.md` §4 | ADR 0001's schedule → validate-on-arrival → pure-aggregate-method pattern is already proven twice (#26, #27). §4 predates the ADR. A saga would need a second store for stationed fleets anyway, and would add a third persistence pattern alongside aggregates and documents |
| D3 | **Travel goes through an `ITravelPlanner` seam; the resulting `TravelPlan` is recorded on `FleetDeparted`** | Warp lanes and jump gates are planned post-MVP and will change travel drastically. A planner interface plus a plan-on-the-event means the rule can be replaced without touching the aggregate, the handler, or in-flight fleets |
| D4 | **`TravelPlan` carries a `Legs` list, single-element in MVP** | Multi-leg routing (lanes, gates) becomes "schedule the next leg on leg completion" — additive, with no reshaping of an event already in the stream |
| D5 | **Planets get their own `X`/`Y`/`Z`**, seeded as a spread around their system's center | `planets.md` already states planets are positioned within systems; measuring distance system-to-system would make intra-system travel a special case forever. No production data exists — dev worlds reseed |
| D6 | **Arrival always leaves the fleet `Stationed`; ships reach the roster only via explicit disband** | `fleets.md` says arriving ships join the roster, but partial cargo must stay bound to ships — the two rules collide. Always-stationed makes every mission take one path and removes the conditional "does my fleet still exist?" that depends on far-end storage headroom. `fleets.md` is updated to match |
| D7 | **Cargo loads at assembly, not at departure** | A stationed fleet holding cargo is a required state regardless (partial unload produces exactly that), so loading early keeps departure a pure state transition and avoids re-checking storage at launch |
| D8 | **Cargo is tracked as fleet-level totals**, capacity = Σ Cargo Vessel capacity | Per-ship binding only matters for combat (post-MVP, `fleets.md` design intent). Disband is refused while cargo remains, so ships can never leave a fleet carrying an unaccounted share |
| D9 | **One `CargoUnloaded` event carrying the accepted amounts** — no separate `PartialCargoUnloaded` | Partial delivery is the same fact with smaller numbers; the remainder is derivable from fleet state. Mirrors D4's "each event states one fact" from Phase 3 |
| D10 | **Colonization claims via `FetchForWriting` + null-owner assertion**, reusing #39's retry | The claim race and registration's homeworld race (#19) are the same race. One guarded claim path serves both; the loser re-reads an owned planet and resolves to `ColonizationFailed` |
| D11 | **Disband is refused (409) while cargo remains** | The alternative is destroying resources. Unload first; at an unowned planet, that means the fleet must already be empty |
| D12 | **#40 (Planet partial-class split) is the phase's prep task** | Every feature below adds roster or storage behavior to a 345-line `Planet.cs` |
| D13 | **`RosterShip` gains an `OwnerId`; assembly validates ship ownership, not planet ownership** | `fleets.md` lets ships sit on an unowned planet's roster indefinitely, and D11 lets a fleet disband there — without an owner on the ship, those ships would be unreachable forever (assembly would demand a planet the player cannot own). No event migration: `Apply(ShipCompleted)` reads the planet's current `OwnerId`, which is correct for every existing stream because planets never change hands in MVP |

## 2. Domain design

### 2.1 The `Fleet` aggregate

```
Fleet
  Id, OwnerId
  Status            : Stationed | InTransit
  LocationPlanetId  : Guid?   -- set when Stationed, null in transit
  Ships             : IList<FleetShip>
  CargoIronOre, CargoIronIngot : decimal
  -- in-transit block, null when Stationed:
  OriginPlanetId, DestinationPlanetId, Mission, DepartedAt, ArrivesAt, TravelPlan
```

`FleetShip(Id, Type, CompletedAt)` mirrors `RosterShip` so ships round-trip through a fleet without losing the roster's stable sort key.

Registered in `Program.cs` as `opts.Projections.Snapshot<Fleet>(SnapshotLifecycle.Inline)`, following `Planet` and `Player`.

Derived, not stored (methods, to stay out of the snapshot document):
- `GetSpeed()` — the minimum `SpeedPerSecond` across `Ships` (slowest ship dictates fleet speed).
- `GetCargoCapacity()` — Σ `CargoCapacity` over `Ships`.
- `GetCargoLoad()` — `CargoIronOre + CargoIronIngot` (one resource unit = one ton; see §6).

### 2.2 Travel

```csharp
public interface ITravelPlanner
{
    TravelPlan Plan(Coordinates origin, Coordinates destination,
                    decimal speedPerSecond, DateTimeOffset departAt);
}

public sealed record TravelPlan(
    DateTimeOffset ArrivesAt,
    decimal TotalDistance,
    IReadOnlyList<TravelLeg> Legs);

public sealed record TravelLeg(
    Guid? WaypointPlanetId,     // null in MVP — the single leg ends at the destination
    decimal Distance,
    DateTimeOffset ArrivesAt);
```

`LinearTravelPlanner` is the MVP implementation: Euclidean 3D distance ÷ speed, one leg, arrival at `departAt + distance / speed`. A zero or negative speed is a programming error (every ship type has a positive speed) and throws.

The plan is **recorded on `FleetDeparted`**, so a fleet in flight keeps the economics it departed under even if balance or the planner changes mid-flight — the same principle as Phase 3 carrying `DrainPerSecond` on `ShipConstructionQueued` (D10 there).

### 2.3 Fleet lifecycle

**Assembly** (`Planet` + `Fleet`, one transaction):
1. Validate that every selected ship is on the roster (409) and owned by the caller (403, per D13 — the *planet* need not be owned, so ships stranded on a foreign or unowned world can fly home), cargo ≤ `GetCargoCapacity()` of the selected ships (400), cargo ≤ current stored amounts (409). Loading cargo additionally requires owning the planet (403) — you cannot draw from someone else's storage.
2. Planet stream: `ShipsRemovedFromRoster`, and `CargoLoadedFromStorage` when cargo is requested.
3. Fleet stream: `StartStream<Fleet>` with `FleetAssembled`, then `CargoLoaded`.

**Launch** (`Fleet` only — ships already left the roster at assembly):
1. Validate `Stationed` (409), fleet ownership (403), destination exists (404), and per-mission preconditions (§2.4). The planet the fleet is sitting on need not be owned — a fleet idling after a failed colonize can be re-targeted in place.
2. `ITravelPlanner.Plan(...)` → `TravelPlan`.
3. Append `FleetDeparted(origin, destination, mission, departedAt, plan)`.
4. Schedule `CompleteFleetArrival(FleetId, ArrivesAt)` at the ETA via the Marten outbox.

**Arrival** (thin handler → pure methods, ADR 0001):
1. `FetchForWriting<Fleet>`; no-op unless still `InTransit` with a matching `ArrivesAt` (validate-on-arrival).
2. Append `FleetArrived`; `Status = Stationed`, `LocationPlanetId = destination`.
3. Dispatch on `Mission` (§2.4).

**Disband** — `Stationed` only, cargo must be empty (409 otherwise, D11). Planet stream gets `ShipsAddedToRoster`; fleet stream gets `FleetDisbanded`. Permitted at unowned planets, per `fleets.md`.

### 2.4 Missions

**Move** — relocation only; arrival adds nothing beyond §2.3. Cargo may ride along and is left untouched (no unload), which is what makes a stationed fleet re-targetable without first emptying it.

**Transport** — destination must be owned by the same player, checked at launch and re-checked on arrival; a failed re-check leaves the cargo aboard and the fleet stationed. In MVP the re-check cannot actually fail (planets never change hands — no combat, no abandonment), but arrival is the honest place for the invariant and post-MVP combat makes it live. Unload is computed on the destination planet:

```
headroom_r    = capacity_r − pool_r.GetCurrentValue(at)      for r in { ore, ingot }
accepted_r    = min(cargo_r, headroom_r)
```

Planet appends `CargoDeliveredToStorage(accepted)`, fleet appends `CargoUnloaded(accepted)`; any remainder stays aboard for a later manual unload.

**Colonize** — requires ≥1 Colony Ship at launch (409). On arrival, `FetchForWriting<Planet>(destination)`:
- Uncolonized → append `PlanetColonized(ownerId, 0, 0, at)` (starting stores are zero; the colony is funded by whatever the fleet unloads), fleet appends `ColonyShipConsumed` removing exactly one Colony Ship, then cargo auto-unloads under the Transport rules above.
- Already owned → fleet appends `ColonizationFailed`; the Colony Ship survives and the fleet idles at the planet, cargo intact.

A losing racer in a genuine tie fails its append with `ConcurrencyException`, is retried by the #39 policy, re-reads a now-owned planet, and takes the second branch (D10).

### 2.5 Planet-side changes

- `PlanetCreated` gains `X`, `Y`, `Z`; `Planet` snapshots them; `WorldSeeder` places each planet at a random offset within `PlanetSpread` of its system center.
- `RosterShip` gains `OwnerId` (D13), set from the planet's owner in `Apply(ShipCompleted)` and carried back through disband.
- Roster mutation: `ShipsRemovedFromRoster(FleetId, ShipIds, At)` / `ShipsAddedToRoster(FleetId, Ships, At)`.
- Storage mutation: `CargoLoadedFromStorage` / `CargoDeliveredToStorage` checkpoint the affected pool at `at` (so #44's non-regressing semantics apply), then adjust `CheckpointValue`, clamped to `[0, capacity]`. **Rates are untouched** — cargo changes stored values, not building composition — so these two `Apply` methods are the only composition-preserving ones and deliberately do not call `RebaseRates`.

### 2.6 Registration's homeworld claim (#19)

`PlayerEndpoints` currently picks a random uncolonized planet and colonizes it with no guard. It moves onto the §2.4 guarded claim, wrapped in a bounded retry (default 3 attempts) that re-picks a different uncolonized planet when the claim loses. Exhausting the attempts returns 503; a genuinely full world still returns the existing "no uncolonized planets" error.

## 3. Events & messages catalog

**Fleet stream (new):**

| Event | Payload |
|---|---|
| `FleetAssembled` | `OwnerId, PlanetId, Ships[], AssembledAt` |
| `CargoLoaded` | `IronOre, IronIngot, LoadedAt` |
| `FleetDeparted` | `OriginPlanetId, DestinationPlanetId, Mission, DepartedAt, Plan` |
| `FleetArrived` | `DestinationPlanetId, ArrivedAt` |
| `CargoUnloaded` | `PlanetId, IronOre, IronIngot, UnloadedAt` |
| `ColonyShipConsumed` | `PlanetId, ShipId, ConsumedAt` |
| `ColonizationFailed` | `PlanetId, At` |
| `FleetDisbanded` | `PlanetId, DisbandedAt` |

**Planet stream (new):**

| Event | Payload |
|---|---|
| `ShipsRemovedFromRoster` | `FleetId, ShipIds[], At` |
| `ShipsAddedToRoster` | `FleetId, Ships[], At` |
| `CargoLoadedFromStorage` | `FleetId, IronOre, IronIngot, At` |
| `CargoDeliveredToStorage` | `FleetId, IronOre, IronIngot, At` |

`PlanetCreated` gains coordinates (§2.5). `PlanetColonized` is reused unchanged for fleet colonization.

**Scheduled command message (new, durable via outbox):**

| Command | Scheduled at | Scheduled by |
|---|---|---|
| `CompleteFleetArrival(FleetId, ArrivesAt)` | `ArrivesAt` | Launch endpoint |

Per D7's naming split from Phase 3: commands ask (`CompleteFleetArrival`), events record (`FleetArrived`).

## 4. API surface

| Endpoint | Purpose |
|---|---|
| `POST /api/planets/{planetId}/fleets` | Assemble: `{ shipIds[], cargo? { ironOre, ironIngot } }` |
| `GET /api/planets/{planetId}/fleets` | Paginated — fleets stationed at this planet |
| `GET /api/fleets` | Paginated — the caller's fleets, `status` filter |
| `GET /api/fleets/{fleetId}` | Detail: composition, cargo, status, origin/destination, ETA |
| `POST /api/fleets/{fleetId}/missions` | Launch: `{ mission, destinationPlanetId }` |
| `POST /api/fleets/{fleetId}/unload` | Retry unload for a stationed fleet at an owned planet |
| `POST /api/fleets/{fleetId}/disband` | Ships → roster |
| `GET /api/planets/{planetId}` | Gains `x`, `y`, `z` |

Collections follow the #29 pagination contract; shapes follow #30's read conventions. Mutations require ownership (403); reads stay universe-visible (full visibility, no fog of war in MVP).

## 5. Error handling

- 404 — unknown fleet or destination planet.
- 403 — fleet or planet not owned by the caller; Transport to a planet the caller does not own.
- 409 — fleet not `Stationed`; ship not on the roster; insufficient stored resources; Colonize without a Colony Ship; disband with cargo aboard; `ConcurrencyException` on a contested append (existing handler).
- 400 — cargo exceeding fleet capacity; empty `shipIds`; unknown mission type; invalid pagination.
- The arrival handler never produces HTTP errors — its failure modes are the stale no-op and `ColonizationFailed`.

## 6. Balance placeholders (config-backed)

`BalanceOptions` gains a `ShipBalance` block per ship type, alongside the existing construction balance:

| Ship | Speed (units/s) | Cargo capacity (tons) |
|---|---|---|
| ColonyShip | 0.05 | 0 |
| CargoVessel | 0.10 | 500 |

`WorldGenOptions.PlanetSpread` = 20 units (planets scatter within ±20 of their system center; `CoordinateRange` stays ±1000 per axis).

One resource unit = one ton, so a Cargo Vessel holds 500 units of any mix of ore and ingots. Sanity: a typical inter-system hop of ~1000 units takes a Cargo Vessel ~2.8 h and a Colony fleet ~5.6 h; an intra-system hop of ~20 units takes ~3 min and ~7 min. The widest possible trip (~3464 units) is ~19 h for a Colony fleet. All values are balancing placeholders (TBD per CLAUDE.md).

## 7. Testing strategy

Layered as in Phase 3 — logic lives in pure methods, so most coverage needs no clock and no scheduler:

1. **Unit (bulk):** `LinearTravelPlanner` distance/ETA math; `GetSpeed` picking the slowest ship; capacity checks; `Arrive` dispatch per mission; unload headroom arithmetic including the exactly-full and over-capacity cases; the stale-arrival no-op; disband guards.
2. **Integration, handler-invoked:** call `CompleteFleetArrival` directly with a crafted command — verifies handler → two-stream append → projection wiring without waiting for delivery.
3. **Scheduling persistence:** assert the scheduled envelope exists in Wolverine's Postgres envelope table at the computed ETA.
4. **Race coverage:** two fleets arriving at one uncolonized planet — exactly one `PlanetColonized`, the other `ColonizationFailed` with its Colony Ship intact; concurrent registrations never double-colonize (closes #19).
5. **End-to-end:** test-host `Balance` overrides raise ship speeds so a real scheduled arrival resolves in seconds.

## 8. PR breakdown & sequencing

| PR | Issue | Content | Merge gate |
|---|---|---|---|
| 0 | #40 | Split `Planet.cs` into partial classes | Existing suite green |
| 1 | #48 | `Fleet` aggregate, `RosterShip.OwnerId`, assembly & disband, fleet read endpoints | Roster round-trip integration: build ships → assemble → roster shrinks → disband → restored |
| 2 | #49 | Coordinates + world-gen spread, `ShipBalance`, `ITravelPlanner`, departure/arrival, Move | Planner units; launch → in-transit with ETA → arrival → stationed at destination |
| 3 | #50 | Cargo at assembly, Transport, auto + manual unload, partial delivery | Load → transport → resources moved; full-storage run leaves a remainder aboard |
| 4 | #51 | Colonize, guarded claim, registration onto the same path (closes #19) | Colonize an empty planet; two-fleet race; concurrent-registration race |

Dependency chain: #40 → #48 → #49 → { #50, #51 }. The last two are independent: if #51 merges first, its cargo auto-unload step is simply inert (a fleet cannot carry cargo until #50 lands) and #50's PR wires it up. Each PR updates `technical-design/domain-model.md`; #49 records the D2 supersession in `architecture.md` §4; #48 updates `game-design/fleets.md` for D6.

The arrival handler is the codebase's first cross-aggregate append (Fleet + destination Planet in one `SaveChangesAsync()`). #39 removed the local-queue parallelism throttle specifically to unblock this; both streams are fetched with `FetchForWriting`, so a contested arrival retries rather than racing.

## 9. Out of scope

- **Fleet recall / cancellation in transit** (`fleets.md`'s turn-around rule) — Phase 5, [#21 in the plan](phase-5-hardening.md). Travel here is one-way; the recall path reuses the same planner and scheduled-message machinery.
- **Warp lanes, jump gates, multi-leg routing** — post-MVP. D3/D4 exist so this lands without reshaping events or handlers.
- **Per-ship cargo binding** — post-MVP, arrives with combat (D8).
- **Scoring of in-transit ships and cargo** — Phase 5; the `Fleet` stream is the read path it will use.
- **Fog of war, scouting, interception** — post-MVP.
- **Storage-full halting interactions** — a delivery that fills storage does not halt producers here; that cascade is Phase 5.
