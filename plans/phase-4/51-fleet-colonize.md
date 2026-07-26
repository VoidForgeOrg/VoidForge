# #51 — Colonize Mission & Atomic Planet Claim Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fleets claim uncolonized planets (one Colony Ship consumed, cargo auto-unloaded); a lost race fails cleanly with the ship preserved; registration's homeworld assignment moves onto the same guarded claim — closing race bug #19.

**Architecture:** The claim is `FetchForWriting<Planet>` + a null-owner assertion in `Planet.Claim` (spec D10). A genuine tie makes the loser's commit throw `ConcurrencyException`; the arrival handler is retried whole by the #39 Wolverine policy and re-reads a now-owned planet into the `ColonizationFailed` branch. Registration wraps the same claim in a bounded re-pick retry with a fresh Marten session per attempt. Colonize extends `CompleteFleetArrivalHandler`'s existing mission dispatch (`else if` before the single `SaveChangesAsync`).

**Tech Stack:** .NET 9, Marten, Wolverine, xUnit + Alba.

**Spec:** `plans/phase-4-fleets-expansion-design.md` §2.4 (Colonize), §2.6 (registration), §5, §7 items 4–5. Closes #19.

## Global Constraints

- MA0048; `TreatWarningsAsErrors`; `_`-prefixed static test fields; `dotnet format --verify-no-changes` clean before the PR.
- `FetchForWriting` at every existing-stream append; the arrival handler keeps ONE `SaveChangesAsync`.
- Test-run hygiene: FOREGROUND test runs only; the quality-gate Stop-hook can race manual runs on the shared DB — treat first-run flakes as possible races (isolated re-run before investigating).
- Error codes per spec §5. TDD per task. Commits conventional, suffixed `(#51)`.

## Plan-level decisions (within spec letter/spirit)

1. **No launch-time "destination uncolonized" check.** Spec §2.4 lists only the ≥1-Colony-Ship 409 at launch; ownership can change mid-flight anyway, and arrival is the honest place (same philosophy as Transport's re-check). `game-design/fleets.md`'s "target must be uncolonized" is enforced by the arrival outcome (claim vs `ColonizationFailed`).
2. **`Register` injects `IDocumentStore`** (like `WorldSeeder`) and opens a fresh `LightweightSession` per claim attempt — after a failed `SaveChangesAsync`, a Marten session's pending unit of work (Player stream, ApiKey, the stale planet append) cannot be selectively unwound, so per-attempt sessions are the only clean retry shape.
3. **`ConsumeColonyShip` picks deterministically**: the ColonyShip with the lowest (`CompletedAt`, `Id`) — the roster's stable sort — so replay and retries agree on which ship died.
4. **`Apply(ColonizationFailed)` is a state no-op** (history-only event; the fleet is already `Stationed` via `FleetArrived`). The observable API outcome for a loser: still stationed at the planet, colony ship intact, cargo intact.
5. **Carried from #50's final review:** the two-fleet race test doubles as the concurrent-arrival CONSERVATION test (winner's planet stores + both fleets' remaining cargo == total loaded); `AcceptCargoDelivery` gains its negative-input guard now that a third caller lands; the colonize auto-unload comment notes headroom computes against the PRE-colonization in-memory pool (benign: uncolonized pools are zero-value/zero-rate, so headroom = full capacity); `Apply(PlanetColonized)`'s raw `CheckpointTime` set gets a safety comment (zero rates/stores at claim time — nothing to accrue or lose).

## File Structure

```
src/Voidforge.Api/Domain/
  Fleet.cs                              (modify — ConsumeColonyShip, RecordColonizationFailure, Applys; D9/D11 comment-label fixes)
  Planet.cs                             (modify — Claim factory + PlanetColonized checkpoint comment; AcceptCargoDelivery negative guard)
  Events/ColonyShipConsumed.cs          (new)  — (Guid PlanetId, Guid ShipId, DateTimeOffset ConsumedAt)
  Events/ColonizationFailed.cs          (new)  — (Guid PlanetId, DateTimeOffset At)
src/Voidforge.Api/Endpoints/
  FleetEndpoints.cs                     (modify — Colonize launch precondition; drop the "not supported yet" branch)
  CompleteFleetArrivalHandler.cs        (modify — Colonize branch)
  PlayerEndpoints.cs                    (modify — guarded homeworld claim, bounded re-pick retry, IDocumentStore)
src/Voidforge.Tests/Colonize/
  ColonizeDomainTests.cs                (new, unit)
  ColonizeMissionTests.cs               (new, integration: launch guards + handler-invoked claim/failure)
  ClaimRaceTests.cs                     (new: two-fleet race + conservation; concurrent registrations)
  FullLoopEndToEndTests.cs              (new, e2e phase gate: economy → ships → colonize → Transport to the colony)
src/Voidforge.Tests/Cargo/TransportMissionEndpointTests.cs  (modify — serialized-collection assumption comment on the raw-Append arrangement)
technical-design/domain-model.md        (modify)
game-design/player-actions.md           (modify — verify/add Colonize)
```

---

### Task 1: Carried hygiene from #50's final review

**Files:** `Domain/Planet.cs`, `Domain/Fleet.cs`, `Tests/Cargo/PlanetStorageMutationTests.cs`, `Tests/Cargo/TransportMissionEndpointTests.cs`

- [ ] **Step 1 (failing test):** `AcceptCargoDelivery` with a negative offer throws `InvalidOperationException` (add to `PlanetStorageMutationTests`).
- [ ] **Step 2:** RED. **Step 3:** add the guard (mirror `LoadCargoFromStorage`'s wording); fix the comment labels in `Fleet.cs` (`GetCargoLoad` cites D8; `UnloadCargo` cites D9); add the one-line serialized-collection assumption comment above the raw `session.Events.Append` arrangement in `TransportMissionEndpointTests`. **Step 4:** GREEN + full suite + format.
- [ ] **Step 5:** Commit `fix: AcceptCargoDelivery negative guard; decision-label comment corrections (#51)`.

---

### Task 2: Colonize domain

**Files:** `Domain/Fleet.cs`, `Domain/Planet.cs`, `Events/ColonyShipConsumed.cs`, `Events/ColonizationFailed.cs`; Test: `Tests/Colonize/ColonizeDomainTests.cs`

**Interfaces:**
- `record ColonyShipConsumed(Guid PlanetId, Guid ShipId, DateTimeOffset ConsumedAt)`
- `record ColonizationFailed(Guid PlanetId, DateTimeOffset At)`
- `Fleet.ConsumeColonyShip(Guid planetId, DateTimeOffset at) → ColonyShipConsumed` — deterministic pick per decision 3; throws `InvalidOperationException` if the fleet holds no ColonyShip. `Apply` removes exactly that ship (by `ShipId`).
- `Fleet.RecordColonizationFailure(Guid planetId, DateTimeOffset at) → ColonizationFailed`; `Apply` is an empty-bodied history no-op (commented).
- `Planet.Claim(Guid ownerId, DateTimeOffset at) → PlanetColonized` — `new PlanetColonized(ownerId, 0, 0, at)` (zero starting stores, spec §2.4); throws `InvalidOperationException("Planet is already colonized.")` when `OwnerId is not null` — the D10 null-owner assertion. Add the safety comment on `Apply(PlanetColonized)`'s raw `CheckpointTime` set (zero rates/stores at claim ⇒ nothing to accrue or lose; homeworld seeding passes starting stores explicitly).

- [ ] **Step 1 (failing tests):** consume picks the oldest ColonyShip of several and removes exactly it; consume with no ColonyShip throws; failure event applies without state change; `Claim` on an uncolonized planet returns the event and `Apply` sets owner with zero stores; `Claim` on an owned planet throws.
- [ ] **Step 2:** RED. **Step 3:** Implement. **Step 4:** GREEN + full suite.
- [ ] **Step 5:** Commit `feat: colonize domain — guarded claim, colony-ship consumption (D10) (#51)`.

---

### Task 3: Launch precondition + arrival Colonize branch

**Files:** `Endpoints/FleetEndpoints.cs`, `Endpoints/CompleteFleetArrivalHandler.cs`; Test: `Tests/Colonize/ColonizeMissionTests.cs`

- Launch: replace the `Mission != Move && Mission != Transport → 400` shape: all three defined missions are valid now (the `Enum.IsDefined` 400 stays). Colonize precondition after the existing guards: `fleet.Ships.Any(s => s.Type == ShipType.ColonyShip)` else 409 "Colonize requires a Colony Ship." No destination-ownership check (decision 1).
- Handler, after the Transport block, per #50's final-review shape guidance:

```csharp
else if (mission == MissionType.Colonize && destinationId is not null)
{
    var planetStream = await session.Events.FetchForWriting<Planet>(destinationId.Value);
    var planet = planetStream.Aggregate;
    if (planet is not null && planet.OwnerId is null)
    {
        planetStream.AppendOne(planet.Claim(fleet.OwnerId, message.ArrivesAt));
        stream.AppendOne(fleet.ConsumeColonyShip(destinationId.Value, message.ArrivesAt));
        if (cargoOre > 0 || cargoIngot > 0)
        {
            // Headroom computes against the PRE-colonization in-memory pool (AppendOne does not
            // re-apply): benign — an uncolonized pool is zero-value/zero-rate, so headroom is the
            // full capacity, which is exactly the post-claim truth (zero starting stores).
            var delivered = planet.AcceptCargoDelivery(fleet.Id, cargoOre, cargoIngot, message.ArrivesAt);
            planetStream.AppendOne(delivered);
            stream.AppendOne(fleet.UnloadCargo(destinationId.Value, delivered.IronOre, delivered.IronIngot, message.ArrivesAt));
        }
    }
    else
    {
        // Lost the race (or targeted an owned world): ship preserved, cargo intact, fleet idles
        // here Stationed. A true tie loses on commit with ConcurrencyException, is retried whole
        // by the #39 policy, re-reads the now-owned planet, and lands in this branch (D10).
        stream.AppendOne(fleet.RecordColonizationFailure(destinationId.Value, message.ArrivesAt));
    }
}
// existing single SaveChangesAsync covers all streams
```

- [ ] **Step 1 (failing tests):** launch Colonize without a ColonyShip → 409; with one → 200 InTransit; handler-invoked arrival at an uncolonized planet → planet owned by fleet owner with zero stores + colony ship gone from fleet + cargo delivered (arrange cargo at assembly); arrival at an ALREADY-owned planet → owner unchanged, colony ship still aboard, cargo intact, fleet Stationed there; duplicate colonize-arrival message → no-op (idempotency, mirrors the Transport test).
- [ ] **Step 2:** RED. **Step 3:** Implement. **Step 4:** GREEN + full suite + format.
- [ ] **Step 5:** Commit `feat: Colonize mission — guarded claim on arrival, ship consumed, cargo delivered (#51)`.

---

### Task 4: Registration onto the guarded claim (closes #19)

**Files:** `Endpoints/PlayerEndpoints.cs`; Test: existing `Tests/Players/PlayerRegistrationTests.cs` still green (behavioral contract unchanged), new assertions in `Tests/Colonize/ClaimRaceTests.cs` (Task 5 covers the race; this task covers the mechanism)

- `Register` signature: replace `IDocumentSession session` with `IDocumentStore store`; per attempt (bounded, 3): open `await using var session = store.LightweightSession()`; name-taken check on attempt 1 only (hoist before the loop with its own session or reuse attempt 1's); query uncolonized ids; empty → 503 (existing semantics); pick random; `FetchForWriting<Planet>(pick)`; if `OwnerId is not null` → next attempt (stale read — no exception consumed); else build the full transaction (StartStream<Player>, `planet.Claim(playerId, now)`? — NO: registration seeds starting stores and buildings, so append `new PlanetColonized(playerId, opts.StartingIronOre, opts.StartingIronIngots, now)` + the three `BuildingPlaced` events via the fetched stream, plus `session.Store(ApiKey)`); `try { await session.SaveChangesAsync(); return Ok; } catch (ConcurrencyException) { /* lost the tie — next attempt */ }`. Exhausted → 503.
- NOTE: `Planet.Claim` (zero stores) is the FLEET claim; registration keeps its richer seeded colonization but gains the SAME guard shape — assert `planet.OwnerId is null` after `FetchForWriting`, and rely on the version guard for ties. Add a comment tying both sites to D10/#19. (Do not force registration through `Claim` — starting stores/buildings differ by design.)
- Keep `[AllowAnonymous]`; keep the perf comment. Remove the stale "Race: two concurrent registrations..." comment — replaced by the guard.

- [ ] **Step 1 (failing test):** in `ClaimRaceTests`, a plain sequential registration still succeeds end-to-end (guard added, behavior preserved) — RED only via compile/behavior if the refactor breaks something; primary RED coverage is Task 5's race test. Run the full `Players` suite.
- [ ] **Step 2–4:** Implement; full suite green + format.
- [ ] **Step 5:** Commit `fix: registration homeworld claim is guarded + retried — closes #19 (#51)`.

---

### Task 5: Race + conservation tests (spec §7 items 4–5; #50 carry-over)

**Files:** `Tests/Colonize/ClaimRaceTests.cs`

- [ ] **Step 1 — two-fleet colonize race with conservation:** two players; each builds 1 ColonyShip + 1 CargoVessel, assembles with known cargo (e.g. 100 ore / 50 ingot each), launches Colonize at the SAME uncolonized planet (pick one not owned by anyone). Fire the two handler-invoked arrivals CONCURRENTLY (`Task.WhenAll`, separate sessions); wrap each `Handle` call in a catch-`ConcurrencyException`-retry loop (max 5, brief delay) that mimics the Wolverine policy — comment that in-test invocation bypasses Wolverine's retry, so the loop stands in for it. Assert: exactly ONE player owns the planet; the winner's fleet lost exactly its oldest ColonyShip and its cargo was delivered; the loser's fleet retains ColonyShip AND full cargo, Stationed at the planet; **conservation: destination stores == winner's loaded cargo, and loser's aboard-cargo == loser's loaded amounts (winner stores + both fleets' remaining cargo == total loaded)**.
- [ ] **Step 2 — concurrent registrations never double-colonize:** five concurrent `POST /api/players/register` with distinct names → all 200, five DISTINCT `homeworldId`s, each planet owned by exactly the registrant (`GET /api/planets/{id}`).
- [ ] **Step 3:** Run both repeatedly (3×, foreground) — flakes here are findings, not noise; escalate failures with output rather than weakening.
- [ ] **Step 4:** Commit `test: colonize claim races — single winner, conservation, registration guard (#51)`.

---

### Task 6: Full-loop e2e gate + docs

**Files:** `Tests/Colonize/FullLoopEndToEndTests.cs`, `technical-design/domain-model.md`, `game-design/player-actions.md`

- [ ] **Step 1 — the phase-completion e2e (real scheduler):** register → (homeworld already produces) → build Shipyard → build 1 ColonyShip + 1 CargoVessel → assemble with cargo → launch **Colonize** at an uncolonized planet in another system → real arrival → colony owned, zero-store planet received the cargo, colony ship consumed → disband remaining ships at the colony → **the true Transport e2e now unlocked:** build a second CargoVessel at home, assemble with cargo, launch **Transport** to the colony (owned destination) → real arrival → delivered. This demonstrates the epic's full loop: economy → ships → expand → supply the colony.
- [ ] **Step 2:** Run — PASS (investigate, don't weaken; expect ~30–60 s with test speeds).
- [ ] **Step 3 — docs:** `domain-model.md`: Colonize branch in the arrival handler, `Planet.Claim`/D10 guarded-claim note covering BOTH claim sites (fleet + registration), new fleet events, registration's bounded re-pick retry, #19 closure. `player-actions.md`: verify/add Colonize wording (one Colony Ship consumed, extras survive, failure preserves the ship). `game-design/fleets.md`: verify Colonize section matches (it already documents the failure rule — read, don't rewrite).
- [ ] **Step 4:** Full suite + `dotnet format --verify-no-changes`.
- [ ] **Step 5:** Commit `test+docs: full-loop e2e — colonize, then transport to the colony; guarded claim documented (#51)` — no push, no PR (controller runs the whole-branch review first).

---

## Self-review notes (performed at plan-writing time)

- Spec coverage: §2.4 launch 409 + claim/failure branches (Tasks 2–3), §2.6 registration retry (Task 4), §5 codes, §7 item 4 races (Task 5) + item 5 e2e (Task 6), D10 both claim sites, #19 closure (Task 4), all six #50-review carry-overs (Tasks 1/3/5 + comments).
- Registration deliberately does NOT reuse `Planet.Claim` (different starting stores/buildings) — the shared thing is the guard shape, stated explicitly so a reviewer doesn't flag divergence as a miss.
- Embedded-code review: handler branch guards `destinationId is not null`; failure branch never touches the planet stream; conservation assertions are exact equalities on known loaded amounts (no accrual on a zero-rate colony — robust).
