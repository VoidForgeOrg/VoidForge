# Phase 2 — Domain Skeleton

**Goal:** The core domain chain is wired up: a player exists, owns a planet, the planet has resource pools and building slots, buildings extract resources, and lazy calculation makes resource values change over time. No edge cases yet — just the happy path.

## Issues

### 4. Player aggregate & registration
**Labels:** `domain:players`, `api`

A player can register and gets an API key.

**Scope:**
- `Player` as Marten event-sourced aggregate with inline snapshot projection
- Events: `PlayerRegistered`
- Registration handler: create player, generate API key, return key (shown once)
- `POST /api/players/register` endpoint (`[AllowAnonymous]`)
- `GET /api/players/me` endpoint (returns player info)
- Integration test: register → use API key → hit authenticated endpoint

**Depends on:** #3 (auth middleware)

---

### 5. Solar systems, planets & world generation
**Labels:** `domain:planets`, `api`

The universe exists — solar systems with planets, each having resource pools and building slots.

**Scope:**
- `SolarSystem` as Marten document: ID, name, 3D coordinates (X, Y, Z as decimals), list of planet IDs
- `Planet` as Marten event-sourced aggregate with inline snapshot projection
- Minimal planet state: resource pools (Iron Ore), building slots (fixed count), storage capacities, owner (nullable)
- Events: `PlanetCreated`
- `ResourceType` enum: `IronOre`, `IronIngots`
- Seeding mechanism: generate N solar systems with M planets each, configurable parameters (pool sizes, slot counts, storage caps, coordinate range). Idempotent.
- `GET /api/solar-systems` and `GET /api/planets/{id}` — basic query endpoints
- Unit tests: aggregate creation, snapshot projection

**Depends on:** #1

---

### 6. Planet ownership & homeworld assignment
**Labels:** `domain:planets`, `domain:players`

Players claim planets. New players get a homeworld.

**Scope:**
- `PlanetColonized` event: sets owner on planet
- Registration handler extended: assign an uncolonized planet as homeworld
- Event: `HomeworldAssigned` on the Player aggregate
- Homeworld starts with resources in storage (static starting values, no production yet)
- Validation: planet must be uncolonized
- Integration test: register → verify homeworld is owned → verify starting resources

**Depends on:** #4 (player exists), #5 (planets exist)

---

### 7. Lazy calculation engine
**Labels:** `domain:core`

The math that powers everything. Resource values change over time without background processing.

**Scope:**
- `ResourcePool` value object: `CheckpointValue`, `Rate` (units/sec), `StorageCapacity`, `CheckpointTime` — all `decimal` except time
- `GetCurrentValue(DateTimeOffset now)` → `Math.Clamp(checkpoint + rate * elapsed, 0, capacity)`
- `Checkpoint(DateTimeOffset now)` → computes current value, resets baseline
- `CheckpointAllResources(DateTimeOffset now)` on Planet aggregate
- Planet query endpoint returns computed values (lazy calc applied at read time)
- Unit tests: rate accumulation, clamping at zero/capacity, negative rates, checkpoint resets

**Depends on:** #5 (planet aggregate exists)

---

### 8. Buildings & resource extraction
**Labels:** `domain:buildings`, `domain:resources`

Buildings can be placed on planets. Drills extract ore. This is the first thing that makes lazy calc actually visible.

**Scope:**
- `BuildingType` enum: `Drill`, `Refinery`, `Shipyard`, `Generator`
- `BuildingSlot` on planet: type, status (operational)
- `BuildingPlaced` event: places a building in a slot, sets it to operational immediately (no construction time yet — that's Phase 5)
- When a Drill is placed: set the planet's Iron Ore resource pool rate to the drill's extraction rate
- Multiple drills: rates are additive
- Homeworld setup updated: place starting buildings (1 Drill, 1 Refinery, 1 Generator), set drill extraction rate
- `POST /api/planets/{planetId}/buildings` — place a building (simplified: instant, no cost yet)
- Integration test: place a drill → query planet over time → verify ore increases

**Depends on:** #6 (planet ownership), #7 (lazy calc)

---

## Phase Completion

- Player registers, gets API key, receives a homeworld
- Homeworld has a Drill, Refinery, Generator in building slots
- Querying the homeworld shows Iron Ore increasing over time (lazy calc working)
- Planets and solar systems are queryable via API
- No construction costs, no energy, no refining — just the structural chain wired up
