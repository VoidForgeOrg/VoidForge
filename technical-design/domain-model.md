# Domain Model

## Overview

The domain uses two storage patterns from Marten:

1. **Event-sourced aggregates** (`Domain/`) — state rebuilt from an event stream, persisted as inline snapshots
2. **Documents** (`Documents/`) — plain JSONB documents, no event history

## Aggregates

### Player

- **File**: `Domain/Player.cs`
- **Events**: `PlayerRegistered(Name, RegisteredAt)`
- **Snapshot fields**: `Id`, `Name`, `RegisteredAt`
- **Unique index**: `Name` (DB-level enforcement)
- **Marten config**: `opts.Projections.Snapshot<Player>(SnapshotLifecycle.Inline)`

Created during registration via `session.Events.StartStream<Player>(...)`.

### Planet

- **File**: `Domain/Planet.cs`
- **Events**: `PlanetCreated(Name, SolarSystemId, IronOrePool, BuildingSlotCount, IronOreStorageCapacity, IronIngotStorageCapacity, X, Y, Z)`, `PlanetColonized(OwnerId, IronOreStored, IronIngotStored, ColonizedAt)`, `BuildingPlaced(BuildingType, PlacedAt)`, `BuildingConstructionStarted(SlotIndex, BuildingType, StartedAt, CompletesAt, DrainPerSecond)`, `BuildingCompleted(SlotIndex, CompletedAt)`, `ShipConstructionQueued`, `ShipConstructionStarted`, `ShipCompleted`, `ShipConstructionCancelled`, `ShipsRemovedFromRoster(FleetId, ShipIds, At)`, `ShipsAddedToRoster(FleetId, Ships, At)`
- **Snapshot fields**: `Id`, `Name`, `SolarSystemId`, `OwnerId` (nullable), `IronOrePool`, `BuildingSlotCount`, `X`, `Y`, `Z` (decimal, 3D coordinates), `IronOre` (ResourcePool), `IronIngot` (ResourcePool), `Buildings` (`IList<BuildingSlot>`), `ShipQueue` (`IList<ShipBuild>`), `Ships` (`IList<RosterShip>`)
- **Behavior**: `PlaceBuilding(type, placedAt)` enforces the slot-availability invariant (throws `NoFreeSlotsException`) and returns the `BuildingPlaced` event to append — it does not mutate; the mutation happens in `Apply` once persisted.
- **Marten config**: `opts.Projections.Snapshot<Planet>(SnapshotLifecycle.Inline)`

Created during world seeding via `session.Events.StartStream<Planet>(...)`. `OwnerId` starts null (uncolonized).

**Coordinates (spec D5, #49)**: `X`/`Y`/`Z` are snapshot fields (`decimal`), set once by `Apply(PlanetCreated)` and immutable thereafter — no travel-related event mutates them (a planet doesn't move; fleets do). `GetCoordinates() → Coordinates` (`Domain/Coordinates.cs`, `record Coordinates(decimal X, decimal Y, decimal Z)`) is a method, not a computed property, for the same reason as the energy getters (kept out of the Marten snapshot document). `WorldSeeder` hoists each solar system's `X/Y/Z` into locals before the planet loop, then scatters every planet in that system around the shared centre: `systemX + NextCoordinate(random, opts.PlanetSpread)` (same for Y/Z), where `WorldGenOptions.PlanetSpread` (default `20m`) bounds the offset. `PlanetResponse` surfaces `X`/`Y`/`Z` alongside the existing fields.

`Apply(BuildingPlaced)` appends a `BuildingSlot` and calls `RebaseRates(at)`: both pools are checkpointed at the event instant (locking in value accrued under the old rates), then rates are re-derived from scratch as a pure function of the operational building composition × the energy productivity multiplier. Wholesale re-derivation (not incremental deltas) is required because the multiplier rescales every operational consumer whenever any building changes. Every composition-changing `Apply` must end with `RebaseRates`. Refinery consumption (#25) is wired here too: `oreInflow` (Σ drill extraction × m) and `refineryDemand` (Σ refinery consumption × m) yield `effectiveConsumption = min(refineryDemand, oreInflow)`; the net `IronOre.Rate = oreInflow − effectiveConsumption` (never negative in Phase 3 — refineries convert the inflow, not the stored buffer) and `IronIngot.Rate = RefineryIngotOutputFactor × effectiveConsumption` (1:2 ratio). Even-split falls out because the pools are planet-level scalars.

**Energy** is a flow resource — derived on demand, never stored: `GetEnergyGenerationMw()` (Σ operational Generator output), `GetEnergyConsumptionMw()` (Σ operational consumer draw), `GetProductivityMultiplier()` (`1` when demand is met, `generation/consumption` when overloaded, `0` with consumers but no generator; range `[0, 1]`). These are methods, not computed properties, to stay out of the Marten snapshot document. Surfaced via the `EnergyResponse` block on `PlanetResponse`.

**Construction (#26)**: player placement via `POST /api/planets/{id}/buildings` calls `Planet.StartConstruction(type, now, ingotCost, buildDurationSeconds)` (balance from `IOptions<BalanceOptions>`, bound from the `Balance` config section — DI options rather than a static so aggregate replay stays pure and unit tests are hermetic). The slot is added `UnderConstruction` carrying `CompletesAt` + `ConstructionDrainPerSecond`; `RebaseRates` subtracts the drain from the ingot rate (not scaled by `m`; the rate may go negative and the stored value clamps at 0 — zero-ingot halting is Phase 5). The endpoint schedules a durable `CompleteBuildingConstruction` message (Marten transactional outbox) at `CompletesAt`. The thin `CompleteBuildingConstructionHandler` calls the pure, idempotent `Planet.CompleteBuilding(slotIndex, at)` — which returns an empty event list (no-op) unless the slot is still `UnderConstruction` with a matching `CompletesAt` (validate-on-arrival) — then `Apply(BuildingCompleted)` flips the slot `Operational` and re-derives rates. Homeworld buildings bypass all of this (seeded `Operational` via `BuildingPlaced`).

**Shipyard & ship queue (#27)**: a single planet-level FIFO `ShipQueue` with fungible bays — `capacity = BuildingSpecs.ShipyardParallelBuilds (3) × operational Shipyards`; builds are not assigned to a specific shipyard (spec decision D5, revising the earlier per-shipyard wording). `QueueShip` is unconditional (a ship may be queued with no shipyard; it waits); it starts immediately if capacity is free. Balance (cost/duration → `DrainPerSecond`, `BuildDurationSeconds`) is computed at the endpoint from `IOptions<BalanceOptions>` and carried on `ShipConstructionQueued` into the `ShipBuild`, so auto-start inside pure methods needs no config. `CompleteShipBuild` (durable `CompleteShipConstruction` message, validate-on-arrival) appends `ShipCompleted` (→ `RosterShip`) and auto-starts the next queued build; `CancelShipBuild` (no refund) frees a slot and auto-starts the next; a completing Shipyard raises capacity and auto-starts queued builds. Active ship-build drain subtracts from the ingot rate (not scaled by `m`). **Shipyard energy is state-dependent:** `GetEnergyConsumptionMw` draws full power for `ceil(activeBuilds / 3)` shipyards and `ShipyardIdleDrawFactor` (5%) for the rest — the first appearance of the "5% when idle" rule. `PlanetResponse` carries bounded counts (`shipCount`, `activeBuilds`, `queueLength`); the roster and queue are paginated endpoints (`GET /api/planets/{id}/ships`, `GET /api/planets/{id}/ship-queue`).

**Fleet assembly/disband roster mutations (#48)**: `RemoveShipsFromRoster(fleetId, shipIds, at)` and `ReturnShipsToRoster(fleetId, ships, at)` are pure factories on `Planet` (in `Planet.Ships.cs`) that produce `ShipsRemovedFromRoster` / `ShipsAddedToRoster`. Their `Apply` methods only splice `Ships` — roster ships are inert (no rate/energy contribution), so neither calls `RebaseRates`. See the `### Fleet` section below for the two-stream transactions that pair these with the `Fleet` aggregate's own events.

### Fleet

- **File**: `Domain/Fleet.cs`
- **Events**: `FleetAssembled(OwnerId, PlanetId, Ships, AssembledAt)`, `FleetDeparted(OriginPlanetId, DestinationPlanetId, Mission, DepartedAt, Plan)`, `FleetArrived(DestinationPlanetId, ArrivedAt)`, `FleetDisbanded(PlanetId, DisbandedAt)`
- **Snapshot fields**: `Id`, `OwnerId`, `Status` (`FleetStatus`), `LocationPlanetId` (nullable), `AssembledAt`, `Ships` (`IList<FleetShip>`); **transit snapshot (#49)**: `OriginPlanetId`, `DestinationPlanetId`, `Mission` (`MissionType?`), `DepartedAt`, `ArrivesAt`, `TravelPlan` — all null while `Stationed`, populated by `Depart`, cleared by `Arrive`. Pre-#49 snapshots deserialize with these null, which is correct: a fleet that predates travel was necessarily `Stationed`.
- **`FleetShip`** (Value Object, `Domain/FleetShip.cs`): `Id`, `Type`, `CompletedAt` — mirrors `RosterShip` minus `OwnerId` (a fleet has one owner) so ships round-trip through a fleet without losing the roster's stable sort key.
- **`FleetStatus`** (`Domain/FleetStatus.cs`): `Stationed`, `InTransit` (wired by travel, #49), `Disbanded` (terminal — the snapshot survives as history; list endpoints filter it out unless explicitly requested via `?status=Disbanded`).
- **Marten config**: `opts.Projections.Snapshot<Fleet>(SnapshotLifecycle.Inline)`

Fleets outlive any single mission (spec D1) and are event-sourced like `Player`/`Planet`, with an inline snapshot.

**Assembly** (`FleetEndpoints.Assemble`, `POST /api/planets/{planetId}/fleets`) is one transaction spanning two streams: the endpoint validates the ship ids exist on the target planet's roster (404 planet, 409 unknown ship) and that the caller owns every ship (403) — ownership is checked per-ship (D13), not per-planet, so ships stranded on a foreign or unowned world can still be assembled by their owner. It then appends `ShipsRemovedFromRoster` on the `Planet` stream and `session.Events.StartStream<Fleet>(fleetId, Fleet.Assemble(...))` on a brand-new `Fleet` stream, committed together with one `SaveChangesAsync()`.

**Travel — launch, transit, arrival (#49, spec D2–D6)**: `FleetEndpoints.Launch` (`POST /api/fleets/{fleetId}/missions`, `LaunchMissionRequest(Mission, DestinationPlanetId)`) currently only accepts `MissionType.Move` (Transport/Colonize dispatch land in #50/#51 — anything else is 400). It touches only the `Fleet` stream (spec §2.3): origin and destination `Planet` documents are read for `GetCoordinates()` but never appended to. The endpoint validates the fleet is `Stationed` (409 otherwise), the destination differs from the current location (400) and exists (404), then computes `fleet.GetSpeed(t => balance.Ships.For(t).SpeedPerSecond)` (the slowest ship in the fleet governs speed) and calls `ITravelPlanner.Plan(origin, destination, speed, now)` (`Travel/LinearTravelPlanner.cs` — straight-line distance ÷ speed; post-MVP planners plug into the same seam without reshaping the event model, spec D3/D4) to get a `TravelPlan(ArrivesAt, TotalDistance, Legs)`.

`Fleet.Depart(destinationPlanetId, mission, plan, at)` (stationed-only, throws otherwise) returns `FleetDeparted` carrying the **whole `TravelPlan`**, not just `ArrivesAt` — so an in-flight fleet keeps the departure economics (distance, speed, arrival time) it launched under even if balance numbers or the travel planner change mid-flight, the same Phase 3 D10 principle that puts `DrainPerSecond` on `ShipConstructionQueued`. `Apply(FleetDeparted)` sets `Status = InTransit`, blanks `LocationPlanetId`, and populates the transit snapshot fields (`OriginPlanetId`, `DestinationPlanetId`, `Mission`, `DepartedAt`, `ArrivesAt`, `TravelPlan`). The endpoint schedules a durable `CompleteFleetArrival(FleetId, ArrivesAt)` message (`bus.ScheduleAsync`) at `plan.ArrivesAt` in the same transaction as the `FleetDeparted` append (ADR 0001's transactional-outbox guarantee).

Arrival resolves the same way build/ship completion do (ADR 0001, D2) — **not** as a Wolverine Saga, which is how `architecture.md` §4 originally sketched fleet missions before this PR (see the supersession note there). The thin `CompleteFleetArrivalHandler` (`Endpoints/CompleteFleetArrivalHandler.cs`) calls the pure, idempotent `Fleet.Arrive(at)`: validate-on-arrival returns an empty event list (no-op) unless the fleet is still `InTransit` with `ArrivesAt == at`, so stale or duplicate scheduled messages (a fleet that already arrived, or a message carrying a superseded `ArrivesAt`) are harmless. Otherwise it returns `[FleetArrived(DestinationPlanetId, at)]`, which the handler appends and saves. **D6 — arrival always leaves the fleet `Stationed`** at the destination: `Apply(FleetArrived)` sets `Status = Stationed`, `LocationPlanetId = DestinationPlanetId`, and clears the entire transit block (`OriginPlanetId`/`DestinationPlanetId`/`Mission`/`DepartedAt`/`ArrivesAt`/`TravelPlan` all null) — nothing about the just-finished mission survives on the snapshot. Disband, not arrival, is the only path back onto a planet's roster. `Fleet.Arrive` returns `IReadOnlyList<object>` rather than a single event so mission dispatch beyond Move (cargo unload, colonization claim) can add elements in #50/#51 without reshaping this method's signature.

**Disband** (`FleetEndpoints.Disband`, `POST /api/fleets/{fleetId}/disband`) is the mirror-image two-stream transaction: `FetchForWriting<Fleet>`, authorize (403 non-owner), guard `Status == Stationed` (409 otherwise — `Fleet.Disband` throws if called off a non-stationed fleet, so a fleet must have completed `Arrive` and landed per D6 before it can be disbanded), then append `ShipsAddedToRoster` on the `Planet` stream (via `Planet.ReturnShipsToRoster`, fed by `Fleet.ToRosterShips()`) and `FleetDisbanded` on the `Fleet` stream, again one `SaveChangesAsync()`. `Fleet.ToRosterShips()` stamps each returned `RosterShip` with the fleet's `OwnerId` (D13), so ships disbanded onto a foreign or unowned planet's roster stay reachable — and assemblable — by their original owner, not the planet's owner.

`Disbanded` is terminal: a disbanded fleet's `Ships` list is cleared and its `Status` never changes again. `GetOwnFleets` defaults to excluding `Disbanded` fleets (`status is null ? query.Where(f => f.Status != FleetStatus.Disbanded) : query.Where(f => f.Status == status)`); passing `?status=Disbanded` explicitly opts into fleet history. `GetPlanetFleets` always lists only `Stationed` fleets at that planet, with no status override.

**`FleetResponse`** (`Endpoints/FleetResponse.cs`) surfaces the full transit snapshot alongside the existing fields (`OriginPlanetId`, `DestinationPlanetId`, `Mission`, `DepartedAt`, `ArrivesAt`) so a mid-transit `GET /api/fleets/{id}` round-trips the same picture the launch response returned — all null once the fleet is `Stationed` or `Disbanded`.

### Buildings (Value Objects)

- **Files**: `Domain/BuildingType.cs` (`Drill`, `Refinery`, `Shipyard`, `Generator`), `Domain/BuildingStatus.cs` (`Operational` — grows to `UnderConstruction` in Phase 3, `Halted` in Phase 5), `Domain/BuildingSlot.cs` (`record BuildingSlot(BuildingType Type, BuildingStatus Status)`)
- **`BuildingSpecs`** (`Domain/BuildingSpecs.cs`): intrinsic, balance-tunable stats per building type — `IronOreRatePerSecond(type)` (Drill = 10 units/sec; others 0). These are domain rules, not world-gen knobs. Units are **per second** to match `ResourcePool` (which accrues over elapsed `TotalSeconds`).
  Energy specs (Phase 3, #24): `EnergyOutputMw(type)` (Generator = 100 MW) and `EnergyDrawMw(type)` (Drill 20 / Refinery 30 / Shipyard 40 MW; Shipyard's 5%-idle rule arrives with #27). Balance placeholders, TBD during balancing.
  Refinery specs (#25): `RefineryOreConsumptionPerSecond(type)` (Refinery = 5/s) and the `RefineryIngotOutputFactor` constant (= 2, the 1:2 ratio in one place).
- **Phase 3 semantics**: Placement is still instant and free in this PR (construction cost/time arrives in #26). The `Drill` extracts ore, the `Generator` produces energy (driving the productivity multiplier), and the `Refinery` converts drill inflow into ingots at 1:2. Only the `Shipyard` remains inert until #27. Available slots = `BuildingSlotCount - Buildings.Count`.
- **Placement**: `POST /api/planets/{planetId}/buildings` (`BuildingEndpoints.cs`). The endpoint owns the application concerns — existence (404) and ownership/authorization (403) — then delegates to `Planet.PlaceBuilding`, mapping `NoFreeSlotsException` to 409. The slot invariant itself lives in the domain.

Homeworld starts with 1 Drill, 1 Refinery, 1 Generator, appended alongside `PlanetColonized` during registration.

### ResourcePool (Value Object)

- **File**: `Domain/ResourcePool.cs`
- **Fields**: `CheckpointValue` (decimal), `Rate` (decimal, units/sec), `StorageCapacity` (decimal), `CheckpointTime` (DateTimeOffset)
- **Methods**: `GetCurrentValue(now)` — computes `Clamp(checkpoint + rate * elapsed, 0, capacity)`; `Checkpoint(now)` — returns new instance with current value as baseline
- **Semantics**: Immutable record. Methods return new instances. Rate starts at 0; buildings (#10) will set rates via checkpointing.

Used by `Planet.IronOre` and `Planet.IronIngot`. The query endpoint computes current values at request time using `GetCurrentValue(timeProvider.GetUtcNow())` — no background ticks needed.

## Documents

### ApiKey

- **File**: `Documents/ApiKey.cs`
- **Fields**: `Id`, `HashedKey`, `PlayerId`, `CreatedAt`
- **Unique index**: `HashedKey`

Stores SHA-256 hashed API keys. The raw key is returned once at registration and never stored.

### SolarSystem

- **File**: `Documents/SolarSystem.cs`
- **Fields**: `Id`, `Name`, `X`, `Y`, `Z` (decimal coordinates), `PlanetIds`

Groups planets into a named system at a 3D position. Created during world seeding.

## Relationships

```
Registration (atomic transaction):
┌──────────────────────────────────────────────────┐
│  1. StartStream<Player>(playerId, event)         │  → mt_events + mt_doc_player
│  2. Append(homeworldId, PlanetColonized,         │  → mt_events + mt_doc_planet
│       BuildingPlaced×3: Drill, Refinery, Generator)
│  3. Store(new ApiKey { ... })                    │  → mt_doc_apikey
│  4. SaveChangesAsync()                           │  → single DB transaction
└──────────────────────────────────────────────────┘
  Homeworld: random uncolonized planet, colonized with starting resources
  and starting buildings (Drill sets the Iron Ore extraction rate)

Authentication:
  X-API-Key header → SHA-256 hash → query ApiKey doc → PlayerId → ClaimsPrincipal

World Seeding (atomic transaction via IHostedService):
┌─────────────────────────────────────────────────┐
│  For each solar system:                         │
│    For each planet:                             │
│      StartStream<Planet>(planetId, PlanetCreated)│
│    Store(new SolarSystem { PlanetIds = [...] }) │
│  SaveChangesAsync()  → single DB transaction    │
└─────────────────────────────────────────────────┘
  Idempotent: skips if SolarSystem count > 0
```

## Adding New Aggregates

1. Create the aggregate class in `Domain/` with `Apply()` methods for each event
2. Create event records in `Domain/Events/`
3. Register inline snapshot in `Program.cs`: `opts.Projections.Snapshot<T>(SnapshotLifecycle.Inline)`
4. Add unique indexes if needed: `opts.Schema.For<T>().UniqueIndex(...)`
