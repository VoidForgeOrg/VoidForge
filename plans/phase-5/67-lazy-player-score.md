# #67 — Lazy Player Score on GET /api/players/me Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. PROD code — review each task's diff like a PR (memory `plan-embedded-code-needs-review-scrutiny`).

**Goal:** A player score computed lazily from everything they own, exposed on `GET /api/players/me`. New `ScoringSpecs` (placeholder point values, like `BuildingSpecs`) + a reusable `ScoreCalculator` service. Spec: `game-design/scoring.md`; descoped from Phase 5 into this issue (D13). **Shape `ScoreCalculator` for reuse by #68's Leaderboard projection.**

**Tech stack:** .NET 9, Marten (query side), Wolverine.Http, xUnit + Alba, #62 helpers.

## Global Constraints
- `TreatWarningsAsErrors`; MA0048/MA0051. Branch `feat/67-lazy-player-score` off `phase-5`. Commits suffixed `(#67)`.
- BOTH `dotnet build src/Voidforge.slnx -warnaserror` AND `dotnet format src/Voidforge.slnx --verify-no-changes` per task. **No local `dotnet test`** — defer to CI (memory `ci-test-job-flaky-kill`: `test` can flakily SIGKILL; re-run first).

## Score inputs (scoring.md §"Score Inputs") — count CURRENT state, including incomplete
- **Planets:** count of owned planets.
- **Buildings:** across all owned planets, every building whose status is NOT a terminal tombstone (exclude `Demolished` and `Cancelled`; INCLUDE `Operational`, `UnderConstruction`, `Halted`, `ConstructionHalted`, `Demolishing`). Point value per building **type** (`ScoringSpecs`).
- **Ships (no double-count — a ship is in exactly one place at a time):** completed roster ships (`Planet.Ships`), in-flight ships (`Fleet.Ships` of owned, non-`Disbanded` fleets), and under-construction ships (`Planet.ShipQueue` entries still in progress — verify which `ShipBuildStatus` values are "alive" and exclude any cancelled/removed). Point value per ship **type**.
- **Resources:** planet storage `IronOre.GetCurrentValue(now)` + `IronIngot.GetCurrentValue(now)` summed over owned planets, plus fleet `CargoIronOre` + `CargoIronIngot` over owned non-`Disbanded` fleets. **Evaluated from checkpoints at query-time `now` — never stored stale** (acceptance criterion). Point value per resource **type** (per unit).

## Task 1 — `ScoringSpecs` + `ScoreCalculator` (+ `ScoreComponents`) with unit tests
- **`ScoringSpecs`** (`src/Voidforge.Api/Domain/ScoringSpecs.cs`): static class mirroring `BuildingSpecs`. Placeholder point values (TBD balancing): `PointsPerPlanet`, `BuildingPoints(BuildingType)`, `ShipPoints(ShipType)`, `ResourcePointsPerUnit(ResourceType)`. Pick simple non-zero placeholders that make each category distinguishable in tests (e.g. planet 100; Shipyard>Drill; ColonyShip>CargoVessel; ingot>ore per unit) — document they are placeholders.
- **`ScoreComponents`** (record): the reuse seam — asset counts keyed by type (`IReadOnlyDictionary<BuildingType,int>` buildings, `IReadOnlyDictionary<ShipType,int>` ships, int planetCount) + resource totals (`decimal` ore, ingot). This is what #68's projection will persist per player.
- **`ScoreCalculator`** (`src/Voidforge.Api/Scoring/ScoreCalculator.cs`, DI-registered singleton): two layers so #68 can reuse the points math without the aggregates —
  - `decimal Score(ScoreComponents components)` — pure: applies `ScoringSpecs` to components.
  - `ScoreComponents Extract(IReadOnlyCollection<Planet> ownedPlanets, IReadOnlyCollection<Fleet> ownedFleets, DateTimeOffset now)` — builds components from aggregates, evaluating pools at `now`, applying the tombstone/alive and non-double-count rules above.
  - Optional convenience `decimal Compute(planets, fleets, now) => Score(Extract(...))`.
- Register in `Program.cs` (`AddSingleton<ScoreCalculator>()`).
- **Unit tests** (`src/Voidforge.Tests/Scoring/ScoreCalculatorTests.cs`, pure-domain, `_at` fixed time like `PlanetHaltingTests`): construct planets/fleets in-memory (apply events), assert EXACT scores — one planet, one of each building status (tombstones excluded), roster+in-fleet+under-construction ships (no double-count), and resources evaluated at `now`. Also a direct `Score(components)` test. Exact-value assertions are safe here (fixed pools, no live accrual).
- Build + format. Commit: `feat: ScoringSpecs + reusable ScoreCalculator (#67)`.

## Task 2 — Expose score on GET /api/players/me + tests
- Add `decimal Score` to `PlayerInfoResponse` (currently `(Id, Name, RegisteredAt)`).
- `PlayerEndpoints.Me`: after loading the Player, query owned planets (`session.Query<Planet>().Where(p => p.OwnerId == playerId)`) and owned fleets (`session.Query<Fleet>().Where(f => f.OwnerId == playerId)`), inject `ScoreCalculator` + `TimeProvider`, compute `Compute(planets, fleets, timeProvider.GetUtcNow())`, include in the response. (Me currently returns 404-ProblemDetails when the id is unparseable/unknown — preserve that.)
- **Endpoint integration test** (extend `Players/*` tests): a registered player with the seeded homeworld has `score > 0` reflecting the seeded planet + buildings.
- **E2E (acceptance)** — extend `src/Voidforge.Tests/Colonize/FullLoopEndToEndTests.cs`: register → build economy → construct ships → colonize → transport, then GET /me and assert the score **reflects** the acquired assets. AVOID brittle exact-equality against a live producing homeworld (pools accrue between events) — assert the score strictly INCREASED across asset-acquiring steps and/or meets a computed lower bound from the known counts; the EXACT-value proof lives in Task 1's unit tests. Document the choice in a comment.
- Build + format. Commit: `feat: score field on GET /api/players/me + e2e (#67)`.

## Task 3 — docs note + PR (coordinator)
- `game-design/scoring.md`: replace the TODO with a pointer to `ScoringSpecs` (placeholders) and the lazy `ScoreCalculator`. `technical-design/architecture.md`/`domain-model.md`: note the read-side `ScoreCalculator` + `ScoreComponents` reuse seam for #68. Also commit the updated `PHASE-5-HANDOVER.md`.
- PR `feat/67-lazy-player-score` → `phase-5`, "Closes #67". Self-merge on green CI.

## Acceptance (from the issue)
- [ ] Point values configurable in one place (`ScoringSpecs`) (Task 1).
- [ ] Resource contributions evaluated from checkpoints at query time, never stale (Task 1 `Extract` uses `GetCurrentValue(now)`; asserted).
- [ ] E2E: register → economy → ships → colonize → transport, score reflects everything (Task 2).
- [ ] `ScoreCalculator` shaped for #68 reuse (the `Score(ScoreComponents)` seam).

## Notes / judgment calls (autonomous)
- `ScoringSpecs` is a **static** class (issue says "like `BuildingSpecs`") — it is read-side only (never inside aggregate `Apply`), so the `BalanceOptions`-DI rationale (aggregate purity) does not apply.
- `Score` typed `decimal` (resources are decimal) — no premature rounding; final rounding is a balancing concern.
- No-double-count rests on the invariant that assembling a fleet MOVES ships out of `Planet.Ships` into `Fleet.Ships` (and disband reverses it) — VERIFY this while implementing; if a ship can transiently appear in both, dedup by ship id.
