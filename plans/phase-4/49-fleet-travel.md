# #49 — Planet Coordinates, Fleet Travel & the Move Mission Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fleets travel through 3D space between coordinate-bearing planets and arrive on schedule; the Move mission works end to end.

**Architecture:** Planets gain per-planet `X/Y/Z` (spec D5) seeded around their system centre. Travel goes through the `ITravelPlanner` seam (D3/D4) whose `TravelPlan` is recorded on `FleetDeparted`; arrival is a durable scheduled `CompleteFleetArrival` message resolved by a thin handler calling pure `Fleet.Arrive` with validate-on-arrival (ADR 0001, D2 — supersedes `architecture.md` §4's Saga sketch). Move relocation leaves the fleet `Stationed` at the destination (D6).

**Tech Stack:** .NET 9, Marten (event sourcing, inline snapshots, transactional outbox), Wolverine (HTTP + durable scheduled messages), xUnit + Alba.

**Spec:** `plans/phase-4-fleets-expansion-design.md` §2.1–§2.3, §2.5, §3–§6. Carried-over gate from #48's final review: the fleet-path race test (Task 1) **must merge with this PR**.

## Global Constraints

- `TreatWarningsAsErrors` on; MA0048 one public type per file. Test-class private static readonly fields use `_`-prefixed names (`_t0`, not `T0`) — IDE1006 fails `dotnet format --verify-no-changes` otherwise.
- Every existing-stream append site uses `session.Events.FetchForWriting<T>(id)` (#39). Launch appends to the **Fleet stream only** (spec §2.3 — ships left the roster at assembly).
- Balance placeholders (spec §6): ColonyShip 0.05 units/s, CargoVessel 0.10 units/s. Config-backed via `IOptions<BalanceOptions>`; domain stays config-free.
- Mutations 403 without ownership; reads universe-visible. Error codes per spec §5.
- TDD per task; full `dotnet test` + `dotnet format --verify-no-changes` green before the PR.
- Commits: conventional, suffixed `(#49)`.

## Plan-level decisions (within spec letter/spirit)

1. **`Fleet.GetSpeed(Func<ShipType, decimal> speedOf)`** instead of §2.1's parameterless sketch — `FleetShip` carries no speed, and injecting the lookup keeps the aggregate config-free (Phase 3 D10 principle). The launch endpoint passes `t => balance.Ships.For(t).SpeedPerSecond`.
2. **Ship stats nest under `BalanceOptions.Ships`** (`Balance__Ships__ColonyShip__SpeedPerSecond` in config) because the top-level `ColonyShip`/`CargoVessel` names are taken by `ConstructionBalance`.
3. **`MissionType { Move, Transport, Colonize }` is defined complete now** (spec-fixed vocabulary); the launch endpoint accepts only `Move` in #49 and returns 400 `"Mission not supported yet."` for the other two — #50/#51 delete that guard branch as they wire each mission.
4. **Launch rejects `destinationPlanetId == fleet.LocationPlanetId`** with 400 (zero-length trip; spec is silent, and a no-op InTransit fleet would be a trap).
5. **Arrival handler appends to the Fleet stream only in #49** — Move adds nothing on arrival (spec §2.4); the first cross-aggregate arrival append arrives with #50/#51.

## File Structure

```
src/Voidforge.Api/Domain/
  Coordinates.cs                      (new record)
  MissionType.cs                      (new enum)
  Planet.cs                           (modify — X/Y/Z snapshot fields + Apply(PlanetCreated))
  Fleet.cs                            (modify — transit block, GetSpeed, Depart, Arrive, Applys)
  Events/PlanetCreated.cs             (modify — gains X, Y, Z)
  Events/FleetDeparted.cs             (new)
  Events/FleetArrived.cs              (new)
  Events/CompleteFleetArrival.cs      (new scheduled command)
src/Voidforge.Api/Travel/
  ITravelPlanner.cs / TravelPlan.cs / TravelLeg.cs / LinearTravelPlanner.cs  (new)
src/Voidforge.Api/Balance/
  ShipBalance.cs / ShipsBalanceOptions.cs   (new); BalanceOptions.cs (modify — Ships block)
src/Voidforge.Api/WorldGeneration/
  WorldGenOptions.cs                  (modify — PlanetSpread = 20m)
  WorldSeeder.cs                      (modify — planet offset within ±PlanetSpread of system centre)
src/Voidforge.Api/Endpoints/
  FleetEndpoints.cs                   (modify — Launch endpoint)
  LaunchMissionRequest.cs             (new)
  FleetResponse.cs                    (modify — transit block fields)
  CompleteFleetArrivalHandler.cs      (new)
  PlanetResponse.cs                   (modify — x/y/z)
src/Voidforge.Api/Http/ConcurrencyConflictExceptionHandler.cs  (modify — "this planet" → "this resource")
src/Voidforge.Api/Program.cs          (modify — AddSingleton<ITravelPlanner, LinearTravelPlanner>)
src/Voidforge.Tests/
  Concurrency/FleetConcurrencyTests.cs      (new — Task 1, #48 carry-over)
  Travel/LinearTravelPlannerTests.cs        (new, unit)
  Travel/FleetTravelDomainTests.cs          (new, unit)
  Travel/FleetMissionEndpointTests.cs       (new, integration)
  Travel/MoveMissionEndToEndTests.cs        (new, e2e merge gate)
  AppFixture.cs                       (modify — ship-speed overrides for fast e2e)
technical-design/architecture.md      (modify — §4 supersession, stream table row)
technical-design/domain-model.md      (modify — Fleet travel, Planet coordinates)
game-design/player-actions.md         (modify — add fleet actions if the doc enumerates them; check first)
```

---

### Task 1: Fleet-path race coverage + 409 message fix (carry-over gate from #48)

**Files:**
- Create: `src/Voidforge.Tests/Concurrency/FleetConcurrencyTests.cs`
- Modify: `src/Voidforge.Api/Http/ConcurrencyConflictExceptionHandler.cs`

**Interfaces:** none produced; consumes #48's endpoints and the `SameStreamConcurrencyTests` batching idiom (read that file first and mirror its structure/helpers).

- [ ] **Step 1: Write the two race tests** in `FleetConcurrencyTests` (`[Collection(IntegrationCollection.Name)]`). Arrange once: register, build shipyard, build ONE roster ship (copy the `BuildOperationalShipyard`/`BuildRosterShip` helpers from `Fleets/FleetEndpointTests.cs`).

Test A — two concurrent assembles claiming the same ship:

```csharp
[Fact]
public async Task ConcurrentAssemblesOfTheSameShipYieldExactlyOneFleet()
{
    var registration = await RegisterPlayer();
    await BuildOperationalShipyard(registration);
    var shipId = await BuildRosterShip(registration);

    var attempts = await Task.WhenAll(
        TryAssemble(registration, [shipId]),
        TryAssemble(registration, [shipId]));

    // #39 semantics: the loser either gets 409 (concurrency/roster conflict) — or,
    // if it read the already-mutated roster, a clean 409 not-on-roster. Never two 200s.
    Assert.Equal(1, attempts.Count(status => status == 200));
    Assert.Equal(1, attempts.Count(status => status == 409));

    var fleets = await GetJson<PagedResponse<FleetSummaryResponse>>(registration, "/api/fleets");
    var fleet = Assert.Single(fleets.Items);
    Assert.Equal(1, fleet.ShipCount);
}
```

Test B — assemble racing disband on the same planet stream: assemble a fleet, then concurrently disband it twice; exactly one 200, one 409/404-family outcome, and the roster afterwards contains the ship exactly once (`Assert.Single(roster.Items, s => s.Id == shipId)`).

`TryAssemble` posts without `StatusCodeShouldBe` and returns the raw status code (see how `SameStreamConcurrencyTests` captures competing statuses — reuse its approach).

- [ ] **Step 2: Run them** — expected: both PASS already (the mechanism exists; this is gap-closing coverage, not TDD of new behavior). If either FAILS, stop and report the failure verbatim — that is a real #48 bug, not a test problem.
- [ ] **Step 3: Fix the 409 body** in `ConcurrencyConflictExceptionHandler`: replace the phrase `"Concurrent modification of this planet"` with `"Concurrent modification of this resource"` (Fleet streams conflict too now). Adjust the existing test that asserts this message if one exists (`grep -rn "Concurrent modification" src/`).
- [ ] **Step 4: Full suite green.**
- [ ] **Step 5: Commit** `test: fleet-path race coverage; generalize 409 conflict message (#49)`

---

### Task 2: Planet coordinates (spec D5)

**Files:**
- Modify: `src/Voidforge.Api/Domain/Events/PlanetCreated.cs`, `src/Voidforge.Api/Domain/Planet.cs`, `src/Voidforge.Api/WorldGeneration/WorldGenOptions.cs`, `src/Voidforge.Api/WorldGeneration/WorldSeeder.cs`, `src/Voidforge.Api/Endpoints/PlanetResponse.cs`
- Create: `src/Voidforge.Api/Domain/Coordinates.cs`
- Test: `src/Voidforge.Tests/Travel/PlanetCoordinateTests.cs`

**Interfaces:**
- Produces: `record Coordinates(decimal X, decimal Y, decimal Z)`; `PlanetCreated(..., decimal X, decimal Y, decimal Z)` (appended last); `Planet.X/Y/Z` snapshot properties + `Planet.GetCoordinates() → Coordinates`; `WorldGenOptions.PlanetSpread` (default `20m`); `PlanetResponse.X/Y/Z`.

- [ ] **Step 1: Failing unit test** (`PlanetCoordinateTests`, use `_`-prefixed static fields):

```csharp
[Fact]
public void PlanetCreatedSetsCoordinates()
{
    var planet = new Planet();
    planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 1000, 5, 1000, 1000, 12.5m, -3m, 990m));

    Assert.Equal(new Coordinates(12.5m, -3m, 990m), planet.GetCoordinates());
}
```

Integration test (same file or `[Collection]` sibling): GET a planet via the API and assert its `x/y/z` lie within `PlanetSpread` (20) of its solar system's `x/y/z` (fetch the system via `GET /api/solar-systems`, match by `SolarSystemId`).

- [ ] **Step 2: Verify RED** (compile error: `PlanetCreated` arity).
- [ ] **Step 3: Implement.** `PlanetCreated` gains `decimal X, decimal Y, decimal Z` as the LAST three positional parameters. `Planet` snapshots them (`public decimal X { get; set; }` etc.; `Apply(PlanetCreated)` copies; `GetCoordinates() => new(X, Y, Z)` as a method — stays out of the snapshot). `WorldGenOptions.PlanetSpread { get; set; } = 20m;`. `WorldSeeder`: hoist the system's `X/Y/Z` into locals before the planet loop, pass `systemX + NextCoordinate(random, opts.PlanetSpread)` (same for Y/Z) into `PlanetCreated`. `PlanetResponse` gains `decimal X, decimal Y, decimal Z` + mapping. Fix ALL `new PlanetCreated(` sites the compiler reports — production and tests (`grep -rn "new PlanetCreated(" src/ | grep -v obj/`); in tests append explicit literals like `0m, 0m, 0m` unless the test is about coordinates.
- [ ] **Step 4: GREEN + full suite** (schema-reset fixture reseeds with coordinates).
- [ ] **Step 5: Commit** `feat: per-planet 3D coordinates, seeded around system centre (D5) (#49)`

---

### Task 3: Ship balance block (spec §6)

**Files:**
- Create: `src/Voidforge.Api/Balance/ShipBalance.cs`, `src/Voidforge.Api/Balance/ShipsBalanceOptions.cs`
- Modify: `src/Voidforge.Api/Balance/BalanceOptions.cs`
- Test: `src/Voidforge.Tests/Balance/` (mirror the existing balance test file's style — find it with `ls src/Voidforge.Tests/Balance/`)

**Interfaces:**
- Produces: `ShipBalance { decimal SpeedPerSecond; decimal CargoCapacity; }` (mutable properties for the config binder); `ShipsBalanceOptions { ShipBalance ColonyShip; ShipBalance CargoVessel; ShipBalance For(ShipType type); }`; `BalanceOptions.Ships { get; set; } = new();`. Defaults: ColonyShip `SpeedPerSecond = 0.05m, CargoCapacity = 0m`; CargoVessel `SpeedPerSecond = 0.10m, CargoCapacity = 500m`. `For` throws `ArgumentOutOfRangeException` on unknown type (mirror `ForShip`).

- [ ] **Step 1: Failing test** — `BalanceOptions` defaults expose `Ships.For(ShipType.CargoVessel).SpeedPerSecond == 0.10m` and `Ships.For(ShipType.ColonyShip).CargoCapacity == 0m`; binder override test if the existing balance tests have a config-binding precedent (follow that file's pattern).
- [ ] **Step 2: RED.** — [ ] **Step 3: Implement per Interfaces.** — [ ] **Step 4: GREEN + full suite.**
- [ ] **Step 5: Commit** `feat: ship speed/cargo balance block under Balance:Ships (#49)`

---

### Task 4: Travel planner seam (spec D3/D4)

**Files:**
- Create: `src/Voidforge.Api/Travel/ITravelPlanner.cs`, `TravelPlan.cs`, `TravelLeg.cs`, `LinearTravelPlanner.cs` (namespace `Voidforge.Api.Travel`)
- Modify: `src/Voidforge.Api/Program.cs` (`builder.Services.AddSingleton<ITravelPlanner, LinearTravelPlanner>();` next to the TimeProvider registration)
- Test: `src/Voidforge.Tests/Travel/LinearTravelPlannerTests.cs`

**Interfaces (verbatim from spec §2.2):**

```csharp
public interface ITravelPlanner
{
    TravelPlan Plan(Coordinates origin, Coordinates destination,
                    decimal speedPerSecond, DateTimeOffset departAt);
}

public sealed record TravelPlan(
    DateTimeOffset ArrivesAt,
    decimal TotalDistance,
    IReadOnlyList<TravelLeg> Legs);

public sealed record TravelLeg(
    Guid? WaypointPlanetId,     // null in MVP — the single leg ends at the destination
    decimal Distance,
    DateTimeOffset ArrivesAt);
```

- [ ] **Step 1: Failing unit tests** — (a) 3D 2-3-6 triple: origin (0,0,0) → destination (2,3,6) is distance 7; speed 3.5 → arrival `departAt + 2s`; one leg, `WaypointPlanetId is null`, leg distance/ETA match the plan. (b) zero distance → `TotalDistance == 0`, `ArrivesAt == departAt`. (c) `speedPerSecond <= 0` → throws `InvalidOperationException` (spec §2.2: programming error).
- [ ] **Step 2: RED.**
- [ ] **Step 3: Implement** `LinearTravelPlanner`: `distance = (decimal)Math.Sqrt((double)(dx*dx + dy*dy + dz*dz))`; `seconds = distance / speedPerSecond`; `arrivesAt = departAt.AddSeconds((double)seconds)`; guard clause throws on `speedPerSecond <= 0` BEFORE computing. Comment the seam's purpose (post-MVP lanes/gates land as new planners + multi-leg plans — no event reshaping, spec D3/D4). Register in `Program.cs`.
- [ ] **Step 4: GREEN + full suite.** — [ ] **Step 5: Commit** `feat: ITravelPlanner seam with linear MVP implementation (D3/D4) (#49)`

---

### Task 5: Fleet transit domain — Depart / Arrive

**Files:**
- Create: `src/Voidforge.Api/Domain/MissionType.cs`, `Events/FleetDeparted.cs`, `Events/FleetArrived.cs`
- Modify: `src/Voidforge.Api/Domain/Fleet.cs`
- Test: `src/Voidforge.Tests/Travel/FleetTravelDomainTests.cs`

**Interfaces:**
- `enum MissionType { Move, Transport, Colonize }`
- `record FleetDeparted(Guid OriginPlanetId, Guid DestinationPlanetId, MissionType Mission, DateTimeOffset DepartedAt, TravelPlan Plan)` (needs `using Voidforge.Api.Travel;`)
- `record FleetArrived(Guid DestinationPlanetId, DateTimeOffset ArrivedAt)`
- `Fleet` gains nullable transit snapshot fields: `OriginPlanetId, DestinationPlanetId (Guid?), Mission (MissionType?), DepartedAt, ArrivesAt (DateTimeOffset?), TravelPlan (TravelPlan?)`
- `Fleet.GetSpeed(Func<ShipType, decimal> speedOf) → decimal` — `Ships.Min(s => speedOf(s.Type))`; throws `InvalidOperationException` on an empty fleet (cannot happen via API; programming error).
- `Fleet.Depart(Guid destinationPlanetId, MissionType mission, TravelPlan plan, DateTimeOffset at) → FleetDeparted` — throws `InvalidOperationException` unless `Stationed` with non-null `LocationPlanetId`; origin = `LocationPlanetId.Value`.
- `Fleet.Arrive(DateTimeOffset at) → IReadOnlyList<object>` — validate-on-arrival: empty list unless `Status == InTransit && ArrivesAt == at`; otherwise `[new FleetArrived(DestinationPlanetId!.Value, at)]`. (Mission dispatch beyond Move lands in #50/#51 — the method returns a list from day one so those PRs only add elements.)
- `Apply(FleetDeparted)`: `Status = InTransit`, `LocationPlanetId = null`, transit block set from the event (`ArrivesAt = @event.Plan.ArrivesAt`).
- `Apply(FleetArrived)`: `Status = Stationed`, `LocationPlanetId = @event.DestinationPlanetId`, transit block all-null (D6: always stationed; disband is the only roster path).

- [ ] **Step 1: Failing unit tests** (build fleets via `Fleet.Assemble` + `Apply` as `FleetAggregateTests` does; `_`-prefixed statics): GetSpeed picks the slowest ship (two ships, lookup `ColonyShip → 0.05m, CargoVessel → 0.10m`, expect `0.05m`); Depart on stationed fleet → event fields correct (origin = former location) and Apply flips to `InTransit` with null `LocationPlanetId` and correct `ArrivesAt`; Depart while `InTransit` throws; Arrive with matching `ArrivesAt` → single `FleetArrived`, Apply stations the fleet at the destination with a cleared transit block; Arrive with WRONG `ArrivesAt` → empty (stale no-op); Arrive while `Stationed` → empty; disband-after-arrival still works (`Disband` succeeds at the new location).
- [ ] **Step 2: RED.** — [ ] **Step 3: Implement per Interfaces** (comment on `FleetDeparted`: the plan rides the event so in-flight fleets keep departure economics — Phase 3 D10 principle). — [ ] **Step 4: GREEN + full suite.**
- [ ] **Step 5: Commit** `feat: fleet transit domain — depart, validate-on-arrival, stationed arrival (D2/D6) (#49)`

---

### Task 6: Launch endpoint, arrival handler, scheduling

**Files:**
- Create: `src/Voidforge.Api/Endpoints/LaunchMissionRequest.cs`, `src/Voidforge.Api/Endpoints/CompleteFleetArrivalHandler.cs`, `src/Voidforge.Api/Domain/Events/CompleteFleetArrival.cs`
- Modify: `src/Voidforge.Api/Endpoints/FleetEndpoints.cs`, `FleetResponse.cs`
- Modify: `src/Voidforge.Tests/AppFixture.cs` (add `Balance__Ships__ColonyShip__SpeedPerSecond` = `1000`, `Balance__Ships__CargoVessel__SpeedPerSecond` = `1000` so real scheduled arrivals resolve in ≤ ~4 s)
- Test: `src/Voidforge.Tests/Travel/FleetMissionEndpointTests.cs`

**Interfaces:**
- `record CompleteFleetArrival(Guid FleetId, DateTimeOffset ArrivesAt);` (command naming per Phase 3 D7: commands ask, events record)
- `record LaunchMissionRequest(MissionType Mission, Guid DestinationPlanetId);`
- `POST /api/fleets/{fleetId}/missions` → 200 `FleetResponse` | 400 (unsupported mission in #49, `Guid.Empty` destination, destination == current location) | 403 (not owner) | 404 (fleet or destination planet) | 409 (not `Stationed`)
- `FleetResponse` gains `Guid? OriginPlanetId, Guid? DestinationPlanetId, MissionType? Mission, DateTimeOffset? DepartedAt, DateTimeOffset? ArrivesAt` (from the aggregate's transit block; null when stationed). `FleetResponse.From(Fleet)` updated; existing tests constructing `FleetResponse` updated by the compiler's guidance.

Launch endpoint flow (mirror `ShipEndpoints.Queue`'s structure and comment style):

```csharp
[WolverinePost("/api/fleets/{fleetId}/missions")]
public static async Task<Results<Ok<FleetResponse>, BadRequest<string>, NotFound, ForbidHttpResult, Conflict<string>>> Launch(
    Guid fleetId,
    LaunchMissionRequest request,
    ClaimsPrincipal principal,
    IDocumentSession session,
    IMessageBus bus,
    ITravelPlanner travelPlanner,
    IOptions<BalanceOptions> balanceOptions,
    TimeProvider timeProvider)
{
    if (request.Mission != MissionType.Move)
    {
        return TypedResults.BadRequest("Mission not supported yet.");   // Transport → #50, Colonize → #51
    }

    if (request.DestinationPlanetId == Guid.Empty)
    {
        return TypedResults.BadRequest("destinationPlanetId is required.");
    }

    var stream = await session.Events.FetchForWriting<Fleet>(fleetId);
    var fleet = stream.Aggregate;
    if (fleet is null)
    {
        return TypedResults.NotFound();
    }

    if (PlayerId(principal) != fleet.OwnerId)
    {
        return TypedResults.Forbid();
    }

    if (fleet.Status != FleetStatus.Stationed || fleet.LocationPlanetId is null)
    {
        return TypedResults.Conflict("Only a stationed fleet can be launched.");
    }

    if (request.DestinationPlanetId == fleet.LocationPlanetId)
    {
        return TypedResults.BadRequest("Destination must differ from the fleet's current location.");
    }

    var destination = await session.LoadAsync<Planet>(request.DestinationPlanetId);
    if (destination is null)
    {
        return TypedResults.NotFound();
    }

    // Launch touches only the Fleet stream (spec §2.3) — the origin planet is read for
    // coordinates, never appended to.
    var origin = await session.LoadAsync<Planet>(fleet.LocationPlanetId.Value)
        ?? throw new InvalidOperationException($"Fleet {fleetId} is stationed at unknown planet {fleet.LocationPlanetId}.");

    var now = timeProvider.GetUtcNow();
    var balance = balanceOptions.Value;
    var speed = fleet.GetSpeed(t => balance.Ships.For(t).SpeedPerSecond);
    var plan = travelPlanner.Plan(origin.GetCoordinates(), destination.GetCoordinates(), speed, now);

    stream.AppendOne(fleet.Depart(request.DestinationPlanetId, request.Mission, plan, now));
    await bus.ScheduleAsync(new CompleteFleetArrival(fleetId, plan.ArrivesAt), plan.ArrivesAt);
    await session.SaveChangesAsync();

    var updated = await session.Events.FetchLatest<Fleet>(fleetId);
    return TypedResults.Ok(FleetResponse.From(updated!));
}
```

Arrival handler (mirror `CompleteShipConstructionHandler` exactly — thin, validate-on-arrival in the pure method):

```csharp
public static class CompleteFleetArrivalHandler
{
    public static async Task Handle(CompleteFleetArrival message, IDocumentSession session)
    {
        var stream = await session.Events.FetchForWriting<Fleet>(message.FleetId);
        var fleet = stream.Aggregate;
        if (fleet is null)
        {
            return;
        }

        var events = fleet.Arrive(message.ArrivesAt);
        if (events.Count == 0)
        {
            return;   // stale or superseded message (ADR 0001 validate-on-arrival)
        }

        stream.AppendMany([.. events]);
        await session.SaveChangesAsync();
    }
}
```

- [ ] **Step 1: Failing integration tests** (`FleetMissionEndpointTests`, helpers copied per suite style): launch unknown fleet → 404; launch foreign fleet → 403; launch with `mission: "Transport"` → 400; destination == current location → 400; unknown destination → 404; happy path: assemble → launch Move → 200 with `Status == InTransit`, non-null `ArrivesAt`, `DestinationPlanetId` set, `LocationPlanetId == null`; launch again while InTransit → 409; **handler-invoked arrival**: resolve `CompleteFleetArrivalHandler.Handle` directly with a crafted `CompleteFleetArrival(fleetId, arrivesAtFromResponse)` and a session from the host's `IDocumentStore` (see how existing tests obtain sessions — testing.md warns: never dispose the DI-owned store) → fleet `Stationed` at destination; **stale arrival**: call the handler again with the SAME message → no state change (idempotent), and with a wrong `ArrivesAt` → no-op.
- [ ] **Step 2: RED (routes/types missing).** — [ ] **Step 3: Implement per the code above.** — [ ] **Step 4: GREEN + full suite.**
- [ ] **Step 5: Commit** `feat: Move mission — launch endpoint, durable scheduled arrival (ADR 0001) (#49)`

---

### Task 7: Move e2e merge gate, docs, supersession

**Files:**
- Test: `src/Voidforge.Tests/Travel/MoveMissionEndToEndTests.cs`
- Modify: `technical-design/architecture.md`, `technical-design/domain-model.md`, `game-design/player-actions.md` (conditional — see below)

- [ ] **Step 1: Merge-gate e2e** (real scheduler, fast speeds from AppFixture): register → shipyard → 1 ship → assemble → `GET /api/solar-systems` to pick a planet in ANOTHER system (maximizes distance but still seconds at test speed) → launch Move → poll `GET /api/fleets/{id}` (bounded ~30 s timeout, mirror the polling helper in `ShipConstructionCompletionTests`) until `Status == Stationed && LocationPlanetId == destination` → disband → destination roster contains the ship. Assert in-transit reads mid-flight if trivially observable (`Status == InTransit` immediately after launch).
- [ ] **Step 2: Run it — PASS** (everything implemented; investigate, don't weaken, on failure).
- [ ] **Step 3: Docs.**
  - `architecture.md`: (a) stream-registry row `Fleet Mission … Wolverine Saga` → `Fleet | Guid (FleetId) | FleetAssembled, FleetDeparted, FleetArrived, CargoLoaded/Unloaded (#50), FleetDisbanded`; (b) prepend to §4: `> **Superseded (2026-07-26, #49):** Fleet arrival is implemented as a durable scheduled message resolved by a thin handler + pure aggregate method (ADR 0001; spec D2) — not the Saga sketched below, which is retained for history. See domain-model.md → Fleet.` Grep the section for other now-false present-tense claims and soften them to the historical frame — check EVERY occurrence, not just the first (a missed spot cost a fix round in #48).
  - `domain-model.md`: Fleet section gains travel (transit snapshot fields, `FleetDeparted` carrying the `TravelPlan`, `CompleteFleetArrival` scheduled command, validate-on-arrival, D6 stationed arrival); Planet section gains coordinates (D5 wording: planets positioned within `PlanetSpread` of system centre; `PlanetCreated` gains X/Y/Z). Scheduled-command table (if present from Phase 3) gains the `CompleteFleetArrival` row.
  - `game-design/player-actions.md`: read it; if it enumerates player actions, add Assemble fleet / Disband fleet / Launch mission (Move) rows in its existing format; if fleet actions are already listed, verify accuracy against the implemented endpoints.
- [ ] **Step 4: Full verification** — full suite + `dotnet format --verify-no-changes`.
- [ ] **Step 5: Commit** `test+docs: Move e2e gate; §4 saga supersession; travel in domain model (#49)` — no push, no PR (controller runs the whole-branch review first).

---

## Self-review notes (performed at plan-writing time)

- Spec coverage: D5 coordinates (Task 2), §6 balance (Task 3), D3/D4 seam (Task 4), D2/D6 depart/arrive (Task 5), §2.3 launch + §4 API (Task 6), §8 gate + doc obligations (Task 7), #48 carry-over gate (Task 1). Cargo capacity config lands here (Task 3) but is consumed by #50.
- Embedded code reviewed for the #48 failure modes: request DTO uses an enum + Guid (no nullable-list NRE surface; invalid enum JSON → framework 400; `Guid.Empty` guarded); test fields `_`-prefixed throughout; doc edits instruct grep-for-all-occurrences.
- Type consistency: `Coordinates` produced in Task 2, consumed Task 4/6; `TravelPlan` produced Task 4, consumed Task 5/6; `GetSpeed(Func<ShipType, decimal>)` produced Task 5, consumed Task 6; `Ships.For(...)` produced Task 3, consumed Task 6.
