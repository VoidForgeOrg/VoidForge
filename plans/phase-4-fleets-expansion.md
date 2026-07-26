# Phase 4 — Fleets & Expansion

**Goal:** Players can assemble ships into fleets, send them across space, and colonize new planets or transport resources. The game becomes multi-planet.

**Design spec:** [`phase-4-fleets-expansion-design.md`](phase-4-fleets-expansion-design.md) — full decisions, events catalog, API surface, balance placeholders.

> **Tracking:** epic + per-feature issues (numbers filled in once filed).
> Dependency order: **prep (#40) → A → B → { C, D }**.

## Decisions (settled during refinement)

- **`Fleet` is a new event-sourced aggregate** — its own Marten stream and inline snapshot, alongside `Planet` and `Player`.
- **Arrival is a durable scheduled message, not a Wolverine Saga.** This supersedes the `FleetMission : Saga` sketch in `technical-design/architecture.md` §4, which predates [ADR 0001](../technical-design/adr/0001-completion-event-resolution.md). The schedule → validate-on-arrival → pure-aggregate-method pattern is already proven by #26 and #27.
- **Travel sits behind an `ITravelPlanner` seam** and the resulting `TravelPlan` (with a single-element `Legs` list in MVP) is recorded on `FleetDeparted`. Warp lanes and jump gates are planned post-MVP and will change travel drastically; this is the seam that keeps that change additive.
- **Planets get their own `X`/`Y`/`Z`**, seeded as a spread around their system's center. No production data exists — dev worlds reseed.
- **Arrival always leaves the fleet `Stationed`**; ships reach the destination roster only via an explicit disband. `game-design/fleets.md` is updated to match (its "ships join the roster on arrival" rule collided with partial cargo staying bound to ships).
- **Cargo loads at assembly**, is tracked as fleet-level totals, and disband is refused while cargo remains.
- **Colonization and registration share one guarded claim** (`FetchForWriting` + null-owner assertion + #39 retry), which closes the open race bug #19.
- **`RosterShip` gains an `OwnerId`**, and assembly validates ship ownership rather than planet ownership — otherwise ships disbanded onto an unowned planet's roster (which `fleets.md` explicitly permits) could never be re-assembled.

## Issues

### Prep — #40: Split the `Planet` aggregate via partial classes
**Labels:** `enhancement`, `domain:core`

Already filed. Done first: every feature below adds roster or storage behavior to a 345-line `Planet.cs`.

---

### A. Fleet assembly & disband
**Labels:** `domain:fleets`

Ships on a planet can be grouped into fleets.

**Scope:**
- `Fleet` aggregate (`Id`, `OwnerId`, `Status`, `LocationPlanetId`, `Ships`), `FleetShip`, inline snapshot registration
- `RosterShip` gains `OwnerId`; assembly validates ship ownership, not planet ownership
- Assemble from the planet roster; disband returns ships (refused while cargo is aboard — cargo arrives in C)
- `POST /api/planets/{planetId}/fleets`, `POST /api/fleets/{fleetId}/disband`
- `GET /api/fleets`, `GET /api/fleets/{fleetId}`, `GET /api/planets/{planetId}/fleets` (paginated per #29)
- Fleet events: `FleetAssembled`, `FleetDisbanded`. Planet events: `ShipsRemovedFromRoster`, `ShipsAddedToRoster`
- Updates `game-design/fleets.md` for the always-stationed arrival rule
- Integration test: build ships → assemble → roster shrinks → disband → ships returned

**Depends on:** #40 (prep), #27 (ships exist on the roster)

---

### B. Planet coordinates, travel & the Move mission
**Labels:** `domain:fleets`, `domain:planets`

Fleets travel through 3D space and arrive.

**Scope:**
- `PlanetCreated` gains `X`/`Y`/`Z`; `WorldGenOptions.PlanetSpread`; `PlanetResponse` surfaces coordinates
- `ShipBalance` (speed, cargo capacity) added to `BalanceOptions`
- `ITravelPlanner` / `LinearTravelPlanner` / `TravelPlan` / `TravelLeg`; fleet speed = slowest ship
- `FleetDeparted` records the plan; `CompleteFleetArrival` scheduled at the ETA; thin handler with validate-on-arrival
- Move mission end to end; fleet ends `Stationed` at the destination
- `POST /api/fleets/{fleetId}/missions`
- Records the §4 saga supersession in `technical-design/architecture.md`
- Integration test: launch → in-transit with ETA → arrival → stationed at destination

**Depends on:** A

---

### C. Cargo & the Transport mission
**Labels:** `domain:fleets`, `domain:resources`

Resources move physically between planets.

**Scope:**
- Cargo on assembly: capacity check (Σ Cargo Vessel capacity) and storage withdrawal
- Storage mutation events checkpoint the pool (#44 semantics) and adjust the checkpoint value without touching rates
- Transport mission: auto-unload on arrival, capped by destination headroom; remainder stays aboard
- `POST /api/fleets/{fleetId}/unload` for a stationed fleet at an owned planet
- Fleet events: `CargoLoaded`, `CargoUnloaded`. Planet events: `CargoLoadedFromStorage`, `CargoDeliveredToStorage`
- Integration test: load → transport → resources moved; full-storage run leaves a remainder aboard

**Depends on:** B

---

### D. Colonize mission (closes #19)
**Labels:** `domain:fleets`, `domain:planets`, `bug`

Fleets claim uncolonized planets.

**Scope:**
- Guarded claim: `FetchForWriting<Planet>` + null-owner assertion, losers retried by the #39 policy
- Success: `PlanetColonized` with zero starting stores, one Colony Ship consumed, cargo auto-unloaded
- Failure (already taken): `ColonizationFailed`, Colony Ship preserved, fleet idles at the planet
- Registration's homeworld assignment moves onto the same guarded claim with a bounded re-pick retry — closes #19
- Fleet events: `ColonyShipConsumed`, `ColonizationFailed`
- Integration tests: colonize an empty planet; two fleets racing one planet; concurrent registrations

**Depends on:** B. Independent of C — if D merges first, its cargo auto-unload step is inert until C lands.

---

## Phase Completion

- Player can assemble ships into a fleet and send it to another planet
- Colonize claims an uncolonized planet, consuming one Colony Ship; a lost race fails cleanly
- Transport delivers resources to another owned planet, with partial unload when storage is full
- Move relocates ships
- The game is multi-planet: build up economy → build ships → expand → build up new colony
