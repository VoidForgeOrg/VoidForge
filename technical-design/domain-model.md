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
- **Events**: `PlanetCreated(Name, SolarSystemId, IronOrePool, BuildingSlotCount, IronOreStorageCapacity, IronIngotStorageCapacity)`, `PlanetColonized(OwnerId, IronOreStored, IronIngotStored, ColonizedAt)`, `BuildingPlaced(BuildingType, PlacedAt)`
- **Snapshot fields**: `Id`, `Name`, `SolarSystemId`, `OwnerId` (nullable), `IronOrePool`, `BuildingSlotCount`, `IronOre` (ResourcePool), `IronIngot` (ResourcePool), `Buildings` (`IList<BuildingSlot>`)
- **Behavior**: `PlaceBuilding(type, placedAt)` enforces the slot-availability invariant (throws `NoFreeSlotsException`) and returns the `BuildingPlaced` event to append — it does not mutate; the mutation happens in `Apply` once persisted.
- **Marten config**: `opts.Projections.Snapshot<Planet>(SnapshotLifecycle.Inline)`

Created during world seeding via `session.Events.StartStream<Planet>(...)`. `OwnerId` starts null (uncolonized).

`Apply(BuildingPlaced)` appends a `BuildingSlot` and, for any building with an extraction rate (the `Drill` in Phase 2), checkpoints `IronOre` at `PlacedAt` before adding the rate — so accumulated ore is locked in at the old rate and multiple drills are additive. The rate is looked up from `BuildingSpecs`, not carried on the event, so replay stays deterministic and balance values live in one place.

### Buildings (Value Objects)

- **Files**: `Domain/BuildingType.cs` (`Drill`, `Refinery`, `Shipyard`, `Generator`), `Domain/BuildingStatus.cs` (`Operational` — grows to `UnderConstruction` in Phase 3, `Halted` in Phase 5), `Domain/BuildingSlot.cs` (`record BuildingSlot(BuildingType Type, BuildingStatus Status)`)
- **`BuildingSpecs`** (`Domain/BuildingSpecs.cs`): intrinsic, balance-tunable stats per building type — `IronOreRatePerSecond(type)` (Drill = 10 units/sec; others 0). These are domain rules, not world-gen knobs. Units are **per second** to match `ResourcePool` (which accrues over elapsed `TotalSeconds`).
- **Phase 2 semantics**: Placement is instant and free; no construction time, cost, or energy yet. Only the `Drill` has behavior (sets the planet's Iron Ore extraction rate). `Refinery` and `Generator` are placed but inert. Available slots = `BuildingSlotCount - Buildings.Count`.
- **Placement**: `POST /api/planets/{planetId}/buildings` (`BuildingEndpoints.cs`). The endpoint owns the application concerns — existence (404) and ownership/authorization (403) — then delegates to `Planet.PlaceBuilding`, mapping `NoFreeSlotsException` to 409. The slot invariant itself lives in the domain.

Homeworld starts with 1 Drill, 1 Refinery, 1 Generator, appended alongside `PlanetColonized` during registration.

### ResourcePool (Value Object)

- **File**: `Domain/ResourcePool.cs`
- **Fields**: `CheckpointValue` (decimal), `Rate` (decimal, units/sec), `StorageCapacity` (decimal), `CheckpointTime` (DateTimeOffset)
- **Methods**: `GetCurrentValue(now)` — computes `Clamp(checkpoint + rate * elapsed, 0, capacity)`; `Checkpoint(now)` — returns new instance with current value as baseline
- **Semantics**: Immutable record. Methods return new instances. Rate starts at 0; buildings (#10) will set rates via checkpointing.

Used by `Planet.IronOre` and `Planet.IronIngot`. The query endpoint computes current values at request time using `GetCurrentValue(DateTimeOffset.UtcNow)` — no background ticks needed.

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
