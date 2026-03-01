# Phase 4 — Fleets & Expansion

**Goal:** Players can assemble ships into fleets, send them across space, and colonize new planets or transport resources. The game becomes multi-planet.

## Issues

### 13. Fleet assembly & ship roster
**Labels:** `domain:fleets`

Ships on a planet can be grouped into fleets.

**Scope:**
- Planet maintains a ship roster: list of ships (ID, type) stationed there
- `AssembleFleet` command: select ships from roster → create fleet
- Fleet tracks composition, current location (planet), status (stationed/in-transit)
- Ships removed from planet roster when assigned to a fleet
- Fleet can be disbanded: ships returned to planet roster
- `POST /api/planets/{planetId}/fleets` — assemble fleet
- `POST /api/fleets/{fleetId}/disband` — disband fleet
- Events: `FleetAssembled`, `FleetDisbanded`, `ShipsRemovedFromRoster`, `ShipsAddedToRoster`
- Integration test: build ships → assemble fleet → verify roster updated → disband → verify ships returned

**Depends on:** #12 (ships exist on roster)

---

### 14. Fleet travel
**Labels:** `domain:fleets`

Fleets travel through 3D space from one planet to another.

**Scope:**
- Fleet mission modeled as Wolverine Saga
- Travel time = 3D distance / fleet speed (slowest ship)
- `FleetArrived` as `TimeoutMessage` scheduled at departure + travel time
- Fleet status changes: stationed → in-transit → arrived
- Track departure time, origin, destination, ETA on the saga
- `GET /api/fleets/{fleetId}` — fleet status (location, ETA if in transit)
- No mission execution yet — fleet just arrives and ships go to destination roster
- Events: `FleetDeparted`, `FleetArrived`
- Integration test: send fleet → verify in-transit status → verify arrival at destination → verify ships on destination roster

**Depends on:** #13 (fleet assembly)

---

### 15. Fleet missions — Colonize, Transport, Move
**Labels:** `domain:fleets`

Fleets do things when they arrive.

**Scope:**
- **Move** (simplest): ships relocate to destination roster. Target: any planet.
- **Colonize**: target must be uncolonized. One Colony Ship consumed, planet claimed (reuse `PlanetColonized` event), cargo auto-unloaded, remaining ships to roster. If already taken → colony ship preserved, fleet idles.
- **Transport**: target must be own planet. Cargo unloaded into storage. Ships to roster.
- Arrival handler dispatches to mission-specific logic based on saga state
- Events: `ColonizationSucceeded`, `ColonizationFailed`, `CargoUnloaded`
- `POST /api/fleets/{fleetId}/missions` — send on mission (type, destination)
- Integration test per mission type

**Depends on:** #14 (fleet travel)

---

### 16. Cargo loading & unloading
**Labels:** `domain:fleets`, `domain:resources`

Resources can be physically moved between planets.

**Scope:**
- Cargo loading during fleet assembly: specify resource amounts to load
- Resources removed from planetary storage on departure
- Cargo distributed across Cargo Vessels by tonnage (mixed resource types allowed)
- Fleet cargo capacity = sum of individual Cargo Vessel capacities
- On Transport/Colonize arrival: auto-unload into destination storage
- Partial unload if destination storage full (fill to capacity, rest stays on ships)
- `POST /api/fleets/{fleetId}/unload` — manual unload for stationed fleets
- Events: `CargoLoaded`, `CargoUnloaded`, `PartialCargoUnloaded`
- Integration test: load cargo → transport → verify resources moved between planets

**Depends on:** #15 (transport mission), #7 (lazy calc for storage values)

---

## Phase Completion

- Player can assemble ships into a fleet and send it to another planet
- Colonize mission claims an uncolonized planet, consuming the Colony Ship
- Transport mission delivers resources to another owned planet
- Move mission relocates ships
- Cargo loading/unloading works with partial unload on full storage
- The game is multi-planet: build up economy → build ships → expand → build up new colony
