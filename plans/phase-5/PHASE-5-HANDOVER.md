# Phase 5 (Hardening) — Session Handover

**As of:** 2026-08-15, mid-phase. Read this first, then `git -C /home/dev/VoidForge log --oneline -5 phase-5` and `gh pr list` to confirm live state.

## Where things stand right now (VERIFY on resume)
- **Current branch:** `phase-5` (both #83 and #71 merged; nothing in flight). Untracked: `plans/phase-5/PHASE-5-HANDOVER.md` (this file).
- **#83 (PR #87) — MERGED** into phase-5 (`0990e2c`). The `test` job flakily SIGKILL'd once mid-run (no xUnit summary + a `pk_mt_events_stream_and_version` dup-key flood + orphaned dotnet); a re-run went green. NOT a code bug (the #83 ingot self-reschedule provably terminates once the buffer clamps to 0). New memory: `ci-test-job-flaky-kill`.
- **#71 (PR #88) — MERGED** into phase-5 (`2204268`). Test-only cascade/edge/even-split proofs in `src/Voidforge.Tests/Cascade/`; passed CI first try. Even-split 7 uses 3 refineries (2-vs-1 is tautological — demand==inflow at every m).
- **FIRST ACTION on resume:** `git -C /home/dev/VoidForge checkout phase-5 && git pull --ff-only`, then `gh pr list`. **Wave 3 is next (#74 → #67 → #68).**

## The workflow (established, follow exactly)
- **One issue → one branch off `phase-5` → one PR into `phase-5` → self-merge on green CI → don't ping between PRs; report at phase end.** (Memory: `phase-integration-branch-workflow`.) The final `phase-5 → main` PR is NEVER self-merged — open it and wait for the user (Tomas).
- **Per issue:** write a JIT plan to `plans/phase-5/<n>-<name>.md`, commit it, then implement via **subagents** (Tomas wants primarily subagents — memory `prefer-subagents-for-execution`), one subagent per plan task, **review the diff between tasks** (this gate has caught ~8 real defects). Then push, open PR, poll CI with a background bash loop, merge on green.
- **CI gotchas:** the `lint` job runs `dotnet format --verify-no-changes` (catches IDE1006 naming etc. that `-warnaserror` misses — memory `lint-ci-runs-dotnet-format`). Always have subagents run BOTH `dotnet build src/Voidforge.slnx -warnaserror` AND `dotnet format --verify-no-changes`. NEVER run `dotnet test` locally concurrently (shared Postgres `voidforge_test` + the quality-gate Stop hook auto-runs the suite → corruption; memory `quality-gate-hook-races-test-runs`). Defer test validation to CI. Commit trailers: `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>` + `Claude-Session: https://claude.ai/code/session_01M7tLYUG4gAfHkgEX6Bacim`.
- **CI poll pattern:** background `while` loop over `gh pr checks <n> --json name,state`, exit when no PENDING/IN_PROGRESS/QUEUED; it re-invokes on completion.

## Progress
Epic **#75**. Waves:
- **Wave 0 — DONE (merged):** #62 (shared test helpers), #45 (registration 409; #61 closed as dup), #46 (WorldSeeder double-seed guard), #40 (closed after class-size audit → spawned **#78** FleetEndpoints split).
- **Wave 1 — DONE (merged):** #69 (storage caps & halting — the keystone), #44 (event-ordering invariant, MVP-scoped: guarded `Apply(PlanetColonized)` + ADR 0002; full rewind-and-reapply → **#81** post-MVP), #70 (resource depletion + refinery ore-starvation).
- **Wave 2 — DONE (merged):** #73 (fleet recall + #60 colonize-in-place; #58 KEPT OPEN — residual double-409 assertion fragility), #72 (building cancel/demolition, tombstone slots), #83 (zero-ingot in-flight-build halting).
- **Wave 1 trailing / capstone — #71 (cascade scenarios & even-split proof): DONE (merged, PR #88).** Test-only (D5), 3 files in `src/Voidforge.Tests/Cascade/` (CascadeScenarioTests = scenarios 1-4 integration; CascadeEdgeCaseTests = edge 5-6 unit; EvenSplitContentionTests = even-split 7-8 unit) + coverage note in `technical-design/testing.md`. Deterministic techniques used: `InvokeHandler`, pool-pinning via oversized cargo events, `PredictX` deadline math, `_base`/`_at` fixed time.
- **Wave 3 — NOT STARTED (NEXT):** #74 (API polish + capstone e2e — shared ownership filter replacing 3 ad-hoc impls, ProblemDetails everywhere replacing bare `{detail}`/`BadRequest<string>`, folds **#63** invalid `?status=`→400; full register→economy→halt→cancel→recall→colonize e2e, NO score assertion), #67 (lazy `ScoreCalculator` on `GET /api/players/me`), #68 (async `Leaderboard` projection + `GET /api/leaderboard`, first async Marten projection, depends on #67).

## Design spec & conventions
- Spec: `plans/phase-5-hardening-design.md` (decisions D1–D14). Docs kept current in `technical-design/domain-model.md`, `adr/0002-event-ordering-invariant.md`.
- **Recurring event-sourcing correctness pattern:** under Marten `UseIdentityMapForAggregates=true`, appended events are re-applied to `stream.Aggregate` at `SaveChanges`. So **idempotent absolute-state events** (BuildingCompleted/Resumed) are safe to also apply in-memory before evaluating downstream resumes; **value-transforming events** (cargo deltas, `ConstructionResumed`/`ShipBuildResumed` which clear `HaltedAt`) must NOT be double-applied — append only, read results post-commit via `FetchLatest`. This has bitten 3x; scrutinize it in any resume/cascade wiring.
- Halting machinery (reuse for #71): `Planet.Halting.cs` (`EvaluateStorageHalts/Resumes`, `EvaluateDepletion`, `EvaluateInputStarvation(Resumes)`, `EvaluateIngotStarvation(Resumes)`, `PredictStorageDeadlines`/`PredictBufferEmpty`/`PredictDepletionDeadline`/`PredictIngotBufferEmpty`), scheduled checks (`CheckStorageFull`/`CheckPoolDepleted`/`CheckInputStarved`/`CheckIngotStarved` + handlers), `StorageHaltScheduling.ScheduleAllChecksAsync` (arms all 4 checks at mutation sites). Statuses: `BuildingStatus{Operational,UnderConstruction,Halted,Cancelled,Demolishing,Demolished,ConstructionHalted}`, `ShipBuildStatus{Queued,Active,Halted}`.

## Next actions in order
1. **#74** (API polish + capstone e2e) — the biggest, PROD-code issue (spec §7, D11/D12). Survey the ownership/error-handling surface first, write a JIT plan, implement via subagents with a review gate (prod code — scrutinize hard per `plan-embedded-code-needs-review-scrutiny`). Parts: shared ownership filter replacing `ShipEndpoints.IsOwner`/inline `BuildingEndpoints` claim parsing/`FleetEndpoints.PlayerId`; ProblemDetails on every non-2xx (concurrency handler's bare `{detail}`, all `BadRequest<string>`); folds #63 (invalid `?status=`→400); OpenAPI review; capstone e2e extending `FullLoopEndToEndTests` (NO score assertion, D13).
2. **#67** (lazy `ScoreCalculator` on `GET /api/players/me`).
3. **#68** (async `Leaderboard` projection + `GET /api/leaderboard`; depends on #67 — first async Marten projection).
4. When #74/#67/#68 merged → open the `phase-5 → main` PR, present the phase report, and **STOP for Tomas's approval (NEVER self-merge to main).**
