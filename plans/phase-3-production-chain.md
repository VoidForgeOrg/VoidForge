# Phase 3 — Production Chain

**Goal:** The full production chain works: generators produce energy, buildings consume it, refineries convert ore into ingots, shipyards build ships. Construction costs resources over time. The economy ticks.

## Issues

### 9. Energy grid
**Labels:** `domain:buildings`, `domain:resources`

Generators produce energy, buildings consume it. Overload degrades productivity.

**Scope:**
- Generators produce MW (configurable per generator)
- Drills, Refineries, Shipyards consume MW
- Planet tracks total generation vs total consumption
- Normal: consumption <= generation → 100% productivity
- Overload: consumption > generation → productivity multiplier = `generation / consumption`
- Productivity multiplier scales all building rates on the planet
- Recalculate energy balance when buildings are added/removed
- Starting homeworld: generator output covers starting buildings with headroom
- Integration test: add buildings until overloaded → verify rates drop proportionally

**Depends on:** #8 (buildings exist)

---

### 10. Refinery — ore to ingots
**Labels:** `domain:buildings`, `domain:resources`

Refineries consume Iron Ore and produce Iron Ingots at a 1:2 ratio.

**Scope:**
- Refinery consumes Iron Ore at a configurable rate, produces Iron Ingots at 2x that rate
- Iron Ingots resource pool on planet (add to planet state if not already present)
- When a Refinery is placed: update ore consumption rate (negative) and ingot production rate (positive)
- Even-split distribution: multiple refineries share available ore equally
- Homeworld refinery now functional: ore is consumed, ingots are produced
- Integration test: place refinery → verify ore decreasing and ingots increasing over time

**Depends on:** #8 (buildings), #9 (energy affects rates)

---

### 11. Building construction with resource cost
**Labels:** `domain:buildings`, `domain:resources`

Buildings are no longer instant — they cost Iron Ingots consumed over time.

**Scope:**
- Construction consumes Iron Ingots continuously at `total_cost / build_duration` rate
- Building occupies a slot immediately, status = `UnderConstruction`
- Schedule `BuildingCompleted` event via Wolverine `ScheduleAsync()`
- On completion: status → `Operational`, energy consumption begins, production rates recalculate
- Construction halts if ingots run out, resumes when available (adjust completion time)
- Update `POST /api/planets/{planetId}/buildings` to require ingots and take time
- Events: `BuildingConstructionStarted`, `BuildingCompleted`
- Integration test: start building → verify ingot consumption → verify completion fires → verify building becomes operational

**Depends on:** #10 (ingots need to exist as a resource), #9 (energy on completion)

---

### 12. Shipyard & ship construction
**Labels:** `domain:buildings`, `domain:fleets`

Shipyards build ships from ingots.

**Scope:**
- `ShipType` enum: `ColonyShip`, `CargoVessel`
- Ship construction consumes Iron Ingots continuously over build duration
- Shipyard supports up to 3 parallel builds, unlimited queue
- Queued ships auto-start as slots free up
- Completed ships added to planet's ship roster (list of ShipType + ID on planet state)
- Shipyard energy: full draw when building, 5% when idle
- `POST /api/planets/{planetId}/shipyards/{slotIndex}/queue` — queue a ship build
- Events: `ShipConstructionQueued`, `ShipConstructionStarted`, `ShipCompleted`
- Integration test: queue ships → verify ingot consumption → verify ships appear on roster

**Depends on:** #11 (construction model), #9 (energy)

---

## Phase Completion

- Energy grid works: generators power buildings, overload reduces productivity
- Refineries convert ore → ingots at 1:2 ratio
- Building construction consumes ingots over time and completes on schedule
- Shipyards build ships (Colony Ship, Cargo Vessel) and add them to the roster
- The full chain is visible: ore extracted → refined into ingots → consumed by construction/shipyard → ships produced
