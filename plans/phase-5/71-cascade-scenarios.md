# #71 — Cascading Scenarios & Even-Split Proof Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development.

**Goal:** Prove the four `engine.md` cascade scenarios resolve **within a single checkpoint**, cover the edge cases (simultaneous checks; all-buildings-halted), and prove even-split distribution — all as tests. **No new machinery** (design D5); the halting/depletion/demolition/ingot machinery already exists (#69/#70/#72/#83).

**Architecture:** integration tests via the `DepletionCascadeTests`/`IngotStarvationCascadeTests` deterministic pattern (direct handler invocation through `InvokeHandler`, pool pinning via oversized cargo events, live-aggregate deadline math — no wall-clock waits), plus a few pure-domain unit slices where cheaper. Assert the `engine.md` L52 invariant explicitly (one `SaveChangesAsync` / one `RebaseRates` re-derivation).

**Tech Stack:** .NET 9, Marten, Wolverine, xUnit + Alba, the #62 shared helpers.

**Spec:** `plans/phase-5-hardening-design.md` §4, D5; `game-design/engine.md` §"Cascading Events" (L48-52 — mirror the wording in test names/comments).

## Global Constraints
- `TreatWarningsAsErrors`; MA0048/MA0051. Branch `feat/71-cascade-scenarios` off `phase-5` (after #83 merges — the scenario-2 tail needs #83). Commits suffixed `(#71)`.
- BOTH `dotnet build -warnaserror` AND `dotnet format --verify-no-changes`. No local `dotnet test`.
- Place in `src/Voidforge.Tests/Cascade/` (new folder, namespace `Voidforge.Tests.Cascade`) — signals the "prove the four scenarios" mandate distinctly from the incremental `Halting/*CascadeTests`.

## Coverage baseline (from survey — do NOT duplicate)
- **Scenario 1 head** (`DepletionCascadeTests`) + **energy tail as a unit slice** (`PlanetHaltingTests.ApplyBuildingHaltedLiftsProductivityMultiplierInOneRederivation`) — exist SPLIT; the full-chain-on-an-overloaded-planet integration is the gap.
- **Scenario 2** — covered SPLIT across `DepletionCascadeTests` (ore→refinery InputStarved→ingots stop) + `IngotStarvationCascadeTests` (→ construction + ship halt). Optional single-flow stitch.
- **Scenario 3** — unit-only (`PlanetEnergyTests.OverloadScalesProductivityProportionally`, via immediate `BuildingPlaced`); the completion-drives-overload integration is the gap.
- **Scenario 4** — unit-only (`PlanetDemolitionTests.DemolishingAConsumerFreesEnergyAndResolvesOverloadInTheSameCommit`); the demolish-endpoint-on-overloaded-planet integration is the gap.
- **Edge cases 5, 6 and even-split proofs 7, 8** — MISSING.

## Task 1 — The four scenarios as cohesive integration tests (`Cascade/CascadeScenarioTests.cs`)
Fill gaps 1 and 3; add the single-flow stitches for 2 and 4 (mirroring engine.md's unbroken-chain wording). Use the `InvokeHandler` + pool-pinning + `PredictX` deadline techniques; assert single-checkpoint resolution.
- **Scenario 1 (full, on an overloaded planet):** register; build extra Drills so the planet is energy-**overloaded** (e.g. enough drills that Σ draw > 100 MW generation, m < 1 — build & complete them via `EnsureOperational...`/`BuildingConstructionCompletionHandler` or seed operational drills). Deplete the ore deposit (`PredictDepletionDeadline` → `CheckPoolDepletedHandler`). Assert: all drills `ResourceDepleted`, **energy consumption drops, `GetProductivityMultiplier`/`Energy.ProductivityMultiplier` recovers toward 1** (energy freed → overload resolves → productivity recovers), all in the depletion commit.
- **Scenario 2 (single flow):** register; start a construction + queue a ship (ingot consumers). Empty the ore buffer + halt/deplete drills so the refinery starves → ingot production stops → the ingot buffer empties → construction + ship build halt. Assert the chain: refinery `InputStarved`, `IronIngot.Rate <= 0`, then (`CheckIngotStarvedHandler`) construction `ConstructionHalted` + ship `Halted`. (May reuse the seed helpers from the existing cascade tests.)
- **Scenario 3 (completion drives overload):** register; queue construction of a building that, on **completion** (`CompleteBuildingConstructionHandler`), tips the planet into overload. Assert the productivity multiplier drops and dependent pool rates scale down — in the completion commit. (This is the real post-#26 path, vs. the retired immediate-place integration.)
- **Scenario 4 (demolish resolves overload, integration):** register; reach an overloaded state; demolish a consumer via `POST .../demolish` + (deterministic) `CompleteBuildingDemolitionHandler`, OR assert the immediate-shutdown at `BuildingDemolitionStarted` already lifts the multiplier. Assert overload resolves in the commit.
- Each test asserts the L52 "within a single checkpoint" invariant (one commit / one re-derivation).
- Build + format. Commit: `test: engine.md cascade scenarios 1-4 as cohesive integration tests (#71)`.

## Task 2 — Edge cases + even-split proofs (`Cascade/CascadeEdgeCaseTests.cs`, `Cascade/EvenSplitContentionTests.cs`)
- **Edge 5 — simultaneous depletion + storage-full at one instant:** compose a planet where a depletion deadline and a storage-full (or buffer-empty) deadline fall at the SAME instant; invoke both handlers at that instant; assert one consistent checkpoint (correct halts, non-negative/≤cap pools, no double-apply, no throw).
- **Edge 6 — all buildings halted (blackout):** halt every operational building (drills + refinery via `ResourceDepleted`/`InputStarved`/`OutputStorageFull`); assert the planet is stable — energy consumption == only the 5% idle floors, all production rates 0 (or buffer-drain only), and reads/queries don't throw.
- **Even-split 7 — two refineries share insufficient ore:** planet with 2 Refineries (demand 10) and 1 Drill (inflow 10) and an empty ore buffer → aggregate refined ore clamps to inflow 10, `IronIngot.Rate = factor × 10` (each refinery implicitly gets inflow/2). Explicitly named as the scalar-pool even-split proof; assert the aggregate throughput, and (from `EvaluateInputStarvation`) that neither refinery halts (both at reduced throughput, not starved). Note in a comment there is no per-refinery tracking — even-split IS the scalar-pool clamp.
- **Even-split 8 — shipyard build vs construction share ingots:** an ingot buffer of N with a construction drain + a ship-build drain; both reach their halt at the SAME predicted `PredictIngotBufferEmpty` instant → the shared buffer was drained evenly (both halt together in the one `CheckIngotStarved`). Assert both consumers halt at that instant.
- Build + format. Commit: `test: cascade edge cases (simultaneous, blackout) + even-split proofs (#71)`.

## Task 3 — docs note + PR
- `technical-design/testing.md`: a short "Cascade scenario coverage (#71)" note mapping the four engine.md scenarios + edge cases + even-split to their tests, and the single-checkpoint invariant they assert.
- PR `feat/71-cascade-scenarios` → `phase-5`, "Closes #71". Self-merge on green CI.

## Notes
- Prefer integration (they prove the real handler/commit path) but a pure-domain unit slice is fine where an integration arrangement would be disproportionate (e.g. even-split 7 can be unit if composing 2 refineries + 1 drill in-memory is cleaner).
- If any "gap" turns out already covered once you read the code, say so and skip it — don't add a redundant test.
