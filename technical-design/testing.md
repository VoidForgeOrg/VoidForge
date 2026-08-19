# Testing

## Stack

- **xUnit** — test framework
- **Alba** — integration testing for ASP.NET Core (HTTP scenario testing)
- **coverlet** — code coverage (70% line threshold)

## Test Host Setup

### Critical Pattern

`AppFixture` uses the env var approach — set `ConnectionStrings__Marten` before calling `AlbaHost.For<Program>()` with no arguments.

```csharp
Environment.SetEnvironmentVariable("ConnectionStrings__Marten", connStr);
Host = await AlbaHost.For<Program>();
```

**Do NOT** use `AlbaHost.For<Program>(Action<IWebHostBuilder>)` — it triggers an `ObjectDisposedException` in .NET 9 due to a `WithWebHostBuilder` + `RunJasperFxCommands` disposal race.

### Shared Fixture

All integration tests share a single `AppFixture` via xUnit collection:

```csharp
[Collection(IntegrationCollection.Name)]
public sealed class MyTests(AppFixture fixture)
{
    private readonly IAlbaHost _host = fixture.Host;
}
```

This avoids booting the app per test class (Marten schema migration is slow).

## Shared Helpers (`Support/`)

Since #62, API-driving helpers are shared extension methods on `IAlbaHost` — do not re-declare them privately in test classes. Add missing helpers to the shared layer instead.

- `Support/IntegrationApiExtensions.cs` — register/get/build/assemble/launch/poll helpers. All assert success (200) unless the name says otherwise (`PostForStatus`); polling helpers return the last-seen state on timeout so the caller's assertion reports the failure. `CompleteArrivalWithRetry` / `LaunchAndArriveInstantly` invoke `CompleteFleetArrivalHandler` directly with a bounded `ConcurrencyException` retry — always drive handler-invoked arrivals through these, never a bare `Handle(...)` call, because a direct call races the real durable scheduler on the same Fleet stream and bypasses Program.cs's #39 retry ladder.
- **The universal "no 5xx" tripwire.** Every asserting helper runs through `Send`, which enforces the product invariant *a caller must never receive a 500*: any request that comes back 5xx (except the modeled **503**, returned for `NoUncolonizedPlanets`) throws **`ServerErrorException`** — carrying the method, URL, and body — instead of `StatusCodeShouldBe`. Nothing in the suite (or the soak driver) catches it, so a server error always fails the test loudly, wherever it happens (atomic call, deep inside a composite like `BuildRosterShips`, or a GET poll). Modeled non-200s (403/409/503) throw the **catchable** `UnexpectedStatusException`, so contention-tolerant callers can `catch` those without ever masking a 500. Negative tests that need to inspect a raw code use `PostForStatus` / `CancelForStatus` (raw int, no tripwire).
- `Support/TestTimeouts.cs` — the suite's named poll cadence and deadlines (`PollInterval`, `Completion`, `StockRecovery`, `QueueDrain`, `Arrival`, `FullLoopArrival`). Use these instead of inline `TimeSpan` literals; they time out real HTTP polling and are unrelated to the app's injected `TimeProvider`.

Usage shape (the class keeps its `[Collection]` + `AppFixture` wiring):

```csharp
var owner = await _host.RegisterPlayer("MySuite_");
var shipId = await _host.BuildRosterShip(owner);
var planet = await _host.PollUntil(owner, p => p.Buildings.Count > 0, TestTimeouts.Completion);
```

Deliberately local (not shared): raw-Marten world mutations (`ColonizeSecondPlanetForOwner`, `UncolonizedPlanetId`), the deterministic forced-collision in `ClaimRaceTests.ContestedPlanetAppendLoses...`, and `PlayerRegistrationTests`' inline scenarios that assert raw registration responses.

## Test Lanes (`Category` traits)

Every test class carries exactly one xUnit trait: `[Trait("Category", "Unit")]` (pure-domain, no host/DB) or `[Trait("Category", "Integration")]` (needs the Alba host + Postgres, alongside `[Collection(IntegrationCollection.Name)]`).

- **Fast lane (no DB):** `dotnet test src/Voidforge.slnx --filter Category=Unit` — runs in seconds. This is what the local Stop-hook (`.claude/hooks/quality-gate.sh`) and the CI `unit` job run, so neither needs Postgres.
- **Full lane + coverage:** the CI `test` job runs the whole suite unfiltered with `--collect:"XPlat Code Coverage"`; the 70% line gate (`src/coverlet.runsettings`) is enforced only on this complete run.

New test classes MUST be tagged — an untagged class silently runs in neither filtered lane.

The out-of-solution `Voidforge.SoakTests` project is the sole exception to the Unit/Integration rule: its `TwoUserEconomySoakTests` is tagged `[Trait("Category", "Soak")]` (see the Soak lane below).

### Soak lane (`src/Voidforge.SoakTests/`, out of the solution)

The live soak-run verifier (design: `technical-design/research/verifier-live-soak-run.md`) lives in a
**separate `Voidforge.SoakTests` project that is deliberately NOT in `src/Voidforge.slnx`**, so it is
invisible to `dotnet test src/Voidforge.slnx`, the CI `test`/`unit` jobs, and the Stop-hook. Each
scenario boots the real host against **its own isolated, auto-created DB** (separate from the shared
`voidforge_test`, so it never collides with a hook- or CI-triggered run), drives players over real HTTP
for a bounded window while the real Wolverine scheduler fires completions, drains the scheduler, takes one
authoritative snapshot, then asserts the three tiers (Tier 1 invariants + Tier 3 outcomes hard-gate; Tier
2 baselines are advisory). Run it explicitly:

```bash
dotnet test src/Voidforge.SoakTests/Voidforge.SoakTests.csproj                                  # all scenarios, serial, 120s
SOAK_WINDOW_SECONDS=300 dotnet test src/Voidforge.SoakTests/Voidforge.SoakTests.csproj          # reaches the 300s cascades
dotnet test src/Voidforge.SoakTests/Voidforge.SoakTests.csproj --filter "FullyQualifiedName~InputStarvation"  # one scenario
bash scripts/soak-matrix.sh                                                                     # all scenarios IN PARALLEL
```

**The scenario seam.** A scenario is one self-contained `SoakScenario` (`SoakScenario.cs`): `Id`, its own
`DbName`, an `ApplyConfig` theme (env-var overrides before boot — never the connection string, the fixture
owns that), a `SoakScenarioBody` (registration + scripting), a `ScenarioIntent`, and an optional Tier-2
`BaselineFile`. The evaluation half — all three tiers, the drain (`SchedulerQuiescence`), the snapshot
reader, and the `IntegrationApiExtensions` HTTP vocabulary — is scenario-agnostic and reused as-is
(`SoakRunner` runs the whole boot→drive→drain→snapshot→assert pipeline for any scenario). Tier 3's outcome
*set* is intent-driven: `ExpectsTransport`/`ExpectsDepletion` gate O4/O5 and `ExpectedHalts` drives O6, so
a differently-shaped scenario asserts only its own story.

**Adding a scenario** = (1) a `SoakScenario` instance in `SoakScenarios.cs` (theme + body + intent, a
`DbName` containing `test`, baseline or `null`); (2) a one-line `SoakHostFixture` subclass; (3) a
`[CollectionDefinition]`; (4) a 3-line test class that calls `SoakRunner.RunAsync`; (5) a line in
`scripts/soak-matrix.sh`. `SoakScenarios.InputStarvation` is the worked example — a single-player scenario
that isolates the `InputStarved` halt, with no Tier-2 baseline (Tier 1 + Tier 3 gate it).

**Parallelism is one process per scenario, not xUnit collections.** In-process the soak assembly runs
**serially** (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`) because the host installs
a process-global economy rate table (`BuildingSpecs.Current`, "fixed for the process lifetime; differing
rates require a separate process") and binds config via process-global env vars set just before boot — two
scenario hosts in one process would share both. `scripts/soak-matrix.sh` launches one `dotnet test --filter`
process per scenario instead, each on its own DB; a nightly CI matrix would do the same with one runner per
cell.

## Log Level Under Test

`AppFixture` pins `Logging__LogLevel__{Marten,Wolverine,Npgsql}=Warning` (and `Default=Information`) via env vars before booting the host. Alba defaults the environment to `Development` (loading `appsettings.Development.json`'s Debug levels), which otherwise makes Marten/Npgsql/Wolverine log every SQL statement — hundreds of MB per run that bury real failures. Do not remove these overrides; they follow the same env-var path as the connection string (never the `WithWebHostBuilder` overload).

## Cascade Scenario Coverage (#71)

`game-design/engine.md` §"Cascading Events" (L48–52) requires each dependency chain to resolve **within a single checkpoint** — one commit / one `RebaseRates` re-derivation — so state stays consistent. Energy is never an event; it is re-derived inside every composition-changing `Apply`, so "within a single checkpoint" means the trigger AND its downstream halts/resumes AND the energy re-derivation land in one post-commit read. The `Cascade/` suite proves the four scenarios, the edge cases, and even-split distribution; the head/tail slices they build on live in `Halting/DepletionCascadeTests`, `Halting/IngotStarvationCascadeTests`, `Planets/PlanetHaltingTests`, `Planets/PlanetEnergyTests`, and `Planets/PlanetDemolitionTests`.

| engine.md item | Test | Kind |
| --- | --- | --- |
| Scenario 1 — ore depletes → all Drills halt → freed energy resolves overload | `Cascade/CascadeScenarioTests.DepletionOnOverloadedPlanetHaltsAllDrillsAndRecoversProductivityInOneCheckpoint` | integration |
| Scenario 2 — ore starvation → Refinery halts → ingot buffer empties → construction + ship build halt (single flow) | `Cascade/CascadeScenarioTests.OreDepletionStarvesRefineryThenHaltsBothIngotConsumersAlongTheChain` | integration |
| Scenario 3 — building **completes** → overload → dependent rates throttle | `Cascade/CascadeScenarioTests.BuildingCompletionTipsPlanetIntoOverloadInTheCompletionCommit` | integration |
| Scenario 4 — demolish (endpoint) frees energy → overload resolves in the start-demolition commit | `Cascade/CascadeScenarioTests.DemolishingAConsumerResolvesOverloadInTheDemolitionCommit` | integration |
| Edge 5 — simultaneous depletion + storage-full at one instant → one consistent, bounded, idempotent state | `Cascade/CascadeEdgeCaseTests.SimultaneousDepletionAndStorageFullResolveToOneConsistentCheckpoint` | unit |
| Edge 6 — all producers halted (blackout) → stable on 5% idle floors, all rates 0, queries throw-free | `Cascade/CascadeEdgeCaseTests.AllProducersHaltedLeavesPlanetStableOnIdleFloors` | unit |
| Even-split 7 — refineries share one Drill's ore (aggregate clamps to inflow; no per-consumer tracking) | `Cascade/EvenSplitContentionTests.EvenSplitClampsAggregateRefiningToTheSingleSharedDrillInflow` | unit |
| Even-split 8 — construction + ship build share the ingot buffer → empty together, halt together | `Cascade/EvenSplitContentionTests.SharedIngotBufferEmptiesForBothConsumersAtTheSameInstant` | unit |

No new machinery (design D5): all halting/depletion/demolition/ingot behaviour already exists (#69/#70/#72/#83); #71 is test-only. Integration tests use the deterministic direct-handler-invocation pattern (`InvokeHandler` + `PredictX` deadline math + pool-pinning via oversized `CargoLoadedFromStorage`) — **no wall-clock waits**. Scenario 2's full chain is intentionally multi-checkpoint (three scheduled checks); its single-checkpoint claim is scoped to the ingot-consumer tail (one `CheckIngotStarved` pauses every consumer together). Edge cases and even-split proofs are pure-domain unit slices — the individual handler commit paths are already covered, and the novel content (two evaluators at one instant; the scalar-pool clamp; a shared buffer emptying for both consumers) is an aggregate property expressed directly without the runtime-marginal integration host. Even-split 7 uses 3 refineries vs 1 drill so the `min(demand, inflow)` clamp genuinely bites (2-vs-1 is tautological — demand equals inflow at every multiplier).

## Capstone & Contract Coverage (#74)

- **Capstone e2e** — `Colonize/FullLoopEndToEndTests.CapstoneHaltResumeCancelRecallColonizeVerifiedThroughTheReadApi` stitches the whole Phase-5 surface into one flight (register → build economy → storage-full **halt** → transport ore away → **resume** → **cancel** a build → **recall** a fleet → **colonize**) and verifies final state through the read API. NO score assertion (D13 — scoring moved to #67/#68). Halt/resume/arrival legs use the deterministic direct-handler-invocation pattern (a real-scheduler ore-pool fill would take ~1900s); everything else runs through the real HTTP API.
- **OpenAPI contract** — `OpenApi/OpenApiContractTests` fetches the live `/swagger/v1/swagger.json` and asserts every current route+method is present, so a new endpoint missing from the emitted doc fails the build. The committed frontend snapshot (`frontend/app/openapi/voidforge.json`) recapture + the zod client regen (#64/#41) stay parked (spec L94 — not near-free).

## Known Pitfalls

### Do Not Dispose DI-Owned IDocumentStore

```csharp
// WRONG — disposes the singleton, kills Npgsql for all subsequent tests
await using var store = _host.Services.GetRequiredService<IDocumentStore>();

// CORRECT — DI owns the lifetime
var store = _host.Services.GetRequiredService<IDocumentStore>();
await using var session = store.LightweightSession();
```

### Test Database

- DB name: `voidforge_test`
- Default connection: `Host=localhost;Port=5432;Database=voidforge_test;Username=postgres;Password=voidforge_dev`
- PostgreSQL runs via Docker (container: `dockerfiles-postgres-1`)
- Reset between full runs: drop and recreate the DB if schema changes

### Wolverine Teardown Hang

After all tests pass, Wolverine's durability agent may retry connections to a disposed Npgsql data source, causing the test process to hang during teardown. This is cosmetic — all tests complete successfully. Use `timeout 120 dotnet test ...` in scripts if needed.

## Coverage

Enforced via `src/coverlet.runsettings`:
- Threshold: 70% line coverage
- Excludes: `[Voidforge.Tests]*`
- Format: cobertura
- Enforced only by the CI `test` job (the full unfiltered run); the local quality gate and CI `unit` job run the DB-free `Category=Unit` lane and do not collect coverage
