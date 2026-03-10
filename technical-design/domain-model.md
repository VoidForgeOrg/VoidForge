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
- **Events**: `PlanetCreated(Name, SolarSystemId, IronOrePool, BuildingSlotCount, IronOreStorageCapacity, IronIngotStorageCapacity)`, `PlanetColonized(OwnerId, IronOreStored, IronIngotStored)`
- **Snapshot fields**: `Id`, `Name`, `SolarSystemId`, `OwnerId` (nullable), `IronOrePool`, `BuildingSlotCount`, `IronOreStorageCapacity`, `IronIngotStorageCapacity`, `IronOreStored`, `IronIngotStored`
- **Marten config**: `opts.Projections.Snapshot<Planet>(SnapshotLifecycle.Inline)`

Created during world seeding via `session.Events.StartStream<Planet>(...)`. `OwnerId` starts null (uncolonized).

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
│  2. Append(homeworldId, PlanetColonized)         │  → mt_events + mt_doc_planet
│  3. Store(new ApiKey { ... })                    │  → mt_doc_apikey
│  4. SaveChangesAsync()                           │  → single DB transaction
└──────────────────────────────────────────────────┘
  Homeworld: random uncolonized planet, colonized with starting resources

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
