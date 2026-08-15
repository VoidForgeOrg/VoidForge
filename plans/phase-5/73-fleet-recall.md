# #73 — Fleet Recall Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development — fresh subagent per task, review between.

**Goal:** A fleet in transit can be recalled: it turns around and returns to its origin in exactly the time it has already traveled, arriving `Stationed` with cargo and colony ship intact. Folds in bug fixes #60 (colonize-in-place) and #58 (flaky concurrent-disband test).

**Architecture (D10, already prescribed by `architecture.md` §306-314):** recall is a single `FleetRecalled` event carrying a synthesized return `TravelPlan`, plus an ordinary scheduled arrival. The originally-scheduled `CompleteFleetArrival(fleetId, oldArrivesAt)` goes stale and no-ops via the existing validate-on-arrival guard (`Fleet.Arrive` checks `ArrivesAt == at`); the freshly-scheduled arrival at the new time fires the return. No outbox cancellation (ADR 0001).

**Tech Stack:** .NET 9, Marten, Wolverine durable messages, xUnit + Alba.

**Spec:** `plans/phase-5-hardening-design.md` §6, D10.

## Global Constraints
- `TreatWarningsAsErrors`; MA0048/MA0051. Branch `feat/73-fleet-recall` off `phase-5`. Commits suffixed `(#73)`.
- Verify locally with BOTH `dotnet build -warnaserror` AND `dotnet format --verify-no-changes`. Do NOT run `dotnet test` (shared DB; CI verifies).

## Plan-level decisions (from survey)
1. **"Already returning" 409 guard needs a new marker.** After recall the fleet is `InTransit` with `Mission = Move` heading to its old origin — indistinguishable from a plain outbound Move on the snapshot. Add `DateTimeOffset? RecalledAt` to the `Fleet` snapshot, set by `Apply(FleetRecalled)`; recall is 409 when it's already set (and 409 when `Stationed`). Old snapshots deserialize `null` (test/dev worlds reseed). Surface it on `FleetResponse` (a recalled fleet is a meaningful client state).
2. **Return `TravelPlan` is synthesized directly, not via the coordinate planner** (an in-transit fleet has no live position): `ArrivesAt = now + (now − DepartedAt)`, `TotalDistance` = the outbound plan's `TotalDistance` (cosmetic), one `Leg` to the origin. Carried on `FleetRecalled` so replay is deterministic (mirrors `FleetDeparted.Plan`).
3. **`Apply(FleetRecalled)` sets `Mission = Move`** so the arrival handler's `Transport`/`Colonize` branches are skipped — no cargo delivery, no colonize claim on the return; `DestinationPlanetId = OriginPlanetId` so `FleetArrived` stations at origin; `Ships`/`CargoIronOre`/`CargoIronIngot` untouched (cargo + colony ship survive).
4. **#58 fix is a deterministic-arrangement change, not suppression** (per the issue). The flake is stream contention between the arrangement's scheduled ship-construction completion and the disbands' planet-stream appends. Settle the planet stream to idle before the race; if investigation reveals a genuine domain race (stale `OwnerId` under Quick-append + retry), flag it for a separate follow-up rather than forcing a fix.

## Task 1 — Domain: `FleetRecalled` event, Apply, return-plan synthesis, marker (unit-tested)
**Files:** `Domain/Events/FleetRecalled.cs` (new), `Domain/Fleet.cs` (Recall method + Apply + `RecalledAt` field), `Domain/Fleet.Recall` return-plan helper; test `Tests/Travel/FleetTravelDomainTests.cs`.
- Add `DateTimeOffset? RecalledAt` to `Fleet`.
- `FleetRecalled(TravelPlan ReturnPlan, DateTimeOffset RecalledAt)` event.
- `Fleet.Recall(DateTimeOffset now)`: guard `Status == InTransit` and `RecalledAt is null` (else throw — the endpoint pre-checks and returns 409, mirroring `Depart`/`Disband`); synthesize the return plan (`ArrivesAt = now + (now − DepartedAt)`, dest leg = `OriginPlanetId`); return `FleetRecalled(returnPlan, now)`.
- `Apply(FleetRecalled)`: `DestinationPlanetId = OriginPlanetId; OriginPlanetId = <the current destination>` (the fleet now travels from where it was heading back to where it started — set `DestinationPlanetId` to the original origin, and it's fine to leave/clear `OriginPlanetId` since arrival clears everything); `Mission = MissionType.Move; DepartedAt = RecalledAt; ArrivesAt = ReturnPlan.ArrivesAt; TravelPlan = ReturnPlan; RecalledAt = @event.RecalledAt`; keep `Status = InTransit`; do NOT touch `Ships`/cargo.
- Unit tests (mirror `FleetTravelDomainTests` `Depart`/`Arrive` tests): `RecallProducesReturnPlanHeadingToOriginKeepsInTransit` (asserts return `ArrivesAt == now + elapsed`, `DestinationPlanetId == origin`, `Mission == Move`, `RecalledAt` set, cargo/ships intact); `RecalledArrivalStationsAtOriginWithCargoIntact` (apply `FleetRecalled` then `FleetArrived` → `Stationed` at origin, cargo/ship preserved); `RecallOfStationedOrAlreadyRecalledThrows`.
- Build + format. Commit: `feat: FleetRecalled domain — return plan, in-transit-to-origin, marker (#73)`.

## Task 2 — Recall endpoint + #60 colonize-in-place fix (integration)
**Files:** `Endpoints/FleetEndpoints.cs` (new `Cancel` endpoint + the #60 guard relaxation), `Endpoints/FleetResponse.cs` (surface `RecalledAt`), `Tests/Support/IntegrationApiExtensions.cs` (`Recall`/`CancelForStatus` helpers), integration tests.
- `POST /api/fleets/{fleetId}/cancel` (mirror `Disband`'s typed union + `IMessageBus bus`): `FetchForWriting<Fleet>` → 404 if null → `PlayerId != OwnerId` → 403 → 409 if `Status != InTransit` OR `RecalledAt is not null` ("already returning") → append `fleet.Recall(now)` → `await bus.ScheduleAsync(new CompleteFleetArrival(fleetId, returnPlan.ArrivesAt), returnPlan.ArrivesAt)` → `SaveChangesAsync` → `FetchLatest` → `Ok(FleetResponse.From(...))`. Touches only the Fleet stream.
- **#60 fix:** in `Launch`, gate the same-destination 400 (`request.DestinationPlanetId == fleet.LocationPlanetId`) on `request.Mission != MissionType.Colonize` — colonize-in-place is allowed (zero-distance plan → immediate arrival → guarded claim). Update `FleetMissionEndpointTests.LaunchToCurrentLocationReturns400` and add a Colonize-in-place success case.
- Add `_host.Recall(...)`/`CancelForStatus(...)` to the shared helpers (mirror `Disband`/`TryDisband`).
- Integration tests (mirror `FleetMissionEndpointTests` + the `LaunchAndArriveInstantly` technique): recall an in-transit fleet → return arrives at origin (invoke `CompleteFleetArrivalHandler.Handle` with the return `ArrivesAt`) `Stationed` with cargo intact; the STALE outbound `CompleteFleetArrival(oldArrivesAt)` invoked directly → no-op; 409 on recalling a `Stationed` fleet and on a second recall; 403/404 cases.
- Build + format. Commit: `feat: POST /api/fleets/{id}/cancel recall + colonize-in-place (#60) (#73)`.

## Task 3 — #58 deterministic rework + docs + PR
**Files:** `Tests/Concurrency/FleetConcurrencyTests.cs` (#58), `technical-design/architecture.md` (mark §306-314 implemented) / `game-design/fleets.md` (recall rule if wording drifts).
- Investigate `ConcurrentDisbandsOfTheSameFleetYieldExactlyOneSuccess`: the arrangement (`EnsureOperationalShipyard`/`BuildRosterShip`) drives scheduled ship-construction completions on the same planet stream the disbands append to. Make the arrangement deterministic — ensure the planet stream is idle (ship build fully completed AND no in-flight scheduled work) before the two concurrent disbands fire, so the only contention is the intended disband race. If the 403 root cause is a genuine domain race (`Apply(ShipCompleted)` reading a stale `OwnerId` under Quick-append + retry), do NOT paper over it — document it and file a follow-up. Report which it was.
- Docs: mark `architecture.md` §306-314 recall as implemented; update `fleets.md` if needed.
- Build + format. Commit: `test+docs: deterministic concurrent-disband arrangement (#58); recall docs (#73)`.
- PR `feat/73-fleet-recall` → `phase-5`, "Closes #73, #60, #58". Self-merge on green CI.

## Hardest decisions (flag for review)
1. The `RecalledAt` marker is the one new snapshot field — it drives the 409 guard, the Apply, and the DTO.
2. Return-plan synthesis bypasses `ITravelPlanner` (no live mid-flight position) — must be deterministic and carried on the event.
3. #58 may be an arrangement fix OR a real domain race — investigate before deciding; don't suppress.
