# Phase 4 — Fleets & Expansion

**Goal:** Players can assemble ships into fleets, send them across space, and colonize new planets or transport resources. The game becomes multi-planet.

**Design spec:** [`phase-4-fleets-expansion-design.md`](phase-4-fleets-expansion-design.md) — full decisions, events catalog, API surface, balance placeholders.

**Tracking:** Epic [#52](https://github.com/VoidForgeOrg/VoidForge/issues/52) · Issues [#48](https://github.com/VoidForgeOrg/VoidForge/issues/48), [#49](https://github.com/VoidForgeOrg/VoidForge/issues/49), [#50](https://github.com/VoidForgeOrg/VoidForge/issues/50), [#51](https://github.com/VoidForgeOrg/VoidForge/issues/51), prep [#40](https://github.com/VoidForgeOrg/VoidForge/issues/40)

> Dependency order: **#40 → #48 → #49 → { #50, #51 }** (#50 and #51 are independent of each other).

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

### #48 — Fleet assembly & disband
**Labels:** `domain:fleets`

Ships on a planet can be grouped into fleets.

**Scope:**
- `Fleet` aggregate (`Id`, `OwnerId`, `Status`, `LocationPlanetId`, `Ships`), `FleetShip`, inline snapshot registration
- `RosterShip` gains `OwnerId`; assembly validates ship ownership, not planet ownership
- Assemble from the planet roster; disband returns ships (refused while cargo is aboard — cargo arrives in #50)
- `POST /api/planets/{planetId}/fleets`, `POST /api/fleets/{fleetId}/disband`
- `GET /api/fleets`, `GET /api/fleets/{fleetId}`, `GET /api/planets/{planetId}/fleets` (paginated per #29)
- Fleet events: `FleetAssembled`, `FleetDisbanded`. Planet events: `ShipsRemovedFromRoster`, `ShipsAddedToRoster`
- Updates `game-design/fleets.md` for the always-stationed arrival rule
- Integration test: build ships → assemble → roster shrinks → disband → ships returned

**Depends on:** [#40](https://github.com/VoidForgeOrg/VoidForge/issues/40) (prep), #27 (ships exist on the roster)

---

### #49 — Planet coordinates, travel & the Move mission
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

**Depends on:** #48

---

### #50 — Cargo & the Transport mission
**Labels:** `domain:fleets`, `domain:resources`

Resources move physically between planets.

**Scope:**
- Cargo on assembly: capacity check (Σ Cargo Vessel capacity) and storage withdrawal
- Storage mutation events checkpoint the pool (#44 semantics) and adjust the checkpoint value without touching rates
- Transport mission: auto-unload on arrival, capped by destination headroom; remainder stays aboard
- `POST /api/fleets/{fleetId}/unload` for a stationed fleet at an owned planet
- Fleet events: `CargoLoaded`, `CargoUnloaded`. Planet events: `CargoLoadedFromStorage`, `CargoDeliveredToStorage`
- Integration test: load → transport → resources moved; full-storage run leaves a remainder aboard

**Depends on:** #49

---

### #51 — Colonize mission (closes #19)
**Labels:** `domain:fleets`, `domain:planets`, `bug`

Fleets claim uncolonized planets.

**Scope:**
- Guarded claim: `FetchForWriting<Planet>` + null-owner assertion, losers retried by the #39 policy
- Success: `PlanetColonized` with zero starting stores, one Colony Ship consumed, cargo auto-unloaded
- Failure (already taken): `ColonizationFailed`, Colony Ship preserved, fleet idles at the planet
- Registration's homeworld assignment moves onto the same guarded claim with a bounded re-pick retry — closes #19
- Fleet events: `ColonyShipConsumed`, `ColonizationFailed`
- Integration tests: colonize an empty planet; two fleets racing one planet; concurrent registrations

**Depends on:** #49. Independent of #50 — if #51 merges first, its cargo auto-unload step is inert until #50 lands.

---

## Phase Completion

- Player can assemble ships into a fleet and send it to another planet
- Colonize claims an uncolonized planet, consuming one Colony Ship; a lost race fails cleanly
- Transport delivers resources to another owned planet, with partial unload when storage is full
- Move relocates ships
- The game is multi-planet: build up economy → build ships → expand → build up new colony

## Execution workflow

Settled 2026-07-26, before implementation started; mirrors Phase 3.

- **Integration branch:** `phase-4` off `main`. One feature branch + PR per issue, targeting `phase-4`; self-merged on green CI. The phase ends with a single PR `phase-4` → `main` closing epic #52.
- **Branches, in order:** `feat/planet-partial-split` (#40), `feat/fleet-assembly` (#48), `feat/fleet-travel` (#49), `feat/fleet-cargo` (#50), `feat/fleet-colonize` (#51). Strictly sequential — #50/#51 are design-independent but share `Fleet` and the arrival mission dispatch, so parallel branches would conflict.
- **Just-in-time plans:** each issue's implementation plan is written against the codebase as it exists after the previous merge, and lands as the first commit on its feature branch under `plans/phase-4/<issue>-<slug>.md`.
- **Per-PR gates:** the §8 merge-gate test, full `dotnet test` green, `dotnet format` clean, a code-review pass on the diff, CI green.
- **Docs owned by each PR:** `domain-model.md` (every PR); `fleets.md` D6 update (#48); `architecture.md` §4 supersession (#49).
- **Reporting:** no per-PR check-ins; report at phase end with the full loop demonstrated (economy → ships → expand → new colony). Interruptions only for genuine spec ambiguity or scope changes.
