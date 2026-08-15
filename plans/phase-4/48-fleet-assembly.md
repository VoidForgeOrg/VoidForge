# #48 — Fleet Assembly & Disband Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ships on a planet's roster can be grouped into an owned, event-sourced `Fleet` and disbanded back onto the roster of whatever planet the fleet sits at.

**Architecture:** `Fleet` becomes the third event-sourced Marten aggregate (inline snapshot, like `Player`/`Planet`). Assembly is one transaction touching both streams: `ShipsRemovedFromRoster` on the Planet stream + `StartStream<Fleet>(FleetAssembled)`. Disband is the mirror image (`FleetDisbanded` + `ShipsAddedToRoster`). Per spec D13, `RosterShip` gains an `OwnerId` and assembly validates **ship** ownership, not planet ownership.

**Tech Stack:** .NET 9, Marten (event sourcing + inline snapshots), Wolverine HTTP endpoints, xUnit + Alba.

**Spec:** `plans/phase-4-fleets-expansion-design.md` §2.1, §2.3 (Assembly/Disband), §3, §4, §5; decisions D1, D6, D11 (guard arrives with cargo in #50), D13.

## Global Constraints

- `TreatWarningsAsErrors` is on; Meziantou MA0048 = one public type per file — every new record/class/enum gets its own file.
- Collection endpoints follow the #29 pagination contract (`PaginationParameters`, `PagedResponse<T>`, explicit deterministic order documented in `technical-design/api-conventions.md`).
- Mutations require ownership (403); reads are universe-visible to any authenticated player (fallback auth policy supplies 401).
- Every append site uses `session.Events.FetchForWriting<T>(id)` so #39's optimistic-concurrency + retry applies.
- TDD: write the failing test first in each task. Full suite + `dotnet format` before the PR.
- Commits: conventional, suffixed `(#48)`.

## Plan-level decisions (within spec letter/spirit)

1. **`FleetStatus` gains a terminal `Disbanded` value** (spec §2.1 lists only live statuses). A disbanded fleet's snapshot survives as history; list endpoints exclude `Disbanded` unless the caller filters for it explicitly.
2. **Roster-membership validation lives in the endpoint**, which must resolve the `RosterShip` objects anyway for the D13 ownership check and the fleet composition. Domain methods are pure event factories (no double validation). This mirrors how #27 endpoints own 404/403 while `Apply` stays unconditional.
3. **`RosterShip.OwnerId` is `Guid?`** (mirrors `Planet.OwnerId`). Pre-#48 snapshots deserialize with `null` — harmless because dev/test worlds reseed (spec D5 note) and the test DB drops its schema every run.
4. **`RosterShipResponse` surfaces `ownerId`** so clients can tell which roster ships are assemblable by them — the moment disband-at-foreign-planets exists, foreign-owned ships on your roster become reachable state.

## File Structure

```text
src/Voidforge.Api/Domain/
  RosterShip.cs                      (modify — add OwnerId)
  Planet.Ships.cs                    (modify — roster mutation methods + Apply, OwnerId on completion)
  Fleet.cs                           (new aggregate: state + Apply methods)
  FleetShip.cs                       (new record)
  FleetStatus.cs                     (new enum)
  Events/FleetAssembled.cs           (new)
  Events/FleetDisbanded.cs           (new)
  Events/ShipsRemovedFromRoster.cs   (new)
  Events/ShipsAddedToRoster.cs       (new)
src/Voidforge.Api/Endpoints/
  FleetEndpoints.cs                  (new: assemble, disband, 3 reads)
  AssembleFleetRequest.cs            (new)
  FleetResponse.cs                   (new, + FleetShipResponse in own file)
  FleetShipResponse.cs               (new)
  FleetSummaryResponse.cs            (new)
  RosterShipResponse.cs              (modify — add OwnerId)  [currently declared near ShipEndpoints DTOs — find with grep]
  ShipEndpoints.cs                   (modify — roster mapping passes OwnerId)
src/Voidforge.Api/Program.cs         (modify — Snapshot<Fleet> registration)
src/Voidforge.Tests/Fleets/
  FleetAggregateTests.cs             (new, unit)
  PlanetRosterMutationTests.cs       (new, unit)
  FleetEndpointTests.cs              (new, integration: validation + reads)
  FleetRoundTripTests.cs             (new, e2e merge gate)
game-design/fleets.md                (modify — D6 always-stationed rule)
technical-design/domain-model.md     (modify — Fleet aggregate section)
technical-design/api-conventions.md  (modify — deterministic-order list)
```

---

### Task 1: `RosterShip.OwnerId` (D13)

**Files:**
- Modify: `src/Voidforge.Api/Domain/RosterShip.cs`
- Modify: `src/Voidforge.Api/Domain/Planet.Ships.cs` (the `Apply(ShipCompleted)` construction site)
- Test: `src/Voidforge.Tests/Fleets/PlanetRosterMutationTests.cs` (new file, first test)

**Interfaces:**
- Produces: `record RosterShip(Guid Id, ShipType Type, DateTimeOffset CompletedAt, Guid? OwnerId)` — later tasks read `OwnerId` for assembly authorization.

- [ ] **Step 1: Write the failing test**

```csharp
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Xunit;

namespace Voidforge.Tests.Fleets;

public sealed class PlanetRosterMutationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Builds an owned planet with one completed CargoVessel on the roster, via real events.
    private static (Planet Planet, Guid OwnerId, Guid ShipId) PlanetWithRosterShip()
    {
        var ownerId = Guid.NewGuid();
        var shipId = Guid.NewGuid();
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 1000, 5, 1000, 1000));
        planet.Apply(new PlanetColonized(ownerId, 0, 0, T0));
        planet.Apply(new ShipConstructionQueued(shipId, ShipType.CargoVessel, T0, 0m, 60m));
        planet.Apply(new ShipConstructionStarted(shipId, T0, T0.AddSeconds(60)));
        planet.Apply(new ShipCompleted(shipId, T0.AddSeconds(60)));
        return (planet, ownerId, shipId);
    }

    [Fact]
    public void CompletedShipCarriesThePlanetsOwner()
    {
        var (planet, ownerId, shipId) = PlanetWithRosterShip();

        var ship = Assert.Single(planet.Ships);
        Assert.Equal(shipId, ship.Id);
        Assert.Equal(ownerId, ship.OwnerId);
    }
}
```

Adjust the `PlanetCreated`/`PlanetColonized` constructor argument lists to the actual record definitions in `Domain/Events/` (positional order matters; check the files).

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test src/Voidforge.slnx --filter "FullyQualifiedName~PlanetRosterMutationTests" 2>&1 | tail -5`
Expected: compile error — `RosterShip` has no `OwnerId`.

- [ ] **Step 3: Implement**

`RosterShip.cs`:

```csharp
namespace Voidforge.Api.Domain;

// A completed ship. CompletedAt gives the roster a stable, meaningful default sort.
// OwnerId (D13) is the owner of the planet at completion time; assembly validates ship
// ownership rather than planet ownership so ships disbanded onto a foreign or unowned
// planet's roster stay reachable by their owner. Nullable to mirror Planet.OwnerId;
// pre-#48 snapshots deserialize with null (dev worlds reseed).
public sealed record RosterShip(Guid Id, ShipType Type, DateTimeOffset CompletedAt, Guid? OwnerId);
```

In `Planet.Ships.cs`, `Apply(ShipCompleted)`:

```csharp
Ships.Add(new RosterShip(build.Id, build.Type, @event.CompletedAt, OwnerId));
```

Fix any other `new RosterShip(` call sites the compiler reports (tests included — but per the merge gate, prefer updating only non-test construction sites; test files may only be touched where they construct `RosterShip` directly).

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test src/Voidforge.slnx --filter "FullyQualifiedName~PlanetRosterMutationTests" 2>&1 | tail -5`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A src/ && git commit -m "feat: RosterShip carries OwnerId from the completing planet (D13) (#48)"
```

---

### Task 2: Planet roster mutation events + methods

**Files:**
- Create: `src/Voidforge.Api/Domain/Events/ShipsRemovedFromRoster.cs`, `src/Voidforge.Api/Domain/Events/ShipsAddedToRoster.cs`
- Modify: `src/Voidforge.Api/Domain/Planet.Ships.cs`
- Test: `src/Voidforge.Tests/Fleets/PlanetRosterMutationTests.cs`

**Interfaces:**
- Produces:
  - `record ShipsRemovedFromRoster(Guid FleetId, IReadOnlyList<Guid> ShipIds, DateTimeOffset At)`
  - `record ShipsAddedToRoster(Guid FleetId, IReadOnlyList<RosterShip> Ships, DateTimeOffset At)`
  - `Planet.RemoveShipsFromRoster(Guid fleetId, IReadOnlyList<Guid> shipIds, DateTimeOffset at) → ShipsRemovedFromRoster`
  - `Planet.ReturnShipsToRoster(Guid fleetId, IReadOnlyList<RosterShip> ships, DateTimeOffset at) → ShipsAddedToRoster`

- [ ] **Step 1: Write the failing tests** (append to `PlanetRosterMutationTests`)

```csharp
[Fact]
public void RemoveShipsFromRosterRemovesExactlyThoseShips()
{
    var (planet, _, shipId) = PlanetWithRosterShip();
    var fleetId = Guid.NewGuid();

    var @event = planet.RemoveShipsFromRoster(fleetId, [shipId], T0.AddSeconds(120));
    planet.Apply(@event);

    Assert.Empty(planet.Ships);
    Assert.Equal(fleetId, @event.FleetId);
    Assert.Equal([shipId], @event.ShipIds);
}

[Fact]
public void RemoveShipsFromRosterWithUnknownIdThrows()
{
    var (planet, _, _) = PlanetWithRosterShip();

    Assert.Throws<InvalidOperationException>(
        () => planet.RemoveShipsFromRoster(Guid.NewGuid(), [Guid.NewGuid()], T0));
}

[Fact]
public void ReturnShipsToRosterRestoresShipsWithOwner()
{
    var (planet, ownerId, shipId) = PlanetWithRosterShip();
    planet.Apply(planet.RemoveShipsFromRoster(Guid.NewGuid(), [shipId], T0.AddSeconds(120)));

    var returned = new RosterShip(shipId, ShipType.CargoVessel, T0.AddSeconds(60), ownerId);
    planet.Apply(planet.ReturnShipsToRoster(Guid.NewGuid(), [returned], T0.AddSeconds(200)));

    var ship = Assert.Single(planet.Ships);
    Assert.Equal(ownerId, ship.OwnerId);
    Assert.Equal(shipId, ship.Id);
}
```

- [ ] **Step 2: Run to verify failure** (compile errors for the new members)

Run: `dotnet test src/Voidforge.slnx --filter "FullyQualifiedName~PlanetRosterMutationTests" 2>&1 | tail -5`

- [ ] **Step 3: Implement**

Event records (one file each, in `Domain/Events/`):

```csharp
namespace Voidforge.Api.Domain.Events;

// Ships leave the roster into a fleet at assembly. Roster mutations do not touch
// resource rates — no RebaseRates in the Apply.
public sealed record ShipsRemovedFromRoster(Guid FleetId, IReadOnlyList<Guid> ShipIds, DateTimeOffset At);
```

```csharp
namespace Voidforge.Api.Domain.Events;

// Ships return to the roster on disband. Carries full RosterShip records (with OwnerId)
// so the Apply is a plain add and the fleet owner survives the round-trip (D13).
public sealed record ShipsAddedToRoster(Guid FleetId, IReadOnlyList<RosterShip> Ships, DateTimeOffset At);
```

(`ShipsAddedToRoster` needs `using Voidforge.Api.Domain;` or fully-qualified `RosterShip` — match the namespace idiom of existing event files.)

In `Planet.Ships.cs`:

```csharp
// Assembly (#48): pure event factory. The endpoint has already resolved and authorized
// the ships; an id missing here is a programming error, not a user error.
public ShipsRemovedFromRoster RemoveShipsFromRoster(Guid fleetId, IReadOnlyList<Guid> shipIds, DateTimeOffset at)
{
    foreach (var shipId in shipIds)
    {
        if (!Ships.Any(s => s.Id == shipId))
        {
            throw new InvalidOperationException($"Ship {shipId} is not on the roster.");
        }
    }

    return new ShipsRemovedFromRoster(fleetId, shipIds, at);
}

// Disband (#48): ships come back carrying the fleet owner's id (D13).
public ShipsAddedToRoster ReturnShipsToRoster(Guid fleetId, IReadOnlyList<RosterShip> ships, DateTimeOffset at)
    => new(fleetId, ships, at);

public void Apply(ShipsRemovedFromRoster @event)
{
    foreach (var shipId in @event.ShipIds)
    {
        var index = Ships.ToList().FindIndex(s => s.Id == shipId);
        if (index >= 0)
        {
            Ships.RemoveAt(index);
        }
    }
    // Roster ships are inert — no rate change, so no RebaseRates.
}

public void Apply(ShipsAddedToRoster @event)
{
    foreach (var ship in @event.Ships)
    {
        Ships.Add(ship);
    }
    // Roster ships are inert — no rate change, so no RebaseRates.
}
```

(If `Ships` is `List<RosterShip>` at runtime, simplify the removal; keep it allocation-light but correct for `IList`.)

- [ ] **Step 4: Run the tests to verify they pass**, then run the full unit suite briefly: `dotnet test src/Voidforge.slnx --filter "FullyQualifiedName~Voidforge.Tests.Domain|FullyQualifiedName~Fleets" 2>&1 | tail -5`

- [ ] **Step 5: Commit**

```bash
git add -A src/ && git commit -m "feat: planet roster mutation events for fleet assembly/disband (#48)"
```

---

### Task 3: The `Fleet` aggregate

**Files:**
- Create: `src/Voidforge.Api/Domain/Fleet.cs`, `src/Voidforge.Api/Domain/FleetShip.cs`, `src/Voidforge.Api/Domain/FleetStatus.cs`
- Create: `src/Voidforge.Api/Domain/Events/FleetAssembled.cs`, `src/Voidforge.Api/Domain/Events/FleetDisbanded.cs`
- Modify: `src/Voidforge.Api/Program.cs` (snapshot registration)
- Test: `src/Voidforge.Tests/Fleets/FleetAggregateTests.cs`

**Interfaces:**
- Produces:
  - `enum FleetStatus { Stationed, InTransit, Disbanded }` (`InTransit` is wired in #49)
  - `record FleetShip(Guid Id, ShipType Type, DateTimeOffset CompletedAt)`
  - `record FleetAssembled(Guid OwnerId, Guid PlanetId, IReadOnlyList<FleetShip> Ships, DateTimeOffset AssembledAt)`
  - `record FleetDisbanded(Guid PlanetId, DateTimeOffset DisbandedAt)`
  - `Fleet` snapshot fields: `Id, OwnerId, Status, LocationPlanetId (Guid?), AssembledAt, Ships (IList<FleetShip>)`
  - `static Fleet.Assemble(Guid ownerId, Guid planetId, IReadOnlyList<RosterShip> ships, DateTimeOffset at) → FleetAssembled`
  - `Fleet.Disband(DateTimeOffset at) → FleetDisbanded` (throws `InvalidOperationException` unless `Stationed`)
  - `Fleet.ToRosterShips() → IReadOnlyList<RosterShip>` (maps ships back with the fleet's `OwnerId`)

- [ ] **Step 1: Write the failing tests**

```csharp
using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Xunit;

namespace Voidforge.Tests.Fleets;

public sealed class FleetAggregateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (Fleet Fleet, Guid OwnerId, Guid PlanetId, RosterShip Ship) AssembledFleet()
    {
        var ownerId = Guid.NewGuid();
        var planetId = Guid.NewGuid();
        var ship = new RosterShip(Guid.NewGuid(), ShipType.ColonyShip, T0, ownerId);
        var fleet = new Fleet();
        fleet.Apply(Fleet.Assemble(ownerId, planetId, [ship], T0.AddSeconds(10)));
        return (fleet, ownerId, planetId, ship);
    }

    [Fact]
    public void AssembleProducesAStationedFleetAtThePlanet()
    {
        var (fleet, ownerId, planetId, ship) = AssembledFleet();

        Assert.Equal(FleetStatus.Stationed, fleet.Status);
        Assert.Equal(planetId, fleet.LocationPlanetId);
        Assert.Equal(ownerId, fleet.OwnerId);
        var fleetShip = Assert.Single(fleet.Ships);
        Assert.Equal(ship.Id, fleetShip.Id);
        Assert.Equal(ship.CompletedAt, fleetShip.CompletedAt);   // roster sort key survives (§2.1)
    }

    [Fact]
    public void DisbandReturnsShipsCarryingTheFleetOwner()
    {
        var (fleet, ownerId, planetId, ship) = AssembledFleet();

        var roster = fleet.ToRosterShips();
        fleet.Apply(fleet.Disband(T0.AddSeconds(20)));

        Assert.Equal(FleetStatus.Disbanded, fleet.Status);
        Assert.Empty(fleet.Ships);
        var returned = Assert.Single(roster);
        Assert.Equal(ownerId, returned.OwnerId);
        Assert.Equal(ship.Id, returned.Id);
    }

    [Fact]
    public void DisbandTwiceThrows()
    {
        var (fleet, _, _, _) = AssembledFleet();
        fleet.Apply(fleet.Disband(T0.AddSeconds(20)));

        Assert.Throws<InvalidOperationException>(() => fleet.Disband(T0.AddSeconds(30)));
    }
}
```

- [ ] **Step 2: Run to verify failure** (types don't exist)

- [ ] **Step 3: Implement**

`FleetStatus.cs`:

```csharp
namespace Voidforge.Api.Domain;

// Disbanded is terminal: the snapshot survives as history and list endpoints filter it
// out unless explicitly requested. InTransit is wired by travel (#49).
public enum FleetStatus
{
    Stationed,
    InTransit,
    Disbanded,
}
```

`FleetShip.cs`:

```csharp
namespace Voidforge.Api.Domain;

// Mirrors RosterShip (minus OwnerId — the fleet has one owner) so ships round-trip
// through a fleet without losing the roster's stable sort key (spec §2.1).
public sealed record FleetShip(Guid Id, ShipType Type, DateTimeOffset CompletedAt);
```

Event records in `Domain/Events/` (one per file):

```csharp
public sealed record FleetAssembled(Guid OwnerId, Guid PlanetId, IReadOnlyList<FleetShip> Ships, DateTimeOffset AssembledAt);
public sealed record FleetDisbanded(Guid PlanetId, DateTimeOffset DisbandedAt);
```

`Fleet.cs`:

```csharp
using Voidforge.Api.Domain.Events;

namespace Voidforge.Api.Domain;

// Event-sourced aggregate (spec D1): fleets outlive any single mission. Inline snapshot
// like Player/Planet. Travel state (#49) and cargo (#50) extend this class.
public sealed class Fleet
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public FleetStatus Status { get; set; }
    public Guid? LocationPlanetId { get; set; }
    public DateTimeOffset AssembledAt { get; set; }
    public IList<FleetShip> Ships { get; set; } = [];

    // Pure factory: the endpoint validates ship ownership (D13) and roster membership
    // before calling. Ships map 1:1 so the roster's sort key survives the round-trip.
    public static FleetAssembled Assemble(
        Guid ownerId, Guid planetId, IReadOnlyList<RosterShip> ships, DateTimeOffset at)
        => new(
            ownerId,
            planetId,
            ships.Select(s => new FleetShip(s.Id, s.Type, s.CompletedAt)).ToList(),
            at);

    public void Apply(FleetAssembled @event)
    {
        OwnerId = @event.OwnerId;
        Status = FleetStatus.Stationed;
        LocationPlanetId = @event.PlanetId;
        AssembledAt = @event.AssembledAt;
        Ships = [.. @event.Ships];
    }

    // Stationed-only (409 at the endpoint). The cargo-empty guard (D11) arrives with #50.
    public FleetDisbanded Disband(DateTimeOffset at)
    {
        if (Status != FleetStatus.Stationed || LocationPlanetId is null)
        {
            throw new InvalidOperationException("Only a stationed fleet can be disbanded.");
        }

        return new FleetDisbanded(LocationPlanetId.Value, at);
    }

    public void Apply(FleetDisbanded @event)
    {
        Status = FleetStatus.Disbanded;
        Ships = [];
    }

    // Ships leave carrying the fleet owner's id (D13) so they stay assemblable wherever
    // they land — including a foreign or unowned planet's roster.
    public IReadOnlyList<RosterShip> ToRosterShips()
        => Ships.Select(s => new RosterShip(s.Id, s.Type, s.CompletedAt, OwnerId)).ToList();
}
```

`Program.cs`, after the `Planet` snapshot line:

```csharp
opts.Projections.Snapshot<Fleet>(SnapshotLifecycle.Inline);
```

- [ ] **Step 4: Run the tests to verify they pass**

- [ ] **Step 5: Commit**

```bash
git add -A src/ && git commit -m "feat: event-sourced Fleet aggregate — assemble, disband, roster round-trip (#48)"
```

---

### Task 4: Assemble & disband endpoints

**Files:**
- Create: `src/Voidforge.Api/Endpoints/FleetEndpoints.cs`, `AssembleFleetRequest.cs`, `FleetResponse.cs`, `FleetShipResponse.cs`, `FleetSummaryResponse.cs`
- Test: `src/Voidforge.Tests/Fleets/FleetEndpointTests.cs`

**Interfaces:**
- Consumes: Task 2 planet methods, Task 3 fleet factory/methods.
- Produces:
  - `POST /api/planets/{planetId}/fleets` body `AssembleFleetRequest(IReadOnlyList<Guid> ShipIds)` → 200 `FleetResponse` | 400 | 403 | 404 | 409
  - `POST /api/fleets/{fleetId}/disband` → 200 `FleetResponse` | 403 | 404 | 409
  - `record FleetShipResponse(Guid Id, ShipType Type, DateTimeOffset CompletedAt)`
  - `record FleetResponse(Guid Id, Guid OwnerId, FleetStatus Status, Guid? LocationPlanetId, DateTimeOffset AssembledAt, IReadOnlyList<FleetShipResponse> Ships)` with `static FleetResponse From(Fleet fleet)`
  - `record FleetSummaryResponse(Guid Id, Guid OwnerId, FleetStatus Status, Guid? LocationPlanetId, DateTimeOffset AssembledAt, int ShipCount)`

- [ ] **Step 1: Write the failing integration tests**

`FleetEndpointTests.cs`, `[Collection(IntegrationCollection.Name)]`, copying the private helpers `RegisterPlayer()` / `QueueShip()` idiom from `ShipEndpointTests` and `BuildOperationalShipyard()` + roster-polling from `ShipConstructionCompletionTests` (shared via copy, matching the suite's current style):

```csharp
[Fact]
public async Task AssembleWithEmptyShipIdsReturns400()
{
    var registration = await RegisterPlayer();
    await _host.Scenario(s =>
    {
        s.Post.Json(new AssembleFleetRequest([])).ToUrl($"/api/planets/{registration.HomeworldId}/fleets");
        s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
        s.StatusCodeShouldBe(400);
    });
}

[Fact]
public async Task AssembleUnknownPlanetReturns404()
{
    var registration = await RegisterPlayer();
    await _host.Scenario(s =>
    {
        s.Post.Json(new AssembleFleetRequest([Guid.NewGuid()])).ToUrl($"/api/planets/{Guid.NewGuid()}/fleets");
        s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
        s.StatusCodeShouldBe(404);
    });
}

[Fact]
public async Task AssembleShipNotOnRosterReturns409()
{
    var registration = await RegisterPlayer();
    await _host.Scenario(s =>
    {
        s.Post.Json(new AssembleFleetRequest([Guid.NewGuid()])).ToUrl($"/api/planets/{registration.HomeworldId}/fleets");
        s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
        s.StatusCodeShouldBe(409);
    });
}

[Fact]
public async Task AssembleSomeoneElsesShipsReturns403()
{
    var owner = await RegisterPlayer();          // builds the ships
    var intruder = await RegisterPlayer();
    var shipId = await BuildRosterShip(owner);   // shipyard + 1 CargoVessel, waits for roster

    await _host.Scenario(s =>
    {
        s.Post.Json(new AssembleFleetRequest([shipId])).ToUrl($"/api/planets/{owner.HomeworldId}/fleets");
        s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, intruder.ApiKey);
        s.StatusCodeShouldBe(403);
    });
}

[Fact]
public async Task DisbandUnknownFleetReturns404()
{
    var registration = await RegisterPlayer();
    await _host.Scenario(s =>
    {
        s.Post.Url($"/api/fleets/{Guid.NewGuid()}/disband");
        s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
        s.StatusCodeShouldBe(404);
    });
}

[Fact]
public async Task DisbandForeignFleetReturns403()
{
    var owner = await RegisterPlayer();
    var intruder = await RegisterPlayer();
    var shipId = await BuildRosterShip(owner);
    var fleet = await AssembleFleet(owner, [shipId]);

    await _host.Scenario(s =>
    {
        s.Post.Url($"/api/fleets/{fleet.Id}/disband");
        s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, intruder.ApiKey);
        s.StatusCodeShouldBe(403);
    });
}
```

- [ ] **Step 2: Run to verify failure** (404s where 400/409 expected, missing types)

- [ ] **Step 3: Implement `FleetEndpoints`** (assemble + disband only; reads are Task 5)

```csharp
using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Voidforge.Api.Domain;
using Wolverine.Http;

namespace Voidforge.Api.Endpoints;

public static class FleetEndpoints
{
    // Assembly (spec §2.3): one transaction over both streams. Ship ownership — not planet
    // ownership — is what's validated (D13): ships stranded on a foreign or unowned world
    // can still be formed into a fleet by their owner. Cargo loading arrives with #50.
    [WolverinePost("/api/planets/{planetId}/fleets")]
    public static async Task<Results<Ok<FleetResponse>, BadRequest<string>, NotFound, ForbidHttpResult, Conflict<string>>> Assemble(
        Guid planetId,
        AssembleFleetRequest request,
        ClaimsPrincipal principal,
        IDocumentSession session,
        TimeProvider timeProvider)
    {
        if (request.ShipIds.Count == 0)
        {
            return TypedResults.BadRequest("shipIds must not be empty.");
        }

        if (request.ShipIds.Distinct().Count() != request.ShipIds.Count)
        {
            return TypedResults.BadRequest("shipIds must not contain duplicates.");
        }

        // FetchForWriting arms Marten's optimistic-concurrency guard (#39).
        var stream = await session.Events.FetchForWriting<Planet>(planetId);
        var planet = stream.Aggregate;
        if (planet is null)
        {
            return TypedResults.NotFound();
        }

        var byId = planet.Ships.ToDictionary(s => s.Id);
        var missing = request.ShipIds.Where(id => !byId.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            return TypedResults.Conflict($"Ship(s) not on this planet's roster: {string.Join(", ", missing)}.");
        }

        var playerId = PlayerId(principal);
        var ships = request.ShipIds.Select(id => byId[id]).ToList();
        if (playerId is null || ships.Any(s => s.OwnerId != playerId))
        {
            return TypedResults.Forbid();
        }

        var now = timeProvider.GetUtcNow();
        var fleetId = Guid.NewGuid();
        stream.AppendOne(planet.RemoveShipsFromRoster(fleetId, request.ShipIds, now));
        session.Events.StartStream<Fleet>(fleetId, Fleet.Assemble(playerId.Value, planetId, ships, now));
        await session.SaveChangesAsync();

        var fleet = await session.Events.FetchLatest<Fleet>(fleetId);
        return TypedResults.Ok(FleetResponse.From(fleet!));
    }

    // Disband (D6 counterpart): ships reach a roster only through this path. Allowed at
    // unowned/foreign planets (fleets.md); refused while cargo remains from #50 on.
    [WolverinePost("/api/fleets/{fleetId}/disband")]
    public static async Task<Results<Ok<FleetResponse>, NotFound, ForbidHttpResult, Conflict<string>>> Disband(
        Guid fleetId,
        ClaimsPrincipal principal,
        IDocumentSession session,
        TimeProvider timeProvider)
    {
        var fleetStream = await session.Events.FetchForWriting<Fleet>(fleetId);
        var fleet = fleetStream.Aggregate;
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
            return TypedResults.Conflict("Only a stationed fleet can be disbanded.");
        }

        var planetStream = await session.Events.FetchForWriting<Planet>(fleet.LocationPlanetId.Value);
        var planet = planetStream.Aggregate
            ?? throw new InvalidOperationException($"Fleet {fleetId} is stationed at unknown planet {fleet.LocationPlanetId}.");

        var now = timeProvider.GetUtcNow();
        planetStream.AppendOne(planet.ReturnShipsToRoster(fleet.Id, fleet.ToRosterShips(), now));
        fleetStream.AppendOne(fleet.Disband(now));
        await session.SaveChangesAsync();

        var updated = await session.Events.FetchLatest<Fleet>(fleetId);
        return TypedResults.Ok(FleetResponse.From(updated!));
    }

    private static Guid? PlayerId(ClaimsPrincipal principal)
        => Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
```

DTOs (one file each):

```csharp
public sealed record AssembleFleetRequest(IReadOnlyList<Guid> ShipIds);

public sealed record FleetShipResponse(Guid Id, ShipType Type, DateTimeOffset CompletedAt);

public sealed record FleetSummaryResponse(
    Guid Id, Guid OwnerId, FleetStatus Status, Guid? LocationPlanetId, DateTimeOffset AssembledAt, int ShipCount);

public sealed record FleetResponse(
    Guid Id, Guid OwnerId, FleetStatus Status, Guid? LocationPlanetId,
    DateTimeOffset AssembledAt, IReadOnlyList<FleetShipResponse> Ships)
{
    public static FleetResponse From(Fleet fleet) => new(
        fleet.Id, fleet.OwnerId, fleet.Status, fleet.LocationPlanetId, fleet.AssembledAt,
        fleet.Ships.Select(s => new FleetShipResponse(s.Id, s.Type, s.CompletedAt)).ToList());
}
```

(`FleetSummaryResponse` is consumed by Task 5's list endpoints; it lives here so the DTO family lands together.)

- [ ] **Step 4: Run the new integration tests** (needs the postgres container; if connection errors, use the start-infra skill)

Run: `dotnet test src/Voidforge.slnx --filter "FullyQualifiedName~FleetEndpointTests" 2>&1 | tail -5`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A src/ && git commit -m "feat: fleet assembly + disband endpoints, two-stream transactions (#48)"
```

---

### Task 5: Fleet read endpoints

**Files:**
- Modify: `src/Voidforge.Api/Endpoints/FleetEndpoints.cs`
- Modify: `RosterShipResponse` declaration + its mapping in `ShipEndpoints.GetRoster` (add `OwnerId`)
- Test: `src/Voidforge.Tests/Fleets/FleetEndpointTests.cs`

**Interfaces:**
- Produces:
  - `GET /api/fleets?status=&page=&pageSize=` → `PagedResponse<FleetSummaryResponse>` — **caller's own fleets**, `Disbanded` excluded unless `status=Disbanded` requested; order `AssembledAt` then `Id`
  - `GET /api/fleets/{fleetId}` → `FleetResponse` | 404 — universe-visible
  - `GET /api/planets/{planetId}/fleets?page=&pageSize=` → `PagedResponse<FleetSummaryResponse>` — fleets **stationed** at the planet, universe-visible, same order
  - `RosterShipResponse` gains `Guid? OwnerId`

- [ ] **Step 1: Write the failing tests** (append to `FleetEndpointTests`; arrange via `BuildRosterShip` + assemble)

```csharp
[Fact]
public async Task OwnFleetsListIsPaginatedAndScopedToCaller()
{
    var a = await RegisterPlayer();
    var b = await RegisterPlayer();
    var shipId = await BuildRosterShip(a);
    var fleet = await AssembleFleet(a, [shipId]);

    var page = await GetJson<PagedResponse<FleetSummaryResponse>>(b, "/api/fleets");
    Assert.DoesNotContain(page.Items, f => f.Id == fleet.Id);   // b sees only own fleets

    var own = await GetJson<PagedResponse<FleetSummaryResponse>>(a, "/api/fleets");
    var summary = Assert.Single(own.Items, f => f.Id == fleet.Id);
    Assert.Equal(1, summary.ShipCount);
}

[Fact]
public async Task FleetDetailIsUniverseVisible()
{
    var a = await RegisterPlayer();
    var b = await RegisterPlayer();
    var shipId = await BuildRosterShip(a);
    var fleet = await AssembleFleet(a, [shipId]);

    var detail = await GetJson<FleetResponse>(b, $"/api/fleets/{fleet.Id}");
    Assert.Equal(fleet.Id, detail.Id);
    Assert.Single(detail.Ships);
}

[Fact]
public async Task PlanetFleetsListsStationedFleets()
{
    var a = await RegisterPlayer();
    var shipId = await BuildRosterShip(a);
    var fleet = await AssembleFleet(a, [shipId]);

    var page = await GetJson<PagedResponse<FleetSummaryResponse>>(a, $"/api/planets/{a.HomeworldId}/fleets");
    Assert.Contains(page.Items, f => f.Id == fleet.Id);
}

[Fact]
public async Task DisbandedFleetsAreExcludedFromListsUnlessRequested()
{
    var a = await RegisterPlayer();
    var shipId = await BuildRosterShip(a);
    var fleet = await AssembleFleet(a, [shipId]);
    await Disband(a, fleet.Id);

    var live = await GetJson<PagedResponse<FleetSummaryResponse>>(a, "/api/fleets");
    Assert.DoesNotContain(live.Items, f => f.Id == fleet.Id);

    var history = await GetJson<PagedResponse<FleetSummaryResponse>>(a, "/api/fleets?status=Disbanded");
    Assert.Contains(history.Items, f => f.Id == fleet.Id);
}
```

(`GetJson`/`AssembleFleet`/`Disband` are small private helpers following the file's existing scenario shape.)

- [ ] **Step 2: Run to verify failure** (404 — routes don't exist)

- [ ] **Step 3: Implement** (add to `FleetEndpoints`; `using Voidforge.Api.Pagination;`)

```csharp
// The caller's fleets (mutation-adjacent view — scoped to owner rather than universe,
// matching "my empire" reads). Disbanded fleets are history: excluded unless asked for.
[WolverineGet("/api/fleets")]
public static async Task<Results<Ok<PagedResponse<FleetSummaryResponse>>, BadRequest<string>>> GetOwnFleets(
    ClaimsPrincipal principal,
    IQuerySession session,
    FleetStatus? status = null,
    int? page = null,
    int? pageSize = null)
{
    var parameters = PaginationParameters.Create(
        page ?? PaginationParameters.DefaultPage,
        pageSize ?? PaginationParameters.DefaultPageSize);
    if (parameters is null)
    {
        return TypedResults.BadRequest("page and pageSize must be >= 1.");
    }

    var playerId = PlayerId(principal);
    var query = session.Query<Fleet>().Where(f => f.OwnerId == playerId);
    query = status is null
        ? query.Where(f => f.Status != FleetStatus.Disbanded)
        : query.Where(f => f.Status == status);

    var response = await query
        .OrderBy(f => f.AssembledAt).ThenBy(f => f.Id)
        .ToPagedResponseAsync(parameters,
            f => new FleetSummaryResponse(f.Id, f.OwnerId, f.Status, f.LocationPlanetId, f.AssembledAt, f.Ships.Count));
    return TypedResults.Ok(response);
}

// Universe-visible (full visibility, no fog of war in MVP).
[WolverineGet("/api/fleets/{fleetId}")]
public static async Task<Results<Ok<FleetResponse>, NotFound>> GetFleet(Guid fleetId, IQuerySession session)
{
    var fleet = await session.LoadAsync<Fleet>(fleetId);
    return fleet is null ? TypedResults.NotFound() : TypedResults.Ok(FleetResponse.From(fleet));
}

// Universe-visible: fleets currently stationed at this planet.
[WolverineGet("/api/planets/{planetId}/fleets")]
public static async Task<Results<Ok<PagedResponse<FleetSummaryResponse>>, NotFound, BadRequest<string>>> GetPlanetFleets(
    Guid planetId,
    IQuerySession session,
    int? page = null,
    int? pageSize = null)
{
    var planet = await session.LoadAsync<Planet>(planetId);
    if (planet is null)
    {
        return TypedResults.NotFound();
    }

    var parameters = PaginationParameters.Create(
        page ?? PaginationParameters.DefaultPage,
        pageSize ?? PaginationParameters.DefaultPageSize);
    if (parameters is null)
    {
        return TypedResults.BadRequest("page and pageSize must be >= 1.");
    }

    var response = await session.Query<Fleet>()
        .Where(f => f.LocationPlanetId == planetId && f.Status == FleetStatus.Stationed)
        .OrderBy(f => f.AssembledAt).ThenBy(f => f.Id)
        .ToPagedResponseAsync(parameters,
            f => new FleetSummaryResponse(f.Id, f.OwnerId, f.Status, f.LocationPlanetId, f.AssembledAt, f.Ships.Count));
    return TypedResults.Ok(response);
}
```

Note: if Marten cannot translate `f.Ships.Count` inside the projection lambda, materialize first (`ToPagedResponseAsync` already materializes the page — check `PaginationExtensions`; the selector runs in memory, so it's fine).

Then add `Guid? OwnerId` to `RosterShipResponse` (find its file with `grep -rn "record RosterShipResponse" src/`) and pass `s.OwnerId` in `ShipEndpoints.GetRoster`; update any test constructing `RosterShipResponse`.

- [ ] **Step 4: Run the Fleets test class**, expect PASS.

- [ ] **Step 5: Commit**

```bash
git add -A src/ && git commit -m "feat: fleet read endpoints — own list, detail, stationed-at-planet (#48)"
```

---

### Task 6: Round-trip merge gate, docs, PR

**Files:**
- Test: `src/Voidforge.Tests/Fleets/FleetRoundTripTests.cs`
- Modify: `game-design/fleets.md`, `technical-design/domain-model.md`, `technical-design/api-conventions.md`

- [ ] **Step 1: Write the merge-gate e2e test** (build ships → assemble → roster shrinks → disband → ships returned):

```csharp
[Fact]
public async Task ShipsRoundTripThroughAFleet()
{
    var registration = await RegisterPlayer();
    await BuildOperationalShipyard(registration);                 // helper per ShipConstructionCompletionTests
    var ship1 = await BuildRosterShip(registration);              // real scheduled completions (fast test balance)
    var ship2 = await BuildRosterShip(registration);

    var rosterBefore = await GetRoster(registration);
    Assert.Equal(2, rosterBefore.TotalItems);

    var fleet = await AssembleFleet(registration, [ship1, ship2]);
    Assert.Equal(FleetStatus.Stationed, fleet.Status);
    Assert.Equal(2, fleet.Ships.Count);
    Assert.Equal(0, (await GetRoster(registration)).TotalItems);  // roster shrank

    await Disband(registration, fleet.Id);

    var rosterAfter = await GetRoster(registration);
    Assert.Equal(2, rosterAfter.TotalItems);                      // ships returned
    Assert.All(rosterAfter.Items, s => Assert.Equal(registration.PlayerId, s.OwnerId));
}
```

(Use the actual property name for the player id on `RegisterPlayerResponse` — check the record.)

- [ ] **Step 2: Run it**, expect PASS (everything is already implemented; this is the gate, not new TDD).

- [ ] **Step 3: Update docs**
  - `game-design/fleets.md` (D6): in **Colonize**, replace "Remaining ships are added to the new planet's ship roster." with "The fleet remains **stationed** at the new colony; ships join the roster only via an explicit disband."; in **Ship Roster**, replace "When a fleet arrives and completes its mission, surviving ships are added to the destination planet's roster." with "An arriving fleet always ends **stationed** at the destination — ships reach the roster only when the fleet is explicitly disbanded."; in **Fleet Lifecycle** step 5, replace "Non-consumed ships are added to the destination planet's ship roster." with "The fleet stays stationed at the destination; disbanding it returns its ships to that planet's roster."
  - `technical-design/domain-model.md`: add a `### Fleet` aggregate section (events, snapshot fields, Marten config line, the assembly/disband two-stream transactions, D13 `RosterShip.OwnerId`) and note the new Planet events under the Planet section.
  - `technical-design/api-conventions.md`: extend the deterministic-order list with `GET /api/fleets` and `GET /api/planets/{planetId}/fleets` — by `AssembledAt`, then `Id`.

- [ ] **Step 4: Full verification**

```bash
dotnet test src/Voidforge.slnx 2>&1 | tail -3        # all green
dotnet format src/Voidforge.slnx                     # then re-run build if it changed files
```

- [ ] **Step 5: Commit docs + gate test, open PR into `phase-4`, self-merge on green CI**

```bash
git add -A && git commit -m "test+docs: fleet round-trip gate; D6 always-stationed rule; domain-model Fleet section (#48)"
git push -u origin feat/fleet-assembly
gh pr create --base phase-4 --title "feat: fleet assembly & disband (#48)" --body "Closes #48. ..."
```
