# Phase 5 — Hardening

**Goal:** All edge cases, cancellation flows, cascading events, and scoring are implemented. The MVP is feature-complete and robust.

## Issues

### 17. Storage caps & halting
**Labels:** `domain:resources`

When storage is full, production halts. When it frees up, production resumes.

**Scope:**
- When a resource reaches storage capacity, halt the producing building
- Halted buildings consume 5% of normal energy draw
- Schedule `StorageFull` event based on `(capacity - current) / rate`
- When storage frees up (consumption or transport), resume halted buildings
- Resuming triggers rate recalculation and rescheduling
- Validate-on-arrival pattern for stale `StorageFull` messages
- Integration test: let storage fill → building halts → consume resources → building resumes

**Depends on:** #10 (refinery consumes ore, creating the scenario)

---

### 18. Resource depletion
**Labels:** `domain:resources`

Ore pools are finite. When depleted, drills stop and the economy cascades.

**Scope:**
- Schedule `ResourceDepleted` event based on `pool_remaining / extraction_rate`
- On depletion: halt all Drills mining that resource
- Downstream: Refineries may halt (no ore input)
- Reschedule depletion when extraction rate changes
- Stale depletion messages handled via validate-on-arrival
- Integration test: deplete ore pool → drills halt → refinery halts

**Depends on:** #17 (halting mechanics)

---

### 19. Cascading event resolution
**Labels:** `domain:core`

Chain reactions resolve atomically within a single checkpoint.

**Scope:**
- Deterministic resolution order: trigger → building states → energy balance → production rates → checkpoint → schedule future events
- All cascade events resolved in one `SaveChangesAsync()`
- Test all scenarios from `engine.md`:
  - Ore depletion → drill halts → energy freed → overload resolves → productivity recovers
  - Ore storage empties → refinery halts → ingot production stops → shipyard halts
  - New building online → energy increases → planet overloads → productivity drops
  - Building demolished → energy freed → overload resolves
- Edge cases: multiple simultaneous depletions, all buildings halted

**Depends on:** #17 (halting), #18 (depletion), #9 (energy)

---

### 20. Building cancellation & demolition
**Labels:** `domain:buildings`

Players can cancel in-progress construction or tear down completed buildings.

**Scope:**
- **Cancel construction:** no refund, slot freed immediately. Scheduled `BuildingCompleted` becomes stale (validate-on-arrival).
- **Demolish building:** stops functioning immediately, demolition takes time, slot freed on completion.
- Both trigger energy rebalance and rate recalculation (cascading)
- `DELETE /api/planets/{planetId}/buildings/{slotIndex}/construction` — cancel
- `POST /api/planets/{planetId}/buildings/{slotIndex}/demolish` — demolish
- Events: `BuildingConstructionCancelled`, `BuildingDemolitionStarted`, `BuildingDemolished`

**Depends on:** #11 (construction), #19 (cascading)

---

### 21. Ship & fleet cancellation
**Labels:** `domain:fleets`

Individual ship builds can be cancelled. Fleets in transit can be recalled.

**Scope:**
- **Cancel ship build:** no refund, parallel slot freed, next queued ship starts
- **Cancel fleet in transit:** fleet turns around, return time = time already traveled. New return saga, original saga completed. Stale arrival → `NotFound` handler.
- `DELETE /api/planets/{planetId}/shipyards/{slotIndex}/queue/{shipBuildId}` — cancel ship
- `POST /api/fleets/{fleetId}/cancel` — cancel fleet in transit
- Events: `ShipConstructionCancelled`, `FleetCancelled`, `FleetReturning`, `FleetReturnedToOrigin`

**Depends on:** #12 (ship construction), #14 (fleet travel)

---

### 22. Resource distribution — even split
**Labels:** `domain:resources`

When multiple buildings compete for the same resource, supply is split evenly.

**Scope:**
- Multiple Refineries sharing ore: each gets `available_ore_rate / refinery_count`
- Shipyard + building construction sharing ingots: even split
- Rate recalculation whenever a consumer is added/removed/halted/resumed
- Integration test: 2 refineries with insufficient ore → each operates at reduced throughput

**Depends on:** #10 (refineries), #11 (construction consumes ingots)

---

### 23. Scoring & leaderboard
**Labels:** `domain:players`, `api`

Player score from all owned assets.

**Scope:**
- Score inputs: planets, buildings (incl. under construction), ships (incl. in-transit, under construction), resources (incl. in-transit)
- Point values per asset type (configurable)
- Score computed on demand (lazy calculation)
- `Leaderboard` as Marten async projection
- `GET /api/players/me` includes score
- `GET /api/leaderboard` — ranked player list
- Integration test: build assets → verify score reflects everything

**Depends on:** #16 (in-transit resources), #12 (under-construction ships)

---

### 24. API polish & documentation
**Labels:** `api`

Complete and consistent API surface.

**Scope:**
- Review all endpoints for consistent naming, response shapes, error handling
- Validate ownership and permissions on all mutation endpoints
- OpenAPI documentation complete via Swashbuckle
- Proper HTTP status codes: 400 (validation), 401 (auth), 403 (not your planet), 404 (not found), 409 (conflict, e.g., slot occupied)
- End-to-end integration test: register → build economy → construct ships → colonize → transport resources → verify score

**Depends on:** All previous issues

---

## Phase Completion

- Storage caps halt producers, resuming when storage frees up
- Ore depletion halts drills and cascades through the economy
- All cascade scenarios resolve atomically
- Cancellation works for buildings, ships, and fleets
- Resource distribution is fair (even split)
- Scoring reflects all owned assets
- API is complete, consistent, and documented
- Full end-to-end gameplay loop works
