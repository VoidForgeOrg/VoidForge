# #72 — Building Cancellation & Demolition Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development — fresh subagent per task, review between.

**Goal:** Players can cancel in-progress construction (no refund, slot freed immediately) and demolish completed buildings (immediate shutdown, timed teardown, slot freed on completion). Both re-derive rates/energy — demolition's "energy freed → overload resolves" cascade resolves inside `RebaseRates`.

**Architecture (D7-D9):** the append-only `Buildings` list stays append-only; cancelled/demolished buildings become **tombstones** in place, so `SlotIndex = Buildings.Count` stays a stable monotonic identifier and in-flight `CompleteBuildingConstruction` messages find the tombstone and no-op. Because the whole energy/rate/halting engine already filters on `Status`, new tombstone statuses drop out of production/generation/consumption **for free** — only the free-slot check and the read model need active changes.

**Tech Stack:** .NET 9, Marten, Wolverine durable messages, xUnit + Alba.

**Spec:** `plans/phase-5-hardening-design.md` §5, D7-D9.

## Global Constraints
- `TreatWarningsAsErrors`; MA0048/MA0051. Branch `feat/72-building-cancel-demolition` off `phase-5` (after #73 merges). Commits suffixed `(#72)`.
- BOTH `dotnet build -warnaserror` AND `dotnet format --verify-no-changes`. No local `dotnet test`.

## Plan-level decisions (from survey)
1. **Three new `BuildingStatus` values: `Cancelled`, `Demolishing`, `Demolished`.** A status that is none of `Operational`/`Halted`/`UnderConstruction` yields zero generation/draw/production automatically — no engine edits. **Critical:** none may equal `Halted` (that branch applies the 5% draw — a tombstone must draw nothing). `Demolishing` = mid-teardown (occupies a slot, draws/produces nothing); `Cancelled`/`Demolished` = terminal tombstones (free the slot).
2. **Free-slot invariant becomes `count(status ∉ {Cancelled, Demolished}) < BuildingSlotCount`** — `Demolishing` still counts as occupied. Two sites: `Planet.Buildings.cs:13` (`PlaceBuilding`) and `:33` (`StartConstruction`). `SlotIndex: Buildings.Count` (`:40`) stays a raw length (monotonic id).
3. **`PlanetResponse` exposes all slots including tombstones** (position = `SlotIndex`, so clients can address `.../buildings/{slotIndex}/...`). Tombstones appear with `Cancelled`/`Demolished` status; clients compute free slots by filtering. Update `BuildingEndpointTests.PlaceBuildingInOccupiedSlotsReturns409` (it computes `BuildingSlotCount - Buildings.Count`, which tombstones break).
4. **Resume-on-cancel (D8's "un-halt a starved neighbor") is DEFERRED to #83.** In #72's world there is nothing a cancel resumes: freeing an *ingot* construction drain doesn't lower a *current* pool value vs. capacity (what the resume evaluators test), and ingot-*starvation* halting doesn't exist until #83. So cancel/demolish call `RebaseRates` (via their `Apply`s) for the rate/energy cascade — which IS the D9 "energy freed → overload resolves" cascade — but wire NO explicit `EvaluateStorageResumes` hook. Documented; #83 adds it alongside the starvation it would resume.
5. **204/202 responses** (no existing endpoint returns these); ownership via a helper mirroring `ShipEndpoints.IsOwner` (D11 consolidation is #74).

## Task 1 — Enum, tombstone free-slot, cancel construction (domain + endpoint + tests)
**Files:** `Domain/BuildingStatus.cs`, `Domain/Planet.Buildings.cs` (free-slot check + `CancelConstruction` + `Apply(BuildingConstructionCancelled)`), `Domain/Events/BuildingConstructionCancelled.cs` (new), `Endpoints/BuildingEndpoints.cs` (DELETE endpoint), tests.
- Add `Cancelled`, `Demolishing`, `Demolished` to `BuildingStatus` (comments clarifying tombstone semantics + the "never equals Halted" rule).
- A private `LiveBuildingCount()` / non-tombstone predicate; replace both `Buildings.Count >= BuildingSlotCount` checks with it.
- `BuildingConstructionCancelled(int SlotIndex, DateTimeOffset At)` event.
- `Planet.CancelConstruction(int slotIndex, DateTimeOffset at)`: no-op `[]` unless `Buildings[slotIndex].Status == UnderConstruction` (validate); else return `[BuildingConstructionCancelled(slotIndex, at)]`.
- `Apply(BuildingConstructionCancelled)`: set slot `Status = Cancelled`, clear `CompletesAt`/`ConstructionDrainPerSecond` (drain drops out of `RebaseRates`), `RebaseRates(at)`.
- `DELETE /api/planets/{planetId}/buildings/{slotIndex}/construction` → **204**; guards: 403 not owner, 404 unknown planet/slot, 409 wrong state (slot not `UnderConstruction`). After commit, `ScheduleAllChecksAsync` (rates changed).
- Tests: pure-domain — cancel frees the slot (a new placement gets `Buildings.Count`, never the cancelled index — **SlotIndex-never-reused**); stale `CompleteBuildingConstruction` no-ops on the `Cancelled` tombstone; ingot drain drops on cancel. Endpoint — 204/403/404/409.
- Build + format. Commit: `feat: cancel construction — tombstone slot, no refund, stable SlotIndex (#72)`.

## Task 2 — Demolition (two-step: events, message, handler, endpoint)
**Files:** `Domain/Events/BuildingDemolitionStarted.cs`, `BuildingDemolished.cs`, `CompleteBuildingDemolition.cs` (new), `Domain/BuildingSpecs.cs` (`DemolitionDurationSeconds`), `Domain/Planet.Buildings.cs` (`StartDemolition` + `CompleteDemolition` + Applys), `Endpoints/BuildingEndpoints.cs` (POST) + `Endpoints/CompleteBuildingDemolitionHandler.cs` (new), tests.
- `BuildingSpecs.DemolitionDurationSeconds` placeholder (next to the other consts).
- `Planet.StartDemolition(slotIndex, now, durationSeconds)`: no-op `[]` unless status ∈ {`Operational`, `Halted`} (can't demolish under-construction/already-demolishing/tombstone); else `[BuildingDemolitionStarted(slotIndex, now, now+duration)]`.
- `Apply(BuildingDemolitionStarted)`: status `Demolishing`, `CompletesAt = @event.CompletesAt`, `RebaseRates(at)` (immediate shutdown → the "energy freed → overload resolves" cascade resolves here).
- `Planet.CompleteDemolition(slotIndex, at)`: validate-on-arrival — `[]` unless `Status == Demolishing && CompletesAt == at`; else `[BuildingDemolished(slotIndex, at)]`.
- `Apply(BuildingDemolished)`: status `Demolished` (terminal tombstone, frees slot), clear `CompletesAt`, `RebaseRates(at)`.
- `CompleteBuildingDemolition(Guid PlanetId, int SlotIndex, DateTimeOffset CompletesAt)` message + handler (mirror `CompleteBuildingConstructionHandler`: FetchForWriting → `CompleteDemolition` → AppendMany → SaveChanges → FetchLatest → `ScheduleAllChecksAsync`).
- `POST /api/planets/{planetId}/buildings/{slotIndex}/demolish` → **202**; append `BuildingDemolitionStarted`, `ScheduleAsync(CompleteBuildingDemolition, completesAt)`, `SaveChangesAsync`, `ScheduleAllChecksAsync`. Guards: 403/404/409 (409 if slot not Operational/Halted).
- Tests: demolish shuts the building down immediately (energy freed — an overloaded planet's multiplier recovers in the same commit, mirror `PlanetEnergyTests` overload style); scheduled `CompleteBuildingDemolition` (invoked directly, deterministic) tombstones the slot and frees it; no cancel-of-demolition.
- Build + format. Commit: `feat: two-step demolition — immediate shutdown, timed teardown, slot freed (#72)`.

## Task 3 — Read model + docs + PR
**Files:** `Endpoints/PlanetResponse.cs`/`BuildingSlotResponse.cs` (tombstone exposure), `Tests/Buildings/BuildingEndpointTests.cs` (fix the free-slot computation), `technical-design/domain-model.md`.
- Confirm `PlanetResponse` exposes all slots (position = SlotIndex); tombstones show `Cancelled`/`Demolished`. Fix `PlaceBuildingInOccupiedSlotsReturns409`'s free-slot math to count non-tombstones.
- Docs: tombstone slot model, the three new statuses, cancel/demolition events + the `CompleteBuildingDemolition` scheduled message, the free-slot redefinition, the resume-on-cancel deferral to #83.
- Build + format. Commit: `docs: tombstone slot model + cancel/demolition in domain-model (#72)`.
- PR → `phase-5`, "Closes #72". Self-merge on green CI.

## Hardest decisions (flag for review)
1. **Tombstone status set + the "never equals Halted" rule** — drives energy correctness, the 409 guards, the free-slot count, and the read model at once.
2. **Auditing every `Buildings.Count`/iteration** (survey's table) — only the two free-slot checks and `PlanetResponse` need changes; everything else is status-filtered and correct for free, but confirm each.
3. **SlotIndex-never-reused** is the load-bearing invariant — the test proving a new placement never reuses a tombstoned index is the key regression guard.
