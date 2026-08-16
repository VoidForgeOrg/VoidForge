# #44 — Event-Ordering Invariant Made Structural Implementation Plan

**Goal:** Close the *structural* half of #44/D14 — encode the non-decreasing-checkpoint invariant in the type system where cheaply possible, and move the residual analysis from a GitHub comment into durable design docs. Full exact-correctness (rewind-and-reapply) stays **post-MVP** per D14.

## Critical framing (from the #44 survey — read before planning any code)

The #47 partial fix already ships the load-bearing protection: `ResourcePool.GetCurrentValue` floors `elapsed` at 0 and `ResourcePool.Checkpoint` never moves `CheckpointTime` backwards. An out-of-order event is therefore already **inert and conservative** (under-credit only, never corruption). The survey establishes three facts that bound what #44 can add in MVP:

1. **Wall-clock player-command appends provably cannot invert.** Every event's `at` is ≤ wall-clock at application time (the scheduler never fires before its scheduled instant), so a planet's `CheckpointTime` is never ahead of wall-clock; a new command at `now` always has `now ≥ CheckpointTime`. A `max(head, now)` clamp on these 8 sites is a **no-op** — building it adds a snapshot field and 8 call-site changes for zero behavior change. **Rejected.**
2. **Scheduled-completion timestamps double as validate-on-arrival match tokens** (`Fleet.Arrive` checks `ArrivesAt == at`; `Planet.CompleteBuilding` checks `slot.CompletesAt == at`; `CompleteShipBuild` likewise). Clamping the token breaks the guard → the completion never fires → permanent stuck state. PR #47 analyzed the `matchAt`/`effectiveAt` split needed to clamp safely and found it **numerically identical** to the shipped floor. **Rejected — re-litigating #47.**
3. **The only genuine inversions are scheduled fleet arrivals** (`CargoDeliveredToStorage`, `PlanetColonized`-via-`Claim`), stamped with `message.ArrivesAt` which can be far in the past after a host-down / dead-letter replay. The planet-side stamp is *separable* from the fleet match token, so a deterministic `max(planetHead, at)` clamp there is feasible — **but** it changes `AcceptCargoDelivery` headroom timing (headroom computed at the clamped-later instant), a subtle behavior change for marginal MVP benefit. **Deferred** to the post-MVP rewind work, documented as a known refinement.

**Net:** #44's MVP-value deliverables are **WS2 (guard the one convention-bypassing Apply)** and **WS3 (durable docs of the bound + conservative guarantee)**, plus regression-tripwire tests. This is consistent with D14's "rewind-and-reapply stays post-MVP."

## Global Constraints
- `TreatWarningsAsErrors`; MA0048/MA0051. Branch `fix/44-event-ordering` off `phase-5` (after #69 merges — #69 touched `Planet.cs`/`Planet.Energy.cs` but not the colonization path; low conflict risk). Commits suffixed `(#44)`. Build-only locally; CI runs the suite.

## File Structure
```text
src/Voidforge.Api/Domain/Planet.cs        (modify: Apply(PlanetColonized) → guarded checkpoint-then-set-value)
technical-design/architecture.md OR a new technical-design/adr/000X-event-ordering.md  (WS3 docs)
src/Voidforge.Tests/Planets/PlanetEventOrderingTests.cs  (add WS2 + arrival-inversion tripwire tests)
```

### Task 1: WS2 — guard `Apply(PlanetColonized)` (pure domain, unit-tested)

**Files:** modify `Planet.cs` `Apply(PlanetColonized)` (~lines 42-53); test `PlanetEventOrderingTests.cs`.

Current code uses raw `with` to inject seeded stores, bypassing the non-regressing `Checkpoint` — flagged by the issue as "guarded by convention, not by the type." Route it through the guarded path while preserving the seeded-store injection:
```csharp
public void Apply(PlanetColonized @event)
{
    OwnerId = @event.OwnerId;
    // Guarded (#44): checkpoint at the colonization instant (non-regressing), then set the seeded
    // stores. Numerically identical to the prior raw `with` on the zero-rate/zero-value claim-time
    // pool this event always lands on (fleet Claim → 0/0; homeworld → first event after PlanetCreated),
    // but the invariant is now type-enforced rather than convention-enforced.
    IronOre = IronOre.Checkpoint(@event.ColonizedAt) with { CheckpointValue = @event.IronOreStored };
    IronIngot = IronIngot.Checkpoint(@event.ColonizedAt) with { CheckpointValue = @event.IronIngotStored };
}
```
- [ ] **Step 1:** Write a unit test first: colonize at `t`, assert both pools' `CheckpointValue == seeded stores` and `CheckpointTime == t`; and a second test that colonizing (via `Claim`, zero stores) after a hypothetical later `CheckpointTime` does not regress `CheckpointTime` (documents the guard). Use the `Homeworld(at)` fixture idiom.
- [ ] **Step 2:** Apply the change. Confirm the "claim-time pools are zero-rate/zero-value" invariant still holds post-#69 (survey confirmed: #69 changed only owned-planet halting, not colonization). Keep the code comment.
- [ ] **Step 3:** `dotnet build -warnaserror` clean. Commit: `fix: route Apply(PlanetColonized) through the guarded non-regressing checkpoint (#44)`.

### Task 2: WS3 — durable docs of the inversion bound + conservative guarantee

**Files:** add a short ADR `technical-design/adr/0002-event-ordering-invariant.md` (follow `adr/0001`'s format) OR a section in `architecture.md` — check which the repo prefers.

- [ ] **Step 1:** Document, moving the analysis out of the #44 GitHub comment into the repo: (a) the shipped `ResourcePool` floor + non-regressing `Checkpoint` and why an inversion is inert/conservative (under-credit only); (b) the inversion-window bound — ~7s for command/completion races (ADR-0001 poll + #39 backoff), but **unbounded by outage/travel duration for scheduled fleet arrivals** (`CargoDeliveredToStorage`/`PlanetColonized` stamped with `message.ArrivesAt`); (c) why clamping scheduled-completion timestamps is rejected (match-token landmine, #47); (d) the residual under-credit and that exact correctness needs **rewind-and-reapply, tracked post-MVP** (link the follow-up issue from Task 4).
- [ ] **Step 2:** Commit: `docs: ADR — event-ordering invariant, inversion bound, conservative guarantee (#44)`.

### Task 3: Arrival-inversion tripwire test

**Files:** `PlanetEventOrderingTests.cs`.

- [ ] **Step 1:** Add a test simulating a far-past fleet arrival: build up a destination planet's `CheckpointTime` via intervening commands, then apply a `CargoDeliveredToStorage` stamped with an `At` well before the current head. Assert the outcome is **conservative** — checkpoint does not regress, stored value stays non-negative and ≤ capacity, and delivery under-accepts rather than corrupts (mirror `ReverseOrderShortfallIsTheInvertedWindowAtThePreCompletionRate`'s "constant offset, not widening drift" style: assert the shortfall is stable across two read times). This pins the conservative guarantee as a regression tripwire.
- [ ] **Step 2:** `dotnet build -warnaserror`. Commit: `test: arrival-inversion stays conservative — non-regressing, non-negative tripwire (#44)`.

### Task 4: Post-MVP follow-up + close-out

- [ ] **Step 1:** File a post-MVP issue: "Exact event-ordering correctness via rewind-and-reapply" (`domain:core`, no phase label) — the only path to eliminating the under-credit residual; capture the survey's WS3-arrival-clamp refinement (deterministic `max(planetHead, at)` on planet-side arrival stamps, which subtly changes delivery headroom timing) as a candidate.
- [ ] **Step 2:** PR `fix/44-event-ordering` → `phase-5`, "Closes #44" with a clear body: MVP-resolves #44 (conservative + structurally guarded + documented); full correctness → the new post-MVP issue. Self-merge on green CI.

## Sequencing note
D14 put #44 before #70 because "depletion's pool-drain math sits on this invariant." The survey shows that invariant is the **already-shipped #47 floor**, not new #44 work — so #70 does not functionally block on #44. #44 stays first (it's small and closes a wave-1 item), but if it proves contentious it can run in parallel with #70 without risk.
