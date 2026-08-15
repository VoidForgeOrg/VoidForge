# #50 — Cargo, Unloading & the Transport Mission Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resources move physically between planets — loaded onto Cargo Vessels at assembly, delivered on Transport arrival (partial when storage is full), retryable via manual unload.

**Architecture:** Cargo is fleet-level totals (D8; per-ship binding is post-MVP, arriving with combat) loaded at assembly (D7). Planet storage mutations (`CargoLoadedFromStorage`/`CargoDeliveredToStorage`) checkpoint the affected pool at `at` under #44's non-regressing semantics, adjust `CheckpointValue` clamped to `[0, capacity]`, and never touch rates. Transport arrival is the codebase's first cross-aggregate append **from an asynchronous durable-message handler** (#48's assembly/disband already commit Planet + Fleet events together, but synchronously inside an endpoint): the handler fetches Fleet + destination Planet with `FetchForWriting` and commits both streams in one `SaveChangesAsync` (#39 retries a contested commit). One `CargoUnloaded` event carries accepted amounts (D9); disband refuses while cargo remains (D11).

**Tech Stack:** .NET 9, Marten, Wolverine, xUnit + Alba.

**Spec:** `plans/phase-4-fleets-expansion-design.md` §2.1, §2.3–§2.5, §5. **Precondition (controller-handled before this branch):** PR #47 (#44 non-regressing `ResourcePool`) merged into `phase-4` — `GetCurrentValue` floors elapsed at 0, `Checkpoint` never moves `CheckpointTime` backwards.

## Global Constraints

- MA0048 one public type per file; `TreatWarningsAsErrors`; `_`-prefixed static test fields; `dotnet format --verify-no-changes` clean before the PR.
- `FetchForWriting` at every existing-stream append; the arrival handler's two-stream write is ONE `SaveChangesAsync`.
- Storage mutations NEVER call `RebaseRates` and NEVER change `Rate` — cargo moves stored value, not building composition (spec §2.5).
- Balance from `IOptions<BalanceOptions>` at endpoints; domain stays config-free (capacity lookups injected like #49's `GetSpeed`).
- Error codes per spec §5; mutations 403 without ownership.
- TDD per task; commits conventional, suffixed `(#50)`.

## Plan-level decisions (within spec letter/spirit)

1. **`GetCargoCapacity(Func<ShipType, decimal> capacityOf)`** mirrors #49's injected-lookup deviation (spec sketches it parameterless; `FleetShip` carries no capacity).
2. **Manual unload of an empty fleet → 409** "No cargo aboard." (spec silent; explicit beats silent no-op).
3. **Undefined `MissionType` values → 400 "Unknown mission type."** via `Enum.IsDefined`, distinct from defined-but-unwired Colonize → 400 "Mission not supported yet." (carry-over from #49's final review).
4. **Unload arithmetic lives in `Planet.AcceptCargoDelivery`** (computes `accepted_r = min(cargo_r, headroom_r)` and returns the event carrying accepted amounts); `Fleet.UnloadCargo` records the matching decrement. The handler/endpoint never does storage math.

## File Structure

```text
src/Voidforge.Api/Domain/
  Fleet.cs                            (modify — cargo props, GetCargoCapacity/GetCargoLoad, UnloadCargo, D11 disband guard, Arrive contract comment)
  Planet.cs                           (modify — cargo storage Apply methods + factories, in the core file: they are pool mutations beside RebaseRates/CheckpointAllResources)
  Events/CargoLoaded.cs               (new)  — (IronOre, IronIngot, LoadedAt)
  Events/CargoUnloaded.cs             (new)  — (PlanetId, IronOre, IronIngot, UnloadedAt)
  Events/CargoLoadedFromStorage.cs    (new)  — (FleetId, IronOre, IronIngot, At)
  Events/CargoDeliveredToStorage.cs   (new)  — (FleetId, IronOre, IronIngot, At)
src/Voidforge.Api/Endpoints/
  FleetEndpoints.cs                   (modify — assembly cargo, Transport launch, unload endpoint, mission-enum guard)
  AssembleFleetRequest.cs             (modify — gains CargoRequest? Cargo)
  CargoRequest.cs                     (new)  — (decimal IronOre, decimal IronIngot)
  FleetResponse.cs                    (modify — cargoIronOre/cargoIronIngot/cargoCapacity)
  CompleteFleetArrivalHandler.cs      (modify — Transport delivery, cross-aggregate append)
src/Voidforge.Tests/
  Concurrency/FleetConcurrencyTests.cs      (modify — concurrent-launch race test)
  Travel/PlanetCoordinateApiTests.cs        (modify — read PlanetSpread from bound options)
  Cargo/FleetCargoDomainTests.cs            (new, unit)
  Cargo/PlanetStorageMutationTests.cs       (new, unit)
  Cargo/CargoEndpointTests.cs               (new, integration)
  Cargo/TransportMissionEndToEndTests.cs    (new, e2e merge gate)
technical-design/domain-model.md      (modify)
game-design/player-actions.md         (modify if it enumerates actions — verify)
```

---

### Task 1: #49 final-review carry-overs

**Files:** `Domain/Fleet.cs`, `Endpoints/FleetEndpoints.cs`, `Tests/Concurrency/FleetConcurrencyTests.cs`, `Tests/Travel/PlanetCoordinateApiTests.cs`, `Tests/Travel/FleetMissionEndpointTests.cs` (message assertion if any)

- [ ] **Step 1:** Add to the `Fleet.Arrive` doc comment: "Returns **Fleet-stream events only**; planet-side arrival effects are produced from the Planet aggregate and appended by the handler onto the planet's own stream."
- [ ] **Step 2 (failing test):** In `FleetMissionEndpointTests`, POST `{"mission": 99, "destinationPlanetId": "<any guid>"}` (use `s.Post.Json(new { mission = 99, destinationPlanetId = Guid.NewGuid() })`) → expect 400. Run: currently returns 400 with the WRONG body ("Mission not supported yet.") — assert the body contains "Unknown mission type." so the test fails RED.
- [ ] **Step 3:** In `Launch`, before the Move check: `if (!Enum.IsDefined(request.Mission)) { return TypedResults.BadRequest("Unknown mission type."); }` → GREEN.
- [ ] **Step 4 (race coverage):** Add `ConcurrentLaunchesYieldExactlyOneDeparture` to `FleetConcurrencyTests` mirroring the existing batching idiom: assemble one fleet, fire two concurrent `POST /missions` (Move, same valid destination), assert exactly one 200 and one 409, and GET shows `InTransit` with a single consistent `ArrivesAt`. Expect PASS (coverage of existing #39 behavior; if it FAILS, stop and escalate — real bug).
- [ ] **Step 5:** `PlanetCoordinateApiTests`: replace the `new WorldGenOptions().PlanetSpread` reference with `_host.Services.GetRequiredService<IOptions<WorldGenOptions>>().Value.PlanetSpread`.
- [ ] **Step 6:** Full suite + format clean. Commit `test+fix: launch race coverage, unknown-mission 400, Arrive contract comment (#50)`.

---

### Task 2: Fleet cargo domain (D8, D9, D11)

**Files:** `Domain/Fleet.cs`, `Events/CargoLoaded.cs`, `Events/CargoUnloaded.cs`; Test: `Tests/Cargo/FleetCargoDomainTests.cs`

**Interfaces:**
- `record CargoLoaded(decimal IronOre, decimal IronIngot, DateTimeOffset LoadedAt)`
- `record CargoUnloaded(Guid PlanetId, decimal IronOre, decimal IronIngot, DateTimeOffset UnloadedAt)`
- `Fleet.CargoIronOre { get; set; }`, `Fleet.CargoIronIngot { get; set; }` (decimals, snapshot fields)
- `Fleet.GetCargoCapacity(Func<ShipType, decimal> capacityOf) → decimal` — Σ over `Ships`
- `Fleet.GetCargoLoad() → decimal` — `CargoIronOre + CargoIronIngot`
- `Fleet.UnloadCargo(Guid planetId, decimal ironOre, decimal ironIngot, DateTimeOffset at) → CargoUnloaded` — throws `InvalidOperationException` if either amount is negative or exceeds what's aboard (programming error: accepted amounts are computed from this fleet's own cargo)
- `Apply(CargoLoaded)` increments; `Apply(CargoUnloaded)` decrements.
- `Disband` gains the D11 guard: `if (GetCargoLoad() > 0) throw new InvalidOperationException("Cannot disband a fleet with cargo aboard.");`

- [ ] **Step 1 (failing tests):** load → totals set; unload partial → decremented; unload more than aboard → throws; `GetCargoCapacity` sums via lookup (CargoVessel 500, ColonyShip 0 → mixed fleet of one each = 500); disband with cargo throws; disband after full unload succeeds. Build fleets via `Fleet.Assemble` + `Apply` per `FleetAggregateTests` style.
- [ ] **Step 2:** RED. — [ ] **Step 3:** Implement. — [ ] **Step 4:** GREEN + full suite.
- [ ] **Step 5:** Commit `feat: fleet-level cargo totals, unload event, D11 disband guard (#50)`.

---

### Task 3: Planet storage mutations (spec §2.5)

**Files:** `Domain/Planet.cs`, `Events/CargoLoadedFromStorage.cs`, `Events/CargoDeliveredToStorage.cs`; Test: `Tests/Cargo/PlanetStorageMutationTests.cs`

**Interfaces:**
- `record CargoLoadedFromStorage(Guid FleetId, decimal IronOre, decimal IronIngot, DateTimeOffset At)`
- `record CargoDeliveredToStorage(Guid FleetId, decimal IronOre, decimal IronIngot, DateTimeOffset At)`
- `Planet.LoadCargoFromStorage(Guid fleetId, decimal ironOre, decimal ironIngot, DateTimeOffset at) → CargoLoadedFromStorage` — throws `InvalidOperationException` if either amount is negative or exceeds `pool.GetCurrentValue(at)` (endpoint pre-validates for the 409; this is the defensive backstop)
- `Planet.AcceptCargoDelivery(Guid fleetId, decimal ironOre, decimal ironIngot, DateTimeOffset at) → CargoDeliveredToStorage` — computes `accepted_r = min(offered_r, max(0, capacity_r − pool_r.GetCurrentValue(at)))` per resource and returns the event **carrying the accepted amounts** (callers read them to build the matching `CargoUnloaded`)
- Both `Apply` methods: `pool = pool.Checkpoint(at)` (non-regressing, #44) then `pool = pool with { CheckpointValue = Math.Clamp(pool.CheckpointValue ± amount, 0, pool.StorageCapacity) }`. **`Rate` untouched; no `RebaseRates`** — comment why (composition-preserving, spec §2.5).

- [ ] **Step 1 (failing tests):** load subtracts at `at` (accrual up to `at` locked in first — planet with a Drill: pool value at `at` reflects production, then subtraction); delivery adds; exactly-full destination accepts 0; partial headroom accepts exactly the headroom; over-capacity offer clamps; rates unchanged after both Applies (assert `Rate` identical); backwards-`at` delivery still adjusts value without regressing `CheckpointTime` (construct pool checkpointed at T, deliver at T−5s, assert `CheckpointTime` still T and value adjusted).
- [ ] **Step 2:** RED. — [ ] **Step 3:** Implement. — [ ] **Step 4:** GREEN + full suite.
- [ ] **Step 5:** Commit `feat: planet storage mutations for cargo — checkpointed, rate-preserving (#50)`.

---

### Task 4: Cargo at assembly + manual unload endpoint

**Files:** `Endpoints/FleetEndpoints.cs`, `AssembleFleetRequest.cs`, `CargoRequest.cs` (new), `FleetResponse.cs`; Test: `Tests/Cargo/CargoEndpointTests.cs`

**Interfaces:**
- `record CargoRequest(decimal IronOre, decimal IronIngot)`; `AssembleFleetRequest(IReadOnlyList<Guid> ShipIds, CargoRequest? Cargo = null)`
- Assembly additions, in order after the existing ship checks (spec §2.3): cargo `null` or both amounts 0 → skip; negative amount → 400; total > `Σ balance.Ships.For(type).CargoCapacity` over selected ships → 400; planet not owned by caller → 403 (only when cargo requested); either amount > `pool.GetCurrentValue(now)` → 409. Then: planet stream additionally appends `LoadCargoFromStorage(...)`; fleet stream starts with `Fleet.Assemble(...)` followed by `new CargoLoaded(ore, ingot, now)` (both in the same `StartStream` call).
- `POST /api/fleets/{fleetId}/unload` → 200 `FleetResponse` | 403 (fleet or planet not owned) | 404 (fleet) | 409 (not `Stationed`; no cargo aboard). Flow: `FetchForWriting<Fleet>` → guards → `FetchForWriting<Planet>(LocationPlanetId)` → `planet.AcceptCargoDelivery(fleet.Id, fleet.CargoIronOre, fleet.CargoIronIngot, now)` → append it + `fleet.UnloadCargo(planetId, delivered.IronOre, delivered.IronIngot, now)` → one `SaveChangesAsync`. (Accepting 0 because storage is full is still 200 — the fleet state is the answer.)
- `FleetResponse` gains `decimal CargoIronOre, decimal CargoIronIngot, decimal CargoCapacity`; `From` gains a `Func<ShipType, decimal>` capacity lookup parameter (`FleetResponse.From(fleet, capacityOf)`) — update ALL call sites (compiler-guided; endpoints pass `t => balance.Ships.For(t).CargoCapacity`).

- [ ] **Step 1 (failing tests):** assemble with cargo → 200, response shows cargo + origin storage decremented (GET planet); cargo exceeding capacity → 400; negative → 400; cargo on foreign planet's roster ships → 403 (ships owned, planet not); insufficient stored → 409; unload with no cargo → 409; unload at foreign planet → 403; assemble→unload round-trip restores storage.
- [ ] **Step 2:** RED. — [ ] **Step 3:** Implement. — [ ] **Step 4:** GREEN + full suite + format.
- [ ] **Step 5:** Commit `feat: cargo loading at assembly + manual unload endpoint (#50)`.

---

### Task 5: Transport mission + the first cross-aggregate arrival append

**Files:** `Endpoints/FleetEndpoints.cs` (launch), `Endpoints/CompleteFleetArrivalHandler.cs`; Test: `Tests/Cargo/CargoEndpointTests.cs` (launch guards), handler-invoked tests in `Tests/Cargo/TransportMissionEndToEndTests.cs`

**Interfaces:**
- Launch: `Transport` becomes valid — additional guard after destination existence: `destination.OwnerId != fleet.OwnerId → 403` (checked at launch, re-checked on arrival). Colonize keeps 400 "Mission not supported yet."
- Handler, after appending `Arrive`'s events (capture `mission`, `destinationId`, and cargo amounts from the aggregate BEFORE appending — the in-memory aggregate still shows pre-arrival state):

```csharp
if (mission == MissionType.Transport && (cargoOre > 0 || cargoIngot > 0))
{
    var planetStream = await session.Events.FetchForWriting<Planet>(destinationId);
    var planet = planetStream.Aggregate;
    // Re-check on arrival (spec §2.4): cannot fail in MVP (planets never change hands),
    // but arrival is the honest place for the invariant; post-MVP combat makes it live.
    if (planet is not null && planet.OwnerId == fleet.OwnerId)
    {
        var delivered = planet.AcceptCargoDelivery(fleet.Id, cargoOre, cargoIngot, message.ArrivesAt);
        planetStream.AppendOne(delivered);
        stream.AppendOne(fleet.UnloadCargo(destinationId, delivered.IronOre, delivered.IronIngot, message.ArrivesAt));
    }
}
await session.SaveChangesAsync();   // ONE commit across both streams — first cross-aggregate append from an async durable-message handler
```

(Structure the handler so the single `SaveChangesAsync` at the end covers the arrival-only path too; keep the early no-op returns for null aggregate / empty events.)
- Move arrival: cargo untouched (already true — add a test proving it).

- [ ] **Step 1 (failing tests):** launch Transport to a foreign-owned destination → 403; launch Transport to own planet → 200 InTransit; handler-invoked Transport arrival → destination storage incremented by accepted amounts, fleet cargo zeroed, fleet Stationed (all via API); full-destination arrival → cargo stays aboard (accepted 0), fleet Stationed; Move arrival with cargo → cargo untouched.
- [ ] **Step 2:** RED. — [ ] **Step 3:** Implement. — [ ] **Step 4:** GREEN + full suite.
- [ ] **Step 5:** Commit `feat: Transport mission — cross-aggregate delivery on arrival (#50)`.

---

### Task 6: E2E merge gate + docs

**Files:** `Tests/Cargo/TransportMissionEndToEndTests.cs`, `technical-design/domain-model.md`, `game-design/player-actions.md` (verify)

- [ ] **Step 1 (merge-gate e2e, real scheduler):** (a) full run: register → shipyard → CargoVessel → colonize... (no second owned planet exists without #51 — use TWO planets both owned? Registration grants one homeworld only). **Arrangement:** register two fleets' worth on ONE player: transport requires a second OWNED planet — unavailable until #51. Therefore the e2e uses the handler-invoked path from Task 5 for delivery correctness, and the real-scheduler e2e covers: assemble-with-cargo at the homeworld → launch **Move** to a foreign planet (cargo rides along) → real arrival → cargo intact → launch Move back → manual unload at home → storage restored. Plus a real-scheduler **Transport-to-own-planet** variant is impossible pre-#51 — note that explicitly in the test file comment and rely on Task 5's handler-invoked Transport tests for the delivery math. The spec's "full-storage run leaves a remainder aboard" gate is covered in Task 5's handler-invoked test.
- [ ] **Step 2:** Run — PASS (investigate, don't weaken, on failure).
- [ ] **Step 3: Docs.** `domain-model.md`: cargo events on both streams, D11 disband guard, `FleetResponse` cargo block, manual unload endpoint, and a highlighted note that `CompleteFleetArrivalHandler` performs the codebase's first cross-aggregate append (both streams `FetchForWriting`, one `SaveChangesAsync`, #39 retries the collision). `player-actions.md`: add/verify load-at-assembly + manual unload wording. Check `game-design/fleets.md` Transport section is consistent (it already documents partial unload; verify only).
- [ ] **Step 4:** Full suite + `dotnet format --verify-no-changes`.
- [ ] **Step 5:** Commit `test+docs: cargo round-trip gate; cross-aggregate arrival documented (#50)` — no push, no PR (controller runs the whole-branch review first).

---

## Self-review notes (performed at plan-writing time)

- Spec coverage: D7 load-at-assembly (Task 4), D8 fleet-level totals + capacity (Task 2), D9 single CargoUnloaded with accepted amounts (Tasks 2/3/5), D11 disband guard (Task 2 + endpoint in Task 4), §2.5 checkpoint-preserving storage mutations under #44 (Task 3), §2.4 Transport launch/arrival re-check + headroom math (Task 5), §4 endpoints (Task 4), §5 error codes (Tasks 4/5), #49 carry-overs (Task 1).
- Known pre-#51 limitation called out explicitly: no real-scheduler Transport e2e (needs a second owned planet); delivery math is proven handler-invoked, and #51's plan should add the full-loop e2e.
- Embedded-code review: `CargoRequest` nullable on the request (no NRE — null means "no cargo"); negative-amount guards named; `FleetResponse.From` signature change is compiler-enforced across call sites; handler captures aggregate state before appends.
