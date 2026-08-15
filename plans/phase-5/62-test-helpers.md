# #62 — Shared Integration-Test Helpers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract the helper methods duplicated across 21 integration-test files into shared test support, and centralize the suite's poll intervals/timeouts as named constants.

**Architecture:** Extension methods on `IAlbaHost` in `Voidforge.Tests/Support/` — not a base class. Test classes keep their `[Collection]` attribute, constructor, and `_host` field untouched; each file's diff is "delete the private-helper region, prefix helper calls with `_host.`". All helpers take the `RegisterPlayerResponse` explicitly (no hidden state), so extensions compose exactly like the private methods did.

**Tech Stack:** .NET 9, xUnit 2.9, Alba 8.4, Marten. Analyzers: Roslynator + Meziantou, `TreatWarningsAsErrors`, MA0048 (one public type per file).

**Spec:** Issue #62; `plans/phase-5-hardening-design.md` §8 wave 0. Test-only churn — no production code changes.

## Global Constraints

- `TreatWarningsAsErrors` — new files must be analyzer-clean.
- MA0048 — one public type per file (`IntegrationApiExtensions.cs`, `TestTimeouts.cs`).
- **No behavior change to what tests assert.** Two deliberate, safe unifications (documented in decisions 2-3 below); everything else byte-equivalent.
- Never run `dotnet test` concurrently with anything (shared test DB; quality-gate Stop hook also runs the suite).
- Commits: conventional, suffixed `(#62)`.

## Plan-level decisions

1. **Extensions over inheritance.** A base class would force ctor chaining in 21 files, an `_host` → `Host` sweep through hundreds of inline scenarios, and reliance on xUnit inheriting `[Collection]`. Extensions touch none of that.
2. **`BuildRosterShip` unifies on ensure + roster-diff** (the ClaimRace/Colonize variant): call idempotent `EnsureOperationalShipyard`, snapshot roster ids, queue, return the first *new* id. This subsumes: variant A (no shipyard yet → ensure builds it; empty roster → diff returns the only ship), variant D in `FleetRoundTripTests` (shipyard already built → ensure is a no-op, no second shipyard), and typed variant B directly. `BuildRosterShips(count)` covers the batch case (30 s deadline, as today).
3. **`FindPlanetOtherThan` unifies on `?pageSize=200`** (`ShipEndpointTests` form). `BuildingEndpointTests`' copy omitted the page size and relied on the default page containing a non-home planet — strictly less reliable; unifying is a safe behavior change.
4. **Stays local (race- or file-specific):** `ClaimRaceTests.ArriveWithRetry` + `BuildAndLaunchColonizeFleet`; `ColonizeMissionTests.UncolonizedPlanetId` (raw-Marten single-file variant); `MoveMissionEndToEndTests.PickPlanetInAnotherSolarSystem` (ignores ownership — different semantics); `PlayerRegistrationTests` (asserts raw registration responses — untouched); the `Try*`/`*Status` wrappers in `Concurrency/*` (semantic names stay, bodies delegate to the shared `PostForStatus` primitive); `ColonizeSecondPlanetForOwner` (2 files, raw-Marten world mutation with a do-not-parallelize warning — the pair collapses when #67's fixtures land, not worth a `Store`-reaching shared helper now).
5. **Poll cadence unchanged** (500 ms), but named. Tightening intervals to speed the suite is deliberately out of scope — it changes DB load during polls and belongs with evidence, not inside a mechanical refactor.
6. **`RegisterPlayer` keeps per-file name prefixes** as an explicit argument — they make test-DB forensics readable; deriving from the class name risks tripping any name-length validation.

## File Structure

```text
src/Voidforge.Tests/Support/
  TestTimeouts.cs                (new — named poll interval + timeout constants)
  IntegrationApiExtensions.cs    (new — all shared helpers as IAlbaHost extensions)
plans/phase-5/62-test-helpers.md (this plan)
technical-design/testing.md      (modify — document the Support layer)
21 test files                    (modify — delete private helpers, prefix calls with _host.)
```

---

### Task 1: Support files

**Files:**
- Create: `src/Voidforge.Tests/Support/TestTimeouts.cs`
- Create: `src/Voidforge.Tests/Support/IntegrationApiExtensions.cs`

**Interfaces produced:** every signature below — later tasks rely on them verbatim.

- [ ] **Step 1: Write `TestTimeouts.cs`**

```csharp
namespace Voidforge.Tests.Support;

/// <summary>
/// Canonical wall-clock poll cadence and deadlines for the integration suite.
/// These time out real HTTP polling — unrelated to the app's injected TimeProvider.
/// </summary>
public static class TestTimeouts
{
    /// <summary>Delay between successive polls.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Building/ship construction completing onto the planet.</summary>
    public static readonly TimeSpan Completion = TimeSpan.FromSeconds(20);

    /// <summary>Resource stock recovering after a spend, and multi-ship queue settling.</summary>
    public static readonly TimeSpan StockRecovery = TimeSpan.FromSeconds(30);

    /// <summary>Draining a multi-ship build queue.</summary>
    public static readonly TimeSpan QueueDrain = TimeSpan.FromSeconds(40);

    /// <summary>Real-scheduler fleet arrival (short hops).</summary>
    public static readonly TimeSpan Arrival = TimeSpan.FromSeconds(30);

    /// <summary>Full-loop end-to-end arrival (longest travel in the suite).</summary>
    public static readonly TimeSpan FullLoopArrival = TimeSpan.FromSeconds(60);
}
```

- [ ] **Step 2: Write `IntegrationApiExtensions.cs`**

```csharp
using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Voidforge.Api.Auth;
using Voidforge.Api.Domain;
using Voidforge.Api.Endpoints;
using Voidforge.Api.Handlers;
using Xunit;

namespace Voidforge.Tests.Support;

/// <summary>
/// Shared API-driving helpers for the integration suite (#62). All helpers assert
/// success (200) unless the name says otherwise; polling helpers return the last
/// state on timeout so the caller's assertion reports the failure.
/// </summary>
public static class IntegrationApiExtensions
{
    public static async Task<RegisterPlayerResponse> RegisterPlayer(this IAlbaHost host, string namePrefix)
    {
        var result = await host.Scenario(s =>
        {
            s.Post.Json(new RegisterPlayerRequest($"{namePrefix}{Guid.NewGuid():N}"))
                .ToUrl("/api/players/register");
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<RegisterPlayerResponse>();
        Assert.NotNull(response);
        return response;
    }

    public static async Task<T> GetJson<T>(this IAlbaHost host, RegisterPlayerResponse asWhom, string url)
    {
        var result = await host.Scenario(s =>
        {
            s.Get.Url(url);
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, asWhom.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<T>();
        Assert.NotNull(response);
        return response;
    }

    public static Task<PlanetResponse> GetPlanet(this IAlbaHost host, RegisterPlayerResponse registration)
        => host.GetPlanetById(registration, registration.HomeworldId);

    public static Task<PlanetResponse> GetPlanetById(this IAlbaHost host, RegisterPlayerResponse asWhom, Guid planetId)
        => host.GetJson<PlanetResponse>(asWhom, $"/api/planets/{planetId}");

    public static Task<PagedResponse<RosterShipResponse>> GetRoster(
        this IAlbaHost host, RegisterPlayerResponse registration, Guid? planetId = null)
        => host.GetJson<PagedResponse<RosterShipResponse>>(
            registration, $"/api/planets/{planetId ?? registration.HomeworldId}/ships?pageSize=200");

    public static async Task<ShipBuildResponse> QueueShip(
        this IAlbaHost host, RegisterPlayerResponse registration, ShipType type)
    {
        var result = await host.Scenario(s =>
        {
            s.Post.Json(new QueueShipRequest(type))
                .ToUrl($"/api/planets/{registration.HomeworldId}/ship-queue");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var build = await result.ReadAsJsonAsync<ShipBuildResponse>();
        Assert.NotNull(build);
        return build;
    }

    /// <summary>Places a Shipyard only if the planet has none, then polls until it is Operational.</summary>
    public static async Task EnsureOperationalShipyard(this IAlbaHost host, RegisterPlayerResponse registration)
    {
        var planet = await host.GetPlanet(registration);
        if (!planet.Buildings.Any(b => b.Type == BuildingType.Shipyard))
        {
            await host.Scenario(s =>
            {
                s.Post.Json(new PlaceBuildingRequest(BuildingType.Shipyard))
                    .ToUrl($"/api/planets/{registration.HomeworldId}/buildings");
                s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
                s.StatusCodeShouldBe(200);
            });
        }

        await host.PollUntil(
            registration,
            p => p.Buildings.Any(b => b.Type == BuildingType.Shipyard && b.Status == BuildingStatus.Operational),
            TestTimeouts.Completion);
    }

    /// <summary>
    /// Ensures an operational shipyard, queues one ship, and returns the id of the ship
    /// that newly appears on the roster (diff-based, so pre-existing roster ships are fine).
    /// </summary>
    public static async Task<Guid> BuildRosterShip(
        this IAlbaHost host, RegisterPlayerResponse registration, ShipType type = ShipType.CargoVessel)
    {
        await host.EnsureOperationalShipyard(registration);

        var before = await host.GetRoster(registration);
        var known = before.Items.Select(s => s.Id).ToHashSet();
        await host.QueueShip(registration, type);

        var deadline = DateTime.UtcNow + TestTimeouts.Completion;
        do
        {
            var roster = await host.GetRoster(registration);
            var added = roster.Items.FirstOrDefault(s => !known.Contains(s.Id));
            if (added is not null)
            {
                return added.Id;
            }

            await Task.Delay(TestTimeouts.PollInterval);
        }
        while (DateTime.UtcNow < deadline);

        throw new InvalidOperationException("Ship did not complete onto the roster in time.");
    }

    /// <summary>Queues <paramref name="count"/> CargoVessels and waits for all of them to reach the roster.</summary>
    public static async Task<IReadOnlyList<Guid>> BuildRosterShips(
        this IAlbaHost host, RegisterPlayerResponse registration, int count)
    {
        await host.EnsureOperationalShipyard(registration);

        var before = await host.GetRoster(registration);
        var known = before.Items.Select(s => s.Id).ToHashSet();
        for (var i = 0; i < count; i++)
        {
            await host.QueueShip(registration, ShipType.CargoVessel);
        }

        var deadline = DateTime.UtcNow + TestTimeouts.StockRecovery;
        do
        {
            var roster = await host.GetRoster(registration);
            var added = roster.Items.Where(s => !known.Contains(s.Id)).Select(s => s.Id).ToList();
            if (added.Count >= count)
            {
                return added;
            }

            await Task.Delay(TestTimeouts.PollInterval);
        }
        while (DateTime.UtcNow < deadline);

        throw new InvalidOperationException($"Queued {count} ships did not all reach the roster in time.");
    }

    public static async Task<FleetResponse> AssembleFleet(
        this IAlbaHost host,
        RegisterPlayerResponse registration,
        IReadOnlyList<Guid> shipIds,
        CargoRequest? cargo = null,
        Guid? planetId = null)
    {
        var result = await host.Scenario(s =>
        {
            s.Post.Json(new AssembleFleetRequest(shipIds, cargo))
                .ToUrl($"/api/planets/{planetId ?? registration.HomeworldId}/fleets");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var fleet = await result.ReadAsJsonAsync<FleetResponse>();
        Assert.NotNull(fleet);
        return fleet;
    }

    public static async Task<FleetResponse> Launch(
        this IAlbaHost host,
        RegisterPlayerResponse registration,
        Guid fleetId,
        MissionType mission,
        Guid destinationPlanetId)
    {
        var result = await host.Scenario(s =>
        {
            s.Post.Json(new LaunchMissionRequest(mission, destinationPlanetId))
                .ToUrl($"/api/fleets/{fleetId}/missions");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var fleet = await result.ReadAsJsonAsync<FleetResponse>();
        Assert.NotNull(fleet);
        return fleet;
    }

    public static async Task<FleetResponse> Disband(
        this IAlbaHost host, RegisterPlayerResponse registration, Guid fleetId)
    {
        var result = await host.Scenario(s =>
        {
            s.Post.Url($"/api/fleets/{fleetId}/disband");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });

        var fleet = await result.ReadAsJsonAsync<FleetResponse>();
        Assert.NotNull(fleet);
        return fleet;
    }

    public static async Task Unload(this IAlbaHost host, RegisterPlayerResponse registration, Guid fleetId)
    {
        await host.Scenario(s =>
        {
            s.Post.Url($"/api/fleets/{fleetId}/unload");
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.StatusCodeShouldBe(200);
        });
    }

    /// <summary>Polls the planet until ore and ingot stocks reach the minimums; asserts on timeout.</summary>
    public static async Task<PlanetResponse> WaitForStock(
        this IAlbaHost host, RegisterPlayerResponse registration, decimal minOre, decimal minIngot)
    {
        var planet = await host.PollUntil(
            registration,
            p => p.IronOre.CurrentValue >= minOre && p.IronIngot.CurrentValue >= minIngot,
            TestTimeouts.StockRecovery);

        Assert.True(
            planet.IronOre.CurrentValue >= minOre && planet.IronIngot.CurrentValue >= minIngot,
            $"Stock did not recover in time: ore={planet.IronOre.CurrentValue} (need {minOre}), " +
            $"ingot={planet.IronIngot.CurrentValue} (need {minIngot}).");

        return planet;
    }

    /// <summary>
    /// Polls the planet (homeworld unless <paramref name="planetId"/> is given) until the
    /// predicate holds. Returns the last-seen state on timeout — callers assert and report.
    /// </summary>
    public static async Task<PlanetResponse> PollUntil(
        this IAlbaHost host,
        RegisterPlayerResponse registration,
        Func<PlanetResponse, bool> predicate,
        TimeSpan timeout,
        Guid? planetId = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        PlanetResponse planet;
        do
        {
            planet = await host.GetPlanetById(registration, planetId ?? registration.HomeworldId);
            if (predicate(planet))
            {
                return planet;
            }

            await Task.Delay(TestTimeouts.PollInterval);
        }
        while (DateTime.UtcNow < deadline);

        return planet;
    }

    public static async Task<FleetResponse> PollFleetUntil(
        this IAlbaHost host,
        RegisterPlayerResponse registration,
        Guid fleetId,
        Func<FleetResponse, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        FleetResponse fleet;
        do
        {
            fleet = await host.GetJson<FleetResponse>(registration, $"/api/fleets/{fleetId}");
            if (predicate(fleet))
            {
                return fleet;
            }

            await Task.Delay(TestTimeouts.PollInterval);
        }
        while (DateTime.UtcNow < deadline);

        return fleet;
    }

    /// <summary>
    /// Launches the mission, then completes the arrival immediately by invoking the
    /// handler directly with the scheduled ArrivesAt — no wall-clock wait.
    /// </summary>
    public static async Task<FleetResponse> LaunchAndArriveInstantly(
        this IAlbaHost host,
        RegisterPlayerResponse registration,
        Guid fleetId,
        MissionType mission,
        Guid destinationPlanetId)
    {
        var launched = await host.Launch(registration, fleetId, mission, destinationPlanetId);
        Assert.NotNull(launched.ArrivesAt);

        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();
        await CompleteFleetArrivalHandler.Handle(
            new CompleteFleetArrival(fleetId, launched.ArrivesAt.Value), session);

        return await host.GetJson<FleetResponse>(registration, $"/api/fleets/{fleetId}");
    }

    /// <summary>POSTs and returns the raw status code — for race tests that expect non-200s.</summary>
    public static async Task<int> PostForStatus(
        this IAlbaHost host, RegisterPlayerResponse registration, string url, object payload)
    {
        var result = await host.Scenario(s =>
        {
            s.Post.Json(payload).ToUrl(url);
            s.WithRequestHeader(ApiKeyAuthenticationDefaults.HeaderName, registration.ApiKey);
            s.IgnoreStatusCode();
        });

        return result.Context.Response.StatusCode;
    }

    /// <summary>First planet in the universe that is not the caller's homeworld.</summary>
    public static async Task<Guid> FindPlanetOtherThan(this IAlbaHost host, RegisterPlayerResponse registration)
    {
        var systems = await host.GetJson<PagedResponse<SolarSystemResponse>>(
            registration, "/api/solar-systems?pageSize=200");
        var planetId = systems.Items
            .SelectMany(sys => sys.PlanetIds)
            .First(id => id != registration.HomeworldId);
        return planetId;
    }

    /// <summary>
    /// Scans the public API for an unowned planet, optionally excluding one solar system.
    /// Throws if the universe has none.
    /// </summary>
    public static async Task<Guid> FindUncolonizedPlanet(
        this IAlbaHost host, RegisterPlayerResponse asWhom, Guid? excludeSystemId = null)
    {
        var systems = await host.GetJson<PagedResponse<SolarSystemResponse>>(
            asWhom, "/api/solar-systems?pageSize=200");
        foreach (var system in systems.Items)
        {
            if (system.Id == excludeSystemId)
            {
                continue;
            }

            foreach (var planetId in system.PlanetIds)
            {
                var planet = await host.GetPlanetById(asWhom, planetId);
                if (planet.OwnerId is null)
                {
                    return planetId;
                }
            }
        }

        throw new InvalidOperationException("No uncolonized planet found in the universe.");
    }
}
```

> Compile-check note: exact DTO namespaces (`RegisterPlayerRequest`, `SolarSystemResponse`, `CompleteFleetArrival`, `CompleteFleetArrivalHandler`, `CargoRequest`) must match the API project — fix usings on first build, do not fully-qualify inline. The `Unload`/`Disband` POST-without-body shape must match the existing local helpers (verify one before deleting).

- [ ] **Step 3: Build**

Run: `dotnet build src/Voidforge.slnx`
Expected: clean (analyzers on). Nothing consumes the new files yet.

- [ ] **Step 4: Commit**

```bash
git add src/Voidforge.Tests/Support plans/phase-5/62-test-helpers.md
git commit -m "test: shared integration-test helper extensions + named timeouts (#62)"
```

---

### Task 2: Migrate Fleets/ + Concurrency/ (4 files)

**Files:** `Fleets/FleetEndpointTests.cs`, `Fleets/FleetRoundTripTests.cs`, `Concurrency/FleetConcurrencyTests.cs`, `Concurrency/SameStreamConcurrencyTests.cs`

- [ ] **Step 1:** In each file: add `using Voidforge.Tests.Support;`, delete the private copies of `RegisterPlayer`/`GetJson`/`GetPlanet`/`GetRoster`/`QueueShip`/`BuildOperationalShipyard`/`BuildRosterShip`/`AssembleFleet`/`Disband`/`PollUntil`, prefix every call with `_host.` and pass each file's existing name-prefix literal to `RegisterPlayer("…")`. Replace hardcoded `TimeSpan.FromSeconds(20/30)` poll args at call sites with `TestTimeouts.Completion` / `TestTimeouts.StockRecovery`.
- [ ] **Step 2:** File-specific: `FleetRoundTripTests` — its local `BuildRosterShip` (variant D) is deleted; the body's one explicit `BuildOperationalShipyard` call becomes `_host.EnsureOperationalShipyard`; the shared diff-based helper preserves its semantics. `FleetConcurrencyTests`/`SameStreamConcurrencyTests` — keep `TryAssemble`/`TryDisband`/`TryLaunch`/`QueueShipStatus` as local one-liners delegating to `_host.PostForStatus(...)`.
- [ ] **Step 3:** `dotnet build src/Voidforge.slnx` → clean.
- [ ] **Step 4:** Commit: `test: migrate Fleets + Concurrency suites to shared helpers (#62)`

---

### Task 3: Migrate Cargo/ (3 files)

**Files:** `Cargo/CargoEndpointTests.cs`, `Cargo/TransportMissionEndToEndTests.cs`, `Cargo/TransportMissionEndpointTests.cs`

- [ ] **Step 1:** Same mechanical sweep as Task 2 (including `Launch`, `Unload`, `WaitForStock`, `PollFleetUntil`, `GetPlanetById`, `LaunchAndArriveInstantly`).
- [ ] **Step 2:** File-specific: `CargoEndpointTests` — `BuildRosterShips(count)` maps to the shared batch helper; `MoveAndArriveInstantly` becomes a local one-liner over `_host.LaunchAndArriveInstantly(reg, fleetId, MissionType.Move, dest)` (ignore the return); its `WaitForStock` callers keep using the returned planet. `TransportMissionEndpointTests` keeps `ColonizeSecondPlanetForOwner` local (decision 4). `_arrivalTimeout` fields switch to `TestTimeouts.Arrival`.
- [ ] **Step 3:** `dotnet build src/Voidforge.slnx` → clean.
- [ ] **Step 4:** Commit: `test: migrate Cargo suites to shared helpers (#62)`

---

### Task 4: Migrate Colonize/ (3 files)

**Files:** `Colonize/ClaimRaceTests.cs`, `Colonize/ColonizeMissionTests.cs`, `Colonize/FullLoopEndToEndTests.cs`

- [ ] **Step 1:** Same mechanical sweep. `EnsureOperationalShipyard` copies delete cleanly (shared one is the same shape). Typed `BuildRosterShip(reg, type)` calls map 1:1.
- [ ] **Step 2:** File-specific: `ClaimRaceTests` keeps `ArriveWithRetry` + `BuildAndLaunchColonizeFleet` local; its `UncolonizedPlanetId(asWhom)` becomes `_host.FindUncolonizedPlanet(asWhom)`. `ColonizeMissionTests` keeps its raw-Marten `UncolonizedPlanetId()` local (decision 4) and keeps `ColonizeSecondPlanetForOwner` local. `FullLoopEndToEndTests` — `UncolonizedPlanetInAnotherSystem(asWhom, homeSystemId)` becomes `_host.FindUncolonizedPlanet(asWhom, excludeSystemId: homeSystemId)`; `_arrivalTimeout` → `TestTimeouts.FullLoopArrival`.
- [ ] **Step 3:** `dotnet build src/Voidforge.slnx` → clean.
- [ ] **Step 4:** Commit: `test: migrate Colonize suites to shared helpers (#62)`

---

### Task 5: Migrate Travel/ (3 files)

**Files:** `Travel/FleetMissionEndpointTests.cs`, `Travel/MoveMissionEndToEndTests.cs`, `Travel/PlanetCoordinateApiTests.cs`

- [ ] **Step 1:** Same mechanical sweep.
- [ ] **Step 2:** File-specific: `MoveMissionEndToEndTests` — `GetRosterAt(reg, planetId)` → `_host.GetRoster(reg, planetId)`; its `PollUntil` variant maps to the shared one (homeworld default); `Task Disband` callers call the shared `Disband` and discard the result with `_ =`; keeps `PickPlanetInAnotherSolarSystem` local; `_arrivalTimeout` → `TestTimeouts.Arrival`. `PlanetCoordinateApiTests` — only `RegisterPlayer` migrates; the `_planetSpread` ctor stays.
- [ ] **Step 3:** `dotnet build src/Voidforge.slnx` → clean.
- [ ] **Step 4:** Commit: `test: migrate Travel suites to shared helpers (#62)`

---

### Task 6: Migrate remaining suites (7 files)

**Files:** `Buildings/BuildingEndpointTests.cs`, `Construction/BuildingConstructionCompletionTests.cs`, `Energy/EnergyGridTests.cs`, `Ships/ShipConstructionCompletionTests.cs`, `Ships/ShipEndpointTests.cs`, `Planets/PlanetEndpointTests.cs`, `Pagination/SolarSystemPaginationTests.cs`

- [ ] **Step 1:** Same mechanical sweep (`RegisterPlayer`, `GetPlanet`, `PollUntil`, `QueueShip`, `GetRoster`, `BuildOperationalShipyard` → `EnsureOperationalShipyard`, `CancelBuild` stays local in `ShipConstructionCompletionTests`).
- [ ] **Step 2:** File-specific: both `FindPlanetOtherThan` copies → shared (Buildings' gains `pageSize=200`, decision 3). `ShipConstructionCompletionTests` 40 s timeouts → `TestTimeouts.QueueDrain`. `SolarSystemPaginationTests` — `RegisterAndGetKey()` becomes `(await _host.RegisterPlayer("Pg_Test_")).ApiKey`; `GetPage(string apiKey, …)` stays local. `PlayerRegistrationTests` is deliberately untouched.
- [ ] **Step 3:** `dotnet build src/Voidforge.slnx` → clean. Then grep for leftovers: `grep -rn "private async Task<RegisterPlayerResponse> RegisterPlayer\|private async Task<PlanetResponse> PollUntil" src/Voidforge.Tests` → no hits.
- [ ] **Step 4:** Commit: `test: migrate remaining suites to shared helpers; drop last duplicates (#62)`

---

### Task 7: Full suite, format, docs

- [ ] **Step 1:** `timeout 900 dotnet test src/Voidforge.slnx` → all green (no concurrent runs; Wolverine teardown hang is cosmetic per `testing.md`).
- [ ] **Step 2:** `dotnet format src/Voidforge.slnx` → commit any churn.
- [ ] **Step 3:** Update `technical-design/testing.md`: add a "Shared helpers (`Support/`)" section — `IntegrationApiExtensions` (all API-driving helpers, assert-200, poll-return-last), `TestTimeouts` (named cadences), and the rule: *new integration tests must use the shared helpers; add to them rather than re-declaring privately*.
- [ ] **Step 4:** Commit: `test+docs: shared-helper conventions in testing.md (#62)`

---

### Task 8: PR and merge

- [ ] **Step 1:** Push `chore/62-test-helpers`; open PR → base `phase-5`, title `test: extract shared integration-test helpers (#62)`, body: summary + decisions 1-6 + "Closes #62".
- [ ] **Step 2:** `gh pr checks --watch` → green.
- [ ] **Step 3:** `gh pr merge --merge`.
