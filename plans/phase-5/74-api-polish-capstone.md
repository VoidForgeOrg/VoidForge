# #74 — API Polish & Capstone e2e Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. This is the phase's closing issue and the **only wave-3 issue that touches production code broadly** — review every task's diff like a PR (memory `plan-embedded-code-needs-review-scrutiny`).

**Goal:** One consistent API surface — one ownership check, one error shape (ProblemDetails everywhere) — plus a capstone e2e exercising the phase. Spec: `plans/phase-5-hardening-design.md` §7, decisions **D11, D12, D13**; closes **#74** and folds **#63**.

**Tech stack:** .NET 9, Wolverine.Http endpoints, Marten, Swashbuckle (OpenAPI), xUnit + Alba, the #62 shared helpers.

## Global Constraints
- `TreatWarningsAsErrors`; MA0048/MA0051. Branch `feat/74-api-polish-capstone` off `phase-5`. Commits suffixed `(#74)`.
- BOTH `dotnet build src/Voidforge.slnx -warnaserror` AND `dotnet format src/Voidforge.slnx --verify-no-changes` must pass per task. **No local `dotnet test`** (shared Postgres DB corruption + Stop-hook auto-run) — defer to CI. Memory `ci-test-job-flaky-kill`: the `test` job can flakily SIGKILL mid-run (no summary + `pk_mt_events_stream_and_version` flood); re-run before diagnosing.
- Production code touched → each task committed separately, reviewed between.

## Canonical patterns (PIN THESE — every task follows them identically)

### Ownership (D11)
- New `src/Voidforge.Api/Auth/ClaimsPrincipalExtensions.cs`: `public static Guid? PlayerId(this ClaimsPrincipal principal)` = `Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null`. Single source of the claim parse.
- Ownership stays a per-endpoint comparison because the aggregate differs (planet vs fleet). Add small, intention-revealing domain helpers where they read better — `planet.IsOwnedBy(playerId)` / `fleet.IsOwnedBy(playerId)` — but the ONE claim-parse primitive is `PlayerId()`. Delete `ShipEndpoints.IsOwner`, `BuildingEndpoints.IsOwner`, `FleetEndpoints.PlayerId`, and the inline parse in `PlayerEndpoints.Me`.
- **Behavior must not change:** 401 (no/ër bad key) is auth-layer; a valid key whose id doesn't own the aggregate → **403**; unknown aggregate → **404**. Preserve the existing 403-vs-404 ordering at each call site exactly. `GetOwnFleets` uses `PlayerId()` to SCOPE the query (not a 403 gate) — keep that semantics.

### Error shape (D12)
- Every deliberate non-2xx becomes a ProblemDetails via **`TypedResults.Problem(detail: "...", statusCode: StatusCodes.Status4xx)`** (returns `ProblemHttpResult`). Collapse each endpoint's `Results<Ok<T>, BadRequest<string>, NotFound, ForbidHttpResult, Conflict<string>>` union to `Results<Ok<T>, ProblemHttpResult>` (keep the 2xx arm(s) as-is: `Ok<T>`/`Accepted`/`Created`).
- Map current → new, preserving status codes: `TypedResults.NotFound()`→`Problem(statusCode:404)`; `TypedResults.Forbid()`→`Problem(statusCode:403)`; `Conflict<string>(msg)`/`Conflict(msg)`→`Problem(detail:msg,statusCode:409)`; `BadRequest<string>(msg)`→`Problem(detail:msg,statusCode:400)`; `StatusCode(503)`→`Problem(statusCode:503)`. Keep the human-readable `detail` messages that already exist (e.g. "Only a completed building can be demolished.").
- Concurrency handler `src/Voidforge.Api/Http/ConcurrencyConflictExceptionHandler.cs`: replace the bare `{ detail }` write with a ProblemDetails 409 emitted through the registered **`IProblemDetailsService`** (inject it), so it matches the endpoint shape.
- `Program.cs` `AddProblemDetails(options => options.CustomizeProblemDetails = ctx => { ... })`: stamp a consistent shape — `Instance = ctx.HttpContext.Request.Path`, a `traceId` extension (`ctx.HttpContext.TraceIdentifier` / `Activity.Current?.Id`). Do NOT hand-set `title`/`type` per call — let the framework default them from status so the shape stays uniform.
- **Tests:** grep the suite for error-body assertions that read a raw string (`ReadAsText`, `ShouldContain`, `ReadAsJsonAsync<string>`, or asserting the old `{ detail }` object) and update them to the ProblemDetails shape. Status-code assertions (`StatusCodeShouldBe(4xx)`) are unaffected — do not touch those. Wave-0 registration-race 409 (`PlayerEndpoints` Register `Conflict`) shape aligns automatically via this sweep; check `PlayerRegistrationTests`/the 409 race test still pass by shape.

## Task 1 — Shared ownership helper (D11)
- Add `Auth/ClaimsPrincipalExtensions.PlayerId()` (+ optional `IsOwnedBy` domain helpers). Replace all four ad-hoc copies and every call site (survey: `ShipEndpoints` :37,:76; `BuildingEndpoints` :39,:94,:146; `FleetEndpoints` :48,:129,:189,:243,:291,:310,:346 + internal :433,:462,:493; `PlayerEndpoints.Me` :187). Pure refactor — NO status-code/behavior change, so no test changes expected.
- Acceptance check: `git grep -n "FindFirstValue(ClaimTypes.NameIdentifier)"` returns ONLY `ClaimsPrincipalExtensions.cs`.
- Build + format. Commit: `refactor: unify ownership checks into ClaimsPrincipal.PlayerId (#74, D11)`.

## Task 2 — ProblemDetails everywhere + #63 (D12)
- Apply the D12 pattern across ALL endpoint files (`PlayerEndpoints`, `PlanetEndpoints`, `ShipEndpoints`, `SolarSystemEndpoints`, `FleetEndpoints`, `BuildingEndpoints`) + the concurrency handler + `Program.cs` `CustomizeProblemDetails`.
- **#63:** `FleetEndpoints.GetOwnFleets` — change `FleetStatus? status` to a `string? status` query param, `Enum.TryParse<FleetStatus>(status, ignoreCase:true, out …)` guarded (reject values not in `Enum.IsDefined`), return **400 ProblemDetails** ("Unknown fleet status '{status}'.") on unparseable input before the query; null/empty keeps the current "exclude Disbanded" default. Add a test asserting `?status=bogus` → 400 ProblemDetails and a valid `?status=Travelling` still filters.
- Update every affected existing test to the ProblemDetails body shape (grep first; see pattern note).
- Build + format. Commit: `feat: ProblemDetails on every error response + invalid ?status=->400 (#74, D12, #63)`.

## Task 3 — OpenAPI review + park frontend regen
- Verify the live `/swagger/v1/swagger.json` includes ALL Phase 2-5 endpoints (ship-queue, buildings place/cancel/demolish, fleets missions/assemble/disband/cancel/unload/list/get/planet-fleets). Add `.ProducesProblem(4xx)` metadata (or Wolverine.Http equivalent) so the doc documents ProblemDetails error responses for the swept endpoints where it is low-effort and improves fidelity.
- **Recapture** the stale committed snapshot `frontend/app/openapi/voidforge.json` (currently 5 of ~19 paths): run the API and dump swagger to that file (script it: `dotnet run` + `curl localhost:port/swagger/v1/swagger.json`, or a tiny test that writes it). Confirm it now lists the Phase-5 endpoints and ProblemDetails responses.
- **PARK** the frontend zod-client regen (#64/#41): NOT near-free (14+ endpoints behind) per spec L94 — add a brief comment on those issues (or a note in this plan's PR body) that the snapshot was recaptured but the client regen stays parked. Do NOT run `bun run generate:api` in this issue.
- Build + format. Commit: `chore: OpenAPI review + recapture openapi snapshot for phase-5 endpoints (#74)`.

## Task 4 — Capstone e2e (D13: NO score assertion)
- Extend `src/Voidforge.Tests/Colonize/FullLoopEndToEndTests.cs` (or add a sibling capstone `[Fact]` in the same collection) covering: register → build economy → **storage fills and halts a producer** (`HaltReason.OutputStorageFull`) → **transport ore away** → **producer resumes** → **cancel a build** → **recall a fleet** → **colonize** → verify final state via the READ API. NO score assertion (D13).
- Add missing helpers to `src/Voidforge.Tests/Support/IntegrationApiExtensions.cs`: place-a-building + poll-until-`Halted`; cancel a ship build (`DELETE /api/planets/{id}/ship-queue/{buildId}`) and/or cancel construction (`DELETE …/buildings/{slot}/construction`); a resume assertion (poll `Buildings[..].Status` back to `Operational`). Recall (`Recall`/`CancelForStatus`) and colonize helpers already exist — reuse.
- **Flaky-suite discipline:** keep it ONE cohesive test; reuse existing polling helpers + `TestTimeouts`; lean on the test host's fast balance durations so halt/resume happen quickly; avoid fixed sleeps.
- Build + format. Commit: `test: capstone full-loop e2e — halt/resume/cancel/recall/colonize (#74)`.

## Task 5 — docs note + PR (coordinator)
- Update `technical-design/domain-model.md` / `authentication.md` (ownership helper) and `technical-design/testing.md` (capstone coverage) as touched. Note the parked frontend regen.
- PR `feat/74-api-polish-capstone` → `phase-5`, "Closes #74. Folds #63." Self-merge on green CI.

## Acceptance (from the issue)
- [ ] One ownership-check implementation; grep finds no per-file variants (Task 1).
- [ ] Every non-2xx response is ProblemDetails incl. 409 concurrency + validation 400s (Task 2).
- [ ] Invalid `?status=` returns 400 — fixes #63 (Task 2).
- [ ] OpenAPI document reflects all Phase 5 endpoints (Task 3).
- [ ] Capstone e2e passes (Task 4).
- [ ] `dotnet test` green (CI).

## Notes / judgment calls (documented, not asked — autonomous per handover)
- Ownership is a shared **helper**, not a blanket endpoint filter, because planet-owner vs fleet-owner vs both (`Unload`) can't be uniformly filtered — this is the minimal change that satisfies "one implementation."
- Collapsing error unions to `ProblemHttpResult` slightly reduces per-status OpenAPI fidelity; Task 3's `.ProducesProblem` metadata restores documentation of the distinct codes.
- Frontend zod regen stays parked (spec L94); only the committed OpenAPI snapshot is recaptured.
- If any "gap" is already satisfied once read, say so and skip — don't add redundancy.
