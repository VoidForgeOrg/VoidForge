# #69 — Storage Caps & Halting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development — dispatch a fresh subagent per task, review between. Steps use `- [ ]`.

**Goal:** When a producer's **output** storage pool reaches capacity, the producer **halts** (drops to a 5% energy draw, stops producing) and **resumes** automatically when the pool frees up. Delivers the reusable halt/resume/schedule machinery the rest of the spine (#70 depletion, #71 cascades) builds on.

**Architecture:** Halts/resumes are explicit domain events on the `Planet` stream, appended either by a scheduled `CheckStorageFull` message (ADR 0001: schedule at predicted fill time → validate-on-arrival → append → `RebaseRates` → reschedule) or by any commit that frees storage. `BuildingStatus` gains `Halted`; halted buildings leave the Operational set (so they stop producing and stop drawing full energy) but contribute a separate 5% draw term. Because `RebaseRates` re-derives all rates + the productivity multiplier from scratch on every composition change, energy cascades (drill halts → load drops → overload resolves) collapse into one re-derivation.

**Tech Stack:** .NET 9, Marten (event sourcing + inline snapshot), Wolverine (HTTP + durable scheduled messages), xUnit + Alba.

**Spec:** `plans/phase-5-hardening-design.md` §2, decisions **D1, D2, D3, D6**. **Scope (see #69 comments):** output-storage-full halting + machinery only. **All** input-starvation (refinery-ore and zero-ingot) is #70. The `InputStarved`/`ResourceDepleted` `HaltReason` values are *defined* here but only `OutputStorageFull` is *triggered*.

## Global Constraints
- `TreatWarningsAsErrors`; MA0048 one-public-type-per-file (each enum/record/event = its own file); MA0051 methods ≤60 lines.
- Every append site uses `session.Events.FetchForWriting<Planet>(id)` (#39 optimistic concurrency + the Program.cs retry ladder).
- New scheduled message + handler need **no** Marten/Wolverine registration — Wolverine auto-discovers static `Handle`; Marten auto-discovers `Apply`.
- Deterministic tests: pure-domain unit tests for the logic; integration tests drive real wall-clock with short fixture durations OR invoke `CheckStorageFullHandler.Handle(...)` directly with the predicted time (mirrors `IntegrationApiExtensions.LaunchAndArriveInstantly`). No `FakeTimeProvider` exists.
- Branch `feat/69-storage-halting` off `phase-5`; commits conventional, suffixed `(#69)`; one PR "Closes #69". Verify locally with `dotnet build -warnaserror` only — CI runs the suite.

## Producer → output-pool mapping (the core rule)
| Building | Produces into | Output-halts when |
|---|---|---|
| Drill | `IronOre` | `IronOre.GetCurrentValue(now) >= IronOre.StorageCapacity` |
| Refinery | `IronIngot` | `IronIngot.GetCurrentValue(now) >= IronIngot.StorageCapacity` |
| Generator, Shipyard | — (no stored output) | never output-halt |

## File Structure
```text
src/Voidforge.Api/Domain/
  ResourceType.cs            (new enum: IronOre, IronIngot)
  HaltReason.cs              (new enum: OutputStorageFull, InputStarved, ResourceDepleted)
  BuildingStatus.cs          (modify: add Halted)
  BuildingSlot.cs            (modify: add HaltReason? HaltReason = null)
  BuildingSpecs.cs           (modify: add HaltedDrawFactor = 0.05m; ProducedResource(type) helper)
  StorageDeadline.cs         (new record: ResourceType Resource, DateTimeOffset At)
  Planet.Halting.cs          (new partial: EvaluateStorageHalts / EvaluateStorageResumes / PredictStorageDeadlines + Apply(BuildingHalted/BuildingResumed))
  Planet.cs                  (modify: RebaseRates uses Operational-only already; add halted 5% draw hook via Energy)
  Planet.Energy.cs           (modify: add halted-building 5% draw term)
  Events/BuildingHalted.cs   (new)
  Events/BuildingResumed.cs  (new)
  Events/CheckStorageFull.cs (new scheduled message)
src/Voidforge.Api/Endpoints/
  CheckStorageFullHandler.cs (new: validate-on-arrival, append, reschedule)
  StorageHaltScheduling.cs   (new helper: schedule CheckStorageFull per predicted deadline)
  BuildingSlotResponse.cs    (modify: surface HaltReason)
  PlanetResponse.cs          (modify: map HaltReason)
  BuildingEndpoints.cs, ShipEndpoints.cs, CompleteBuildingConstructionHandler.cs,
  CompleteShipConstructionHandler.cs  (modify: after commit, schedule storage-full checks)
src/Voidforge.Tests/  (new unit + integration tests per task)
```

---

### Task 1: Domain scaffolding, events, energy/rate integration, evaluation methods (pure domain, unit-tested)

**Files:** create `ResourceType.cs`, `HaltReason.cs`, `StorageDeadline.cs`, `Events/BuildingHalted.cs`, `Events/BuildingResumed.cs`, `Planet.Halting.cs`; modify `BuildingStatus.cs`, `BuildingSlot.cs`, `BuildingSpecs.cs`, `Planet.Energy.cs`. Test: `Tests/Planets/PlanetHaltingTests.cs`.

**Interfaces produced (later tasks depend on these exact names):**
- `enum ResourceType { IronOre, IronIngot }`
- `enum HaltReason { OutputStorageFull, InputStarved, ResourceDepleted }`
- `BuildingStatus.Halted`
- `BuildingSlot(... , HaltReason? HaltReason = null)`
- `BuildingSpecs.HaltedDrawFactor` (`0.05m`), `BuildingSpecs.ProducedResource(BuildingType) -> ResourceType?`
- `record StorageDeadline(ResourceType Resource, DateTimeOffset At)`
- `BuildingHalted(int SlotIndex, HaltReason Reason, DateTimeOffset At)`, `BuildingResumed(int SlotIndex, DateTimeOffset At)`
- `Planet.EvaluateStorageHalts(DateTimeOffset now) -> IReadOnlyList<object>`
- `Planet.EvaluateStorageResumes(DateTimeOffset now) -> IReadOnlyList<object>`
- `Planet.PredictStorageDeadlines(DateTimeOffset now) -> IReadOnlyList<StorageDeadline>`
- `Planet.Apply(BuildingHalted)`, `Planet.Apply(BuildingResumed)` (each ends with `RebaseRates(@event.At)`)

- [ ] **Step 1 — enums/specs/slot.** Add `Halted` to `BuildingStatus`. New `ResourceType`, `HaltReason` enums. Add `HaltReason? HaltReason = null` to the `BuildingSlot` record (after `ConstructionDrainPerSecond`). In `BuildingSpecs`: `public const decimal HaltedDrawFactor = 0.05m;` and
```csharp
public static ResourceType? ProducedResource(BuildingType type) => type switch
{
    BuildingType.Drill => ResourceType.IronOre,
    BuildingType.Refinery => ResourceType.IronIngot,
    _ => null,
};
```

- [ ] **Step 2 — energy 5% draw.** In `Planet.Energy.cs`, add halted buildings' draw into `GetEnergyConsumptionMw` as a distinct term (they are NOT in the Operational set):
```csharp
var haltedDraw = Buildings
    .Where(b => b.Status == BuildingStatus.Halted)
    .Sum(b => BuildingSpecs.HaltedDrawFactor * BuildingSpecs.EnergyDrawMw(b.Type));
```
Add `haltedDraw` to the returned total (both the early `shipyardCount == 0` return and the final return). Generation is unaffected (halted producers already excluded by the `Operational` filter). Confirm `RebaseRates` needs no change — it already filters `Status == Operational`, so halted producers automatically drop out of `oreInflow`/`refineryDemand`.

- [ ] **Step 3 — evaluation + prediction + Apply (new partial `Planet.Halting.cs`).**
```csharp
// EvaluateStorageHalts: an Operational producer whose OUTPUT pool is at capacity halts.
public IReadOnlyList<object> EvaluateStorageHalts(DateTimeOffset now)
{
    var events = new List<object>();
    for (var i = 0; i < Buildings.Count; i++)
    {
        var slot = Buildings[i];
        if (slot.Status != BuildingStatus.Operational) continue;
        var produced = BuildingSpecs.ProducedResource(slot.Type);
        if (produced is null) continue;
        var pool = PoolFor(produced.Value);
        if (pool.GetCurrentValue(now) >= pool.StorageCapacity)
            events.Add(new BuildingHalted(i, HaltReason.OutputStorageFull, now));
    }
    return events;
}

// EvaluateStorageResumes: a building halted OutputStorageFull whose output pool now has headroom resumes.
public IReadOnlyList<object> EvaluateStorageResumes(DateTimeOffset now)
{
    var events = new List<object>();
    for (var i = 0; i < Buildings.Count; i++)
    {
        var slot = Buildings[i];
        if (slot.Status != BuildingStatus.Halted || slot.HaltReason != HaltReason.OutputStorageFull) continue;
        var produced = BuildingSpecs.ProducedResource(slot.Type);
        if (produced is null) continue;
        var pool = PoolFor(produced.Value);
        if (pool.GetCurrentValue(now) < pool.StorageCapacity)
            events.Add(new BuildingResumed(i, now));
    }
    return events;
}

// PredictStorageDeadlines: per pool with positive net rate and below capacity, time-to-full.
public IReadOnlyList<StorageDeadline> PredictStorageDeadlines(DateTimeOffset now)
{
    var deadlines = new List<StorageDeadline>();
    foreach (var (resource, pool) in new[] { (ResourceType.IronOre, IronOre), (ResourceType.IronIngot, IronIngot) })
    {
        if (pool.Rate <= 0) continue;
        var current = pool.GetCurrentValue(now);
        if (current >= pool.StorageCapacity) continue;
        var seconds = (double)((pool.StorageCapacity - current) / pool.Rate);
        deadlines.Add(new StorageDeadline(resource, now.AddSeconds(seconds)));
    }
    return deadlines;
}

private ResourcePool PoolFor(ResourceType r) => r == ResourceType.IronOre ? IronOre : IronIngot;

public void Apply(BuildingHalted @event)
{
    var slot = Buildings[@event.SlotIndex];
    Buildings[@event.SlotIndex] = slot with { Status = BuildingStatus.Halted, HaltReason = @event.Reason };
    RebaseRates(@event.At);
}

public void Apply(BuildingResumed @event)
{
    var slot = Buildings[@event.SlotIndex];
    Buildings[@event.SlotIndex] = slot with { Status = BuildingStatus.Operational, HaltReason = null };
    RebaseRates(@event.At);
}
```
(`RebaseRates` and `PoolFor` visibility: `RebaseRates` is private in `Planet.cs` — same partial class, so accessible. If a helper is needed by handlers, keep it public.)

- [ ] **Step 4 — unit tests** in `Tests/Planets/PlanetHaltingTests.cs` (follow `PlanetAggregateTests`/`PlanetEnergyTests` in-memory style: build a colonized planet, `Apply` events, assert). Cover: (a) a Drill on a planet whose `IronOre` is at capacity → `EvaluateStorageHalts` emits `BuildingHalted(Drill slot, OutputStorageFull)`; (b) `Apply(BuildingHalted)` sets `Halted`+reason, drops ore inflow to 0 in `RebaseRates`, and the halted Drill now draws `0.05*20=1 MW` (assert via `GetEnergyConsumptionMw` delta); (c) `EvaluateStorageResumes` emits `BuildingResumed` once the pool is below cap, and `Apply(BuildingResumed)` restores Operational + rate; (d) `PredictStorageDeadlines` returns the correct `(capacity-current)/rate` instant for a filling pool and nothing for a full/negative-rate pool; (e) halting a Drill frees energy that lifts the productivity multiplier (cascade-in-one-rederivation).

- [ ] **Step 5** — `dotnet build -warnaserror` clean. Commit: `feat: halt/resume domain — BuildingStatus.Halted, events, 5% draw, storage-full evaluation (#69)`.

---

### Task 2: `CheckStorageFull` scheduled message, handler, scheduling wiring, read DTO

**Files:** create `Events/CheckStorageFull.cs`, `Endpoints/CheckStorageFullHandler.cs`, `Endpoints/StorageHaltScheduling.cs`; modify `BuildingSlotResponse.cs`, `PlanetResponse.cs`, and the four commit sites (`BuildingEndpoints.cs`, `ShipEndpoints.cs`, `CompleteBuildingConstructionHandler.cs`, `CompleteShipConstructionHandler.cs`).

**Interfaces produced:**
- `CheckStorageFull(Guid PlanetId, ResourceType Resource, DateTimeOffset PredictedAt)`
- `StorageHaltScheduling.ScheduleDeadlinesAsync(IMessageBus bus, Guid planetId, IReadOnlyList<StorageDeadline> deadlines)`

- [ ] **Step 1 — message + handler.** `CheckStorageFull` record (shape copies `CompleteBuildingConstruction`). Handler (copies `CompleteBuildingConstructionHandler`):
```csharp
public static class CheckStorageFullHandler
{
    public static async Task Handle(CheckStorageFull message, IDocumentSession session, IMessageBus bus)
    {
        var stream = await session.Events.FetchForWriting<Planet>(message.PlanetId);
        var planet = stream.Aggregate;
        if (planet is null) return;

        // Validate-on-arrival: re-derive halts at the scheduled instant; if nothing is actually
        // at capacity now (rates changed since prediction), this is a no-op.
        var halts = planet.EvaluateStorageHalts(message.PredictedAt);
        if (halts.Count > 0) stream.AppendMany([.. halts]);

        // Re-read to reflect the appended halts, then reschedule the next deadlines.
        await session.SaveChangesAsync();
        await StorageHaltScheduling.ScheduleDeadlinesAsync(bus, message.PlanetId, planet.PredictStorageDeadlines(message.PredictedAt));
    }
}
```
Note: after appending halts and `RebaseRates`, `planet` (the in-memory aggregate) reflects the new rates, so `PredictStorageDeadlines` there is correct for rescheduling. Verify the aggregate instance is mutated by `AppendMany`+save (Marten inline) or re-fetch if needed — the subagent must confirm whether `stream.Aggregate` is live-updated post-append; if not, recompute from a fresh `FetchLatest`/`AggregateStreamAsync`. **This is the one correctness subtlety in Task 2 — resolve it explicitly.**

- [ ] **Step 2 — scheduling helper** `StorageHaltScheduling.ScheduleDeadlinesAsync`: for each `StorageDeadline`, `await bus.ScheduleAsync(new CheckStorageFull(planetId, d.Resource, d.At), d.At);`. (Model: `ShipConstructionScheduling`.)

- [ ] **Step 3 — wire into commit sites.** After the existing `SaveChangesAsync` in each of `BuildingEndpoints.Place`, `ShipEndpoints` queue, `CompleteBuildingConstructionHandler`, `CompleteShipConstructionHandler` (all change composition → new rates → new fill deadlines), call `StorageHaltScheduling.ScheduleDeadlinesAsync(bus, planetId, planet.PredictStorageDeadlines(now))`. Use the aggregate available post-commit (or `FetchLatest`). Superseded checks fire and no-op (validate-on-arrival), per ADR 0001 — never cancel outbox messages.

- [ ] **Step 4 — read DTO.** `BuildingSlotResponse` gains `HaltReason? HaltReason`; `PlanetResponse.From` maps `b.HaltReason`. (Keep `EtaCompletionUtc` position; add reason after.)

- [ ] **Step 5 — integration test** `Tests/Halting/StorageHaltingTests.cs`: register a player; drive a pool to capacity fast (shrink the relevant storage cap via a dedicated fixture env var if needed — check `AppFixture`/`WorldGenOptions` knobs; otherwise invoke `CheckStorageFullHandler.Handle` directly with a predicted time after seeding a near-full pool). Assert the producer's `Buildings[i].Status == Halted` with `HaltReason == OutputStorageFull` via the API, and that energy consumption dropped. Add a stale-check test: call the handler with a `PredictedAt` when the pool is NOT full → no halt appended.

- [ ] **Step 6** — build clean. Commit: `feat: CheckStorageFull scheduling + validate-on-arrival halting; surface HaltReason (#69)`.

---

### Task 3: Resume on storage-freeing commits (D6)

**Files:** modify `Planet.cs` (`Apply(CargoLoadedFromStorage)` path) and/or `FleetEndpoints.cs` assemble+cargo endpoint; `CompleteFleetArrivalHandler.cs` (cargo delivery frees the destination? no — delivery ADDS to storage; the *origin* load frees it). Test: extend `StorageHaltingTests`.

- [ ] **Step 1 — resume evaluation on the load path.** When cargo is loaded from a planet's storage (freeing an output pool that may have halted its producer), the commit must evaluate resumes. `Apply(CargoLoadedFromStorage)` currently does NOT call `RebaseRates` (composition-preserving). Rather than change the `Apply` (which would alter checkpoint semantics), add resume evaluation at the **endpoint** that loads cargo: after loading, `planet.EvaluateStorageResumes(now)` → append any `BuildingResumed` → `RebaseRates` runs via their `Apply` → schedule fresh deadlines → one commit. The subagent must locate the exact load endpoint (`FleetEndpoints` assemble-with-cargo, per survey ~149-155) and confirm the cleanest insertion point.
- [ ] **Step 2 — integration test.** Fill ore storage → Drill halts → assemble a cargo fleet loading ore off the planet → Drill resumes (status Operational, ore rate positive again). Use the #62 shared helpers (`_host.WaitForStock`, `PollUntil`).
- [ ] **Step 3** — build clean. Commit: `feat: producers resume when cargo load frees output storage (#69)`.

---

### Task 4: End-to-end integration + docs

- [ ] **Step 1 — e2e test** `Tests/Halting/StorageHaltResumeEndToEndTests.cs`: register → let ore storage fill to cap (short fixture caps) → assert Drill halts (5% draw, ore rate 0) → transport ore away → assert Drill resumes and ore accrues again. Mirror `BuildingConstructionCompletionTests` polling style.
- [ ] **Step 2 — docs.** Update `technical-design/domain-model.md`: `Halted` status + `HaltReason`, `BuildingHalted`/`BuildingResumed`/`CheckStorageFull`, the deadline-prediction method. One line in `game-design/resources.md` if wording drifts.
- [ ] **Step 3** — build clean. Commit: `test+docs: storage halt/resume e2e; domain-model updates (#69)`.

---

### Task 5: PR
- [ ] Push `feat/69-storage-halting`; open PR base `phase-5`, title `feat: storage caps & halting (#69)`, body summarizing the machinery + the scope note (input-starvation → #70) + "Closes #69". Self-merge on green CI.
