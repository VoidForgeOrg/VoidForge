# Verifier Research: Live Soak-Run Verifier

> **Status:** Design doc — **all three assertion tiers shipped** (Tier 1 #96, Tier 3 #98, Tier 2 #99).
> §2–§8 are the design of record the shipped code follows; where the code refined a detail, the notes
> below and the source under `src/Voidforge.SoakTests/` are authoritative.
>
> **Shipped (`src/Voidforge.SoakTests/`):** a standalone `dotnet test` project — deliberately
> **not** in `src/Voidforge.slnx`, so no CI lane or Stop-hook runs it — that boots the real host
> against an isolated auto-created `voidforge_soak_test` DB with the §8.2 theme config, drives the
> two-user scenario over real HTTP for a bounded window (`SOAK_WINDOW_SECONDS`, default 120),
> drains via **aggregate-quiesce + settle-cap**, snapshots via Marten, and asserts:
> - **Tier 1 (I1–I11)** — invariants, hard-assert (#96).
> - **Tier 3 (O1–O6)** — structural outcomes, hard-assert; O4–O6 window-gated at `>= 300s` (#98).
> - **Tier 2 (baselines & blessing)** — aggregate comparison vs a **blessed baseline**
>   (`baselines/soak-baseline.json`) within per-metric tolerances, rendered as a `[BAND]`/`[WARN]`
>   matrix. **Advisory only — never fails the test** (§2/§7.3); `SOAK_EMIT_BASELINE=1` emits the
>   machine-readable aggregates that feed the N-run blessing envelope. Blessed with N=5 × 300s runs.
>
> Run: `SOAK_WINDOW_SECONDS=300 dotnet test src/Voidforge.SoakTests/Voidforge.SoakTests.csproj`.
> Validated: 90 s / 300 s runs pass all 11 invariants; the run genuinely exercised parallel
> Planet-stream appends (observed `23505` optimistic-concurrency collisions absorbed by the retry
> ladder — I3 dead-letters empty, I5 clean). **Deferred to follow-ups:** nightly CI/soak lane +
> dedicated DB, envelope-based drain, multi-theme matrix, the golden-diff sibling, and converging
> Tier 3 onto the shared `SoakAggregates` (once a CI net exists).
>
> **Two findings from v1 (acted on):**
> 1. **Transport is own-planets-only, so §8.2's A→B lifeline was infeasible — the scenario was
>    reshaped.** `FleetEndpoints.ValidateMissionPrecondition` (`FleetEndpoints.cs:459-461`) 403s a
>    Transport whose destination is not owned by the caller, so "Player A delivers ore to Player B"
>    cannot happen. v1 instead runs a faithful **within-player supply line**: Player A colonizes a
>    second planet in another system, then Transports ore from its homeworld to that **own** colony —
>    a same-owner destination, so the mission launches 200 and the cargo delivers + auto-unloads on
>    arrival. Player B keeps the Colonize → mid-transit Recall leg for reschedule coverage. Validated:
>    a 300 s run shows `A: supply transport -> 200`, A owning 2 planets / 2 fleets, and the delivery
>    completing over the real scheduler. **§8.2's prose below is superseded** by this shape (its
>    "B receives A's ore → ingot-storage-full on B" chain does not occur; see the §8.2 note).
> 2. **Open Question #1 (drain signal) is resolved for v1** by aggregate-quiesce (§5.4), avoiding the
>    unverified `wolverine_*_envelopes` schema entirely. Envelope introspection remains a possible
>    hardening but is not required.
>
> **Sibling:** [`verifier-golden-diff.md`](verifier-golden-diff.md) — the complementary
> *exact-logic* verifier that pins the pure domain math (checkpoint arithmetic, cascade
> resolution order, rate derivation) with byte-reproducible golden fixtures. The two are
> designed to interlock: golden-diff proves *the math is right in isolation*; the soak run
> proves *the math stays right when the real ~5 s durable scheduler, real wall-clock, and
> real optimistic-concurrency retries are all in play at once*. Where this doc says "exact
> diff is out of scope," that scope belongs to the sibling.

## 1. Thesis & Scope

### 1.1 What a live soak run is

Boot the **real** `Program` host (Marten + Wolverine on Postgres), tuned via config so the
economy reaches rich states inside a bounded window (~5 minutes of wall-clock), drive it
with a scripted multi-user scenario over real HTTP, let the **real Wolverine durability
agent** fire the scheduled completions/arrivals/cascade-checks on its ~5 s poll, then snapshot
the resulting world state and assert correctness against a **tiered** assertion strategy.

It is a *system* test, not a *unit* test. Nothing is faked:

- **Clock is real.** The app injects `TimeProvider.System` as a singleton
  (`src/Voidforge.Api/Program.cs:93`); endpoints stamp events with `timeProvider.GetUtcNow()`
  and the domain computes values as a pure function of a passed-in `now`
  (`ResourcePool.GetCurrentValue`, `src/Voidforge.Api/Domain/ResourcePool.cs:15-19`). The soak
  run does **not** substitute a fake clock — that is the entire point.
- **Scheduler is real.** Build/ship completion, fleet arrival, and the storage-full /
  depletion / starvation cascade checks are durable scheduled messages
  (`bus.ScheduleAsync(...)`, e.g. `src/Voidforge.Api/Endpoints/ShipConstructionScheduling.cs:16`),
  persisted to Postgres in the triggering transaction and delivered by the single-node
  durability agent (`DurabilityMode.Solo`, `src/Voidforge.Api/Program.cs:48`) polling on a
  ~5 s wall-clock interval (`technical-design/architecture.md:229`; configurable via
  `opts.Durability.ScheduledJobPollingTime`, architecture.md:318). ADR 0001 is the record of
  this "schedule optimistically, validate on arrival" model.
- **Concurrency is real.** `EventAppendMode.Quick` (`Program.cs:34`) with `FetchForWriting`
  optimistic concurrency; multiple paths append to one `Planet` stream (parallel scheduled
  completions plus HTTP commands). A loser fails with `ConcurrencyException`, which for
  scheduled work is replayed by the retry ladder `Program.cs:60-66` (50 ms → 1 s) and for HTTP
  is mapped to 409 by `ConcurrencyConflictExceptionHandler` (`Program.cs:82`). The old
  `MaximumParallelMessages(1)` throttle was **deliberately removed** (architecture.md:252), so
  message processing is genuinely parallel.

### 1.2 Why this is the faithful choice

The pure-domain tests and the golden-diff verifier both freeze exactly the variables this
verifier exists to exercise. A golden fixture invokes handlers directly at computed instants
with no scheduler and no wall-clock (see the `#71` cascade suite and
`Tests/Halting/DepletionCascadeTests.cs`, which drive `CheckPoolDepletedHandler` /
`CheckInputStarvedHandler` by hand — `DepletionCascadeTests.cs:45,68`). That is correct for
proving the math, but it means **no test currently exercises**:

- the ~5 s **poll granularity** and the resulting event-timing dynamics (completions bunching
  in one poll, or landing after a later HTTP command — the inversion source of ADR 0002);
- genuine **parallel** appends to one Planet stream and the `ConcurrencyException` → retry
  ladder under contention (only a single deterministic forced collision exists today —
  `ClaimRaceTests`, per `testing.md:51`);
- **cascade chains at realistic proportions** — a depletion that actually empties a pool on
  wall-clock, freeing energy that resolves a real overload, all without a test hand-invoking
  the handler at a pre-computed instant.

A live soak run is the only harness that puts all three in play simultaneously, on the real
binary, for a sustained window. If the engine has an emergent bug that only appears when the
scheduler, the clock, and concurrency interact, this is the test that finds it.

### 1.3 Why exact byte-reproducibility is out (and that is fine)

A soak run is **not** byte-reproducible, by construction. Three independent mechanisms
guarantee two runs of the same script diverge:

1. **Time-derived reads.** Every materialized resource value is `checkpoint + rate × elapsed`
   evaluated at the wall-clock instant of the read (`ResourcePool.GetCurrentValue`,
   `ResourcePool.cs:15-19`). Two snapshots taken microseconds apart differ; two runs whose HTTP
   round-trips differ by 3 ms produce different `IronOre.CurrentValue`. Materialized values are
   continuous functions of real time.
2. **Event-order races.** With parallel processing and a ~5 s poll, the *order* in which a
   scheduled completion and an HTTP command land on a Planet stream is a race. Checkpoint
   *stored* state is timing-robust because handlers checkpoint at the message's **carried**
   timestamp (`message.CompletesAt` / `ArrivesAt`), not delivery time (ADR 0001; ADR 0002 §"The
   shipped protection"). But event *ordering* and hence intermediate materialized reads are not
   reproducible.
3. **Order-independence is only approximate.** ADR 0002 concedes the accrual model is "inert,
   not order-independent": an out-of-order delivery can **under-credit** a pool by
   `(rate delta) × (inversion window)` — bounded, conservative, never corrupting, but a real
   numeric difference between runs (`ResourcePool.cs:9-14`; ADR 0002 §"Residual under-credit").

Plus, **in the soak run's unseeded default** (no `WorldGeneration:Seed`), the world is generated with
entropy: an unseeded `new Random()` for planet coordinates and `Guid.NewGuid()` for world-gen ids
(`WorldSeeder.BuildWorld`), and a `Random.Shared`-picked homeworld over the candidate query
(`PlayerEndpoints`). So even the starting board differs run to run. (Setting a seed makes coordinates,
world-gen ids, and a lowest-id homeworld pick reproducible — see `verification-config.md` — but the soak
run deliberately does *not* set one; it asserts invariants and count/threshold outcomes, not the exact
board.)

**Conclusion:** exact-diff assertions (`expected == actual` on the full snapshot) are the wrong
tool for this harness — they would flake on every run. Reproducibility-dependent checks are the
sibling golden-diff verifier's job. This verifier instead asserts **invariants that hold for
any legal run**, **totals within a tolerance band**, and **structural outcomes that the script
was designed to produce**. That is §2.

## 2. Assertion Strategy (the heart)

Three tiers, ordered by how hard they may fail. A soak run's flakiness budget is spent almost
entirely at Tier 2/3, so Tier 1 must be things that are *never* legitimately violated.

### Tier 1 — Invariants (hard gate, must never flake)

These are properties the engine claims to guarantee for **every** legal state at **every**
instant. A Tier-1 failure is a real bug (or a real regression) — it is a hard CI failure. Each
is checked against the **final drained snapshot** (see §5.4) and, where cheap, against every
intermediate snapshot the driver captured.

Concrete, code-grounded invariants:

| # | Invariant | Where it comes from / how to check |
|---|-----------|------------------------------------|
| I1 | **No pool below 0.** `IronOre`, `IronIngot`, `IronOreDeposit` current value `>= 0` for every planet. | `GetCurrentValue` clamps to `[0, cap]` (`ResourcePool.cs:18`); a negative value means a snapshot bypassed the clamp or a stored checkpoint went negative. Read every `Planet` via Marten, evaluate at snapshot `now`. |
| I2 | **No pool above its cap.** `IronOre.CurrentValue <= IronOre.StorageCapacity`; same for ingot. Deposit `<= initial deposit` (its `StorageCapacity` is seeded to the initial value, `Planet.cs:35-39`). | Same clamp. Cap comes from `WorldGenOptions.IronOre/IngotStorageCapacity`. |
| I3 | **No dead-lettered messages.** `wolverine_dead_letters` is empty. | A dead letter means the retry ladder was exhausted — architecture.md:245-249 argues this is "effectively impossible" for the transient collisions in scope, so a non-empty table is a genuine defect. Query the table directly over the same Npgsql connection (see §5.3). |
| I4 | **No 5xx responses.** Every driven request returned a modeled status (2xx, or an *expected* 4xx the script asked for — 409/403/503). | The driver records every response code. A 500 means an unhandled exception escaped a handler — e.g. a concurrency loser that failed to map to 409 (`ConcurrencyConflictExceptionHandler`, `Program.cs:81`). |
| I5 | **Concurrency conflicts surface as 409, never lost.** Any conflict the driver provoked came back 409 (HTTP) and the losing command had **no** partial effect; scheduled-side conflicts left no stuck aggregate (see I6). | `Program.cs:59-65,81`; regression baseline is `SameStreamConcurrencyTests` (architecture.md:252). The driver counts 409s as *expected*, not failures. |
| I6 | **Everything scheduled by T resolves by T + margin.** After the drain window (§5.4), **no** building is `UnderConstruction` past its `CompletesAt + margin`; no ship build is `Active` past its deadline; no fleet is `InTransit` past `ArrivesAt + margin`. `ConstructionHalted`, `Queued`, and `Halted` are modeled (ingot-starved / capacity-waiting) states, not stuck, so they are excluded. | Nothing may be stuck. `margin` = poll interval + retry-ladder span ≈ **7 s** (ADR 0002 §"bounded ~7 s") plus a safety multiple. Statuses: `BuildingStatus` (`BuildingStatus.cs`), `ShipBuildStatus` (`ShipBuildStatus.cs`), `FleetStatus.InTransit` (`FleetStatus.cs`). |
| I7 | **Slot counts within cap.** For every planet, the count of live building slots (not `Cancelled`/`Demolished` tombstones) `<= BuildingSlotCount`. | `WorldGenOptions.BuildingSlotCount` (default 6, `WorldGenOptions.cs:8`); tombstone statuses per `BuildingStatus.cs:17,27`. |
| I8 | **Roster / queue uniqueness.** A ship id appears **at most once** across planet rosters (`Planet.Ships`), ship queues (`Planet.ShipQueue`), and non-`Disbanded` fleets (`Fleet.Ships`) — never in two places at once. Every `ShipQueue` entry is a live in-progress build (no tombstone status exists there). *v1 verifies uniqueness only; asserting that a completed ship never vanishes from **every** collection needs a durable completed-ship ledger and is a documented follow-up.* | The no-double-count rule is documented in `ScoreCalculator.CountShips` (`ScoreCalculator.cs:82-125`): assembly atomically MOVES a ship from roster to fleet; disband reverses it. The verifier re-runs this cross-check as an assertion instead of a scoring convenience. |
| I9 | **Fleet cargo non-negative and bounded.** `CargoIronOre >= 0`, `CargoIronIngot >= 0`, and `GetCargoLoad() <= GetCargoCapacity(...)` for the fleet's ship mix. | `Fleet.cs:38-39,78-82`; capacity from `ShipsBalanceOptions` (`ShipsBalanceOptions.cs:8-9`). |
| I10 | **Energy multiplier in `[0, 1]`.** Every planet's productivity multiplier is `<= 1` and `>= 0`; the blackout floor is exactly `0` — a planet with consumers but no generation yields `0` (`GetProductivityMultiplier`, `Planet.Energy.cs`). Halted/tombstone slots draw the documented fractions, not full rating. | `PlanetResponse.Energy.ProductivityMultiplier` (`PlanetResponse.cs:41-44`); halted 5% floor `BuildingSpecs.HaltedDrawFactor`; tombstones draw nothing (`BuildingStatus.cs:15-16,20-22`). |

**Investigating resource conservation as an invariant.** This is tempting but **must not be
asserted as an exact equality** — verify the model before trusting it:

- The 1:2 ratio and the deposit drain are exact *as rates*: a Drill adds ore at `10/s`
  (`BuildingSpecs.cs:8-12`), a Refinery consumes `5/s` and emits `2×` that as ingots
  (`BuildingSpecs.cs:34-41`), and the deposit drains at exactly `-oreInflow`
  (`Planet.cs:114`). Decimals are exact (no float drift — architecture.md:184-196), so the
  *instantaneous rate* relationships hold precisely.
- **But the integrated totals are NOT conserved**, for three concrete reasons the code makes
  unavoidable:
  1. **Cap clamping silently discards overflow.** When a buffer is full, `GetCurrentValue`
     clamps to `StorageCapacity` (`ResourcePool.cs:18`) — ore/ingots produced beyond the cap
     vanish from the ledger. Real storage-full cascades (`CheckStorageFullHandler`) then halt
     the producer, but any accrual computed past the cap before the checkpoint is lost.
  2. **The under-credit residual.** An out-of-order delivery under-credits a pool by
     `(rate delta) × (inversion window)` (ADR 0002 §"Residual under-credit"; pinned by
     `ReverseOrderShortfallIsTheInvertedWindowAtThePreCompletionRate`). Conserved quantities
     would need this residual back.
  3. **The refinery draws the *stored* buffer, not just inflow.** `EffectiveOreConsumption`
     lets refineries pull the stored ore buffer down (net ore rate goes negative) until it is
     empty, then clamps consumption to inflow (`Planet.cs:144-153`). "Ingots = 2 × ore
     consumed" is only true against *actual* consumption, which is buffer-dependent, not a
     simple function of drill output.

  So the correct conservation check is a **bounded inequality**, not an equality, and it lives
  in **Tier 2**, not Tier 1:

  > `ore_mined = initialDeposit − currentDeposit` (this *is* exact and monotonic — see I11).
  > `ingots_ever_produced <= 2 × ore_mined + startingIngots` (produced can only be `≤` because
  > cap-clamp and starvation destroy potential ingots, never create them).
  > Total ore accounted `(Σ planet ore buffers + Σ fleet cargo ore + ore_refined_away)`
  > reconciles to `ore_mined + startingOre` **within ε**, where ε absorbs cap-clamp loss and
  > the inversion residual.

- **One conservation fact that *is* Tier-1 safe (I11): the deposit is monotonically
  non-increasing.** `IronOreDeposit.Rate = −oreInflow <= 0` always (`Planet.cs:114`;
  `CurrentOreInflow >= 0`), and `Checkpoint` never rewinds time (`ResourcePool.cs:24-31`), so a
  planet's deposit current-value can never rise between two snapshots. Assert
  `deposit(t2) <= deposit(t1)` for every consecutive snapshot pair. This is a clean hard
  invariant and a strong smoke test for the whole checkpoint engine.

### Tier 2 — Tolerance comparison vs a blessed baseline (headroom, warn/soft-fail)

Compare run aggregates against a recorded **baseline** (§3) within `ε` (absolute) or `X%`
(relative). These are the "the numbers are in the right ballpark" checks. A Tier-2 miss is a
soft failure: it flags for human review rather than hard-failing CI, because jitter is expected.

Candidate aggregates (all computable from the Marten snapshot):

- **Per-player score** via the real `ScoreCalculator.Compute(...)` (`ScoreCalculator.cs:54-58`) —
  a single scalar folding planets + buildings + ships + resources, so it is the best
  single-number health signal. Compare to baseline within, say, ±10%.
- **Global totals:** Σ ore mined (exact, from I11), Σ ingots produced, count of completed
  buildings, count of ships built, count of planets colonized, count of cascades fired.
- **The conservation reconciliation** from I11's inequality — asserted as "within ε."

**Techniques to shrink jitter** (make Tier 2 tight enough to be useful):

- **Anchor snapshots to game events, not arbitrary wall-clock instants.** Do not read "at
  minute 3." Instead, quiesce first (stop driving, let the scheduler drain, §5.4), then read
  once. A single post-drain read removes most read-time jitter because all pending completions
  have landed and rates have settled.
- **Read using an event-derived timestamp where possible.** Materialize resource values with a
  *fixed* `now` captured once at snapshot time and reused for every planet in that snapshot, so
  the whole snapshot is internally consistent (all pools evaluated at the same instant). The
  `ScoreCalculator` already takes a single `now` for exactly this reason
  (`ScoreCalculator.cs:54-58,206`). Never evaluate planet A at `now₁` and planet B at `now₂`
  within one comparison.
- **Prefer counts and monotonic totals over instantaneous buffer levels.** "12 buildings
  completed" is far less jittery than "ore buffer = 8,431.2." Weight the baseline comparison
  toward counts and toward the exact `ore_mined` quantity.

### Tier 3 — Structural outcomes ("did the scripted story happen")

The scenario (§8) is written to *make specific things occur*. Tier 3 asserts they did, with
generous timing slack so contention never turns a real success into a false negative. These are
existence/threshold checks, not value checks:

- Player owns `>= N` planets (the colonize legs succeeded).
- `>= N` ships reached a roster (the shipyard actually produced).
- At least one building went `Halted`/`ConstructionHalted` and later resumed (a cascade fired
  and recovered) — detectable from the presence of a `HaltReason` in captured intermediate
  snapshots (`BuildingSlotResponse.HaltReason`, `PlanetResponse.cs:48`).
- At least one fleet completed a Transport (cargo delivered — a planet buffer rose on a planet
  with no local producer, or a fleet returned empty after unload).
- At least one depletion actually emptied a deposit to 0 (if the scenario tuned for it, §4).

**Slack rule:** every Tier-3 deadline is `expected_time + k × pollInterval` with `k >= 3`. A
build that "should finish in 20 s" is only a failure if it hasn't finished after, say, 20 + 20 s.
Contention legitimately delays completions by a few polls; Tier 3 must not punish that.

## 3. Baseline Recording & Blessing

A baseline is what Tier 2 compares against. It must be *loose enough not to rot* on every legal
run yet *tight enough to catch a real regression*.

### 3.1 What to store

Store **only** low-jitter, run-stable quantities, plus the config that produced them:

```jsonc
// technical-design/research/baselines/soak-baseline.json  (illustrative)
{
  "scenarioId": "two-user-economy-v1",
  "config": {                       // the exact env overrides used — the baseline is only
    "WorldGeneration__IronOrePool": "4000",   // valid for THIS config (§4)
    "Balance__Drill__BuildDurationSeconds": "20",
    "...": "..."
  },
  "windowSeconds": 300,
  "tolerances": { "scorePct": 10, "countAbs": 1, "oreReconEpsilon": 250 },
  "expected": {
    "oreMinedTotal":        { "value": 6000, "kind": "exact-ish", "tol": "oreReconEpsilon" },
    "buildingsCompleted":   { "value": 11,   "kind": "count",     "tol": "countAbs" },
    "shipsBuilt":           { "value": 4,    "kind": "count",     "tol": "countAbs" },
    "planetsColonized":     { "value": 3,    "kind": "count",     "tol": "countAbs" },
    "cascadesFired":        { "value": 2,    "kind": "count-min", "tol": "countAbs" },
    "player0Score":         { "value": 830,  "kind": "scalar",    "tol": "scorePct" }
  }
}
```

Do **not** store raw buffer levels, per-event timestamps, ids, or coordinates — all rot every
run. Store *distributions/ranges* only if a scalar proves too jittery (e.g. store
`shipsBuilt: [3, 5]` as an accepted range rather than a point).

### 3.2 Re-bless workflow

1. Run the soak N times (e.g. 5) on a known-good commit.
2. Take the **min/max envelope** (or mean ± 2σ) of each low-jitter quantity across the N runs;
   that envelope, widened by the configured tolerance, becomes the stored baseline.
3. Commit the baseline JSON alongside this doc.

### 3.3 Detecting "legit game change" vs "regression"

The baseline is keyed by `scenarioId` **and** the embedded `config` block. A change to balance
constants, rates, or world-gen defaults *should* move the baseline — so:

- If the failing diff correlates with a deliberate change to `BalanceOptions` /
  `BuildingSpecs` / `WorldGenOptions` / `ScoringSpecs` in the same PR, it is a **legit game
  change**: re-bless (step 3.2) as part of that PR, and the baseline diff is reviewable
  evidence of the intended effect.
- If the diff appears with **no** balance/world/scoring change in the PR, it is a **regression
  candidate**: hard-investigate before re-blessing. Never re-bless to make a red run green
  without a corresponding intentional change — that is how a baseline silently absorbs a bug.
- Rates that surface through `BuildingSpecs` (drill/refinery throughput, 1:2 ratio, 5% floors) now
  bind from the **`Economy`** config section into `EconomyRates` (`Program.cs:125-128,139`) — a
  change to any of them, whether via config or by editing the `EconomyRates` defaults, is a "legit
  game change" that must move the baseline and be called out in the PR description.

## 4. World / Config Tuning for a Rich 5-Minute Run

The goal: within ~300 s of wall-clock, a handful of simulated users should complete several
buildings, build several ships, run at least one fleet mission, and trigger at least one
cascade — **without** the economy either starving instantly or being so over-provisioned that
no cascade ever fires.

### 4.1 The levers (all proven-bindable)

Everything below binds from config sections `Balance` / `WorldGeneration`
(`Program.cs:115-116`) and is already overridden via `__`-env-vars by the integration host
(`AppFixture.cs:29-49`), so the mechanism is proven. Env-var name = section path with `__`.

| Lever | Env var | Default | Soak target (illustrative) | Why |
|-------|---------|---------|----------------------------|-----|
| Drill build time | `Balance__Drill__BuildDurationSeconds` | 60 (`BalanceOptions.cs:11`) | **20** | Comfortably `>` the ~5 s poll so completions land across *several* polls, not all in one; short enough to complete a few in 300 s. |
| Refinery / Generator / Shipyard build time | `Balance__{Refinery,Generator,Shipyard}__BuildDurationSeconds` | 90 / 60 / 120 | **20–30** | Same reasoning. Keep all ≥ ~3× poll interval. |
| Ship build time | `Balance__{ColonyShip,CargoVessel}__BuildDurationSeconds` | 300 / 120 | **15 / 15** | Default ColonyShip (300 s) alone eats the whole window; compress so several ships finish. |
| Ship ingot cost | `Balance__{ColonyShip,CargoVessel}__IngotCost` | 1000 / 400 | **60 / 60** | Default costs vs. compressed durations would over-drain the buffer; keep drain sustainable (cf. `AppFixture.cs:38-41`). |
| Ship speed | `Balance__Ships__{ColonyShip,CargoVessel}__SpeedPerSecond` | 0.05 / 0.10 (`ShipsBalanceOptions.cs:8-9`) | **~100** | Defaults make a transit take *hours*; `AppFixture` uses `1000` for instant arrival. For a soak, pick a speed that makes a typical transit **tens of seconds** so `InTransit` is a *real observed state* that crosses several polls — not instantaneous. With `CoordinateRange 1000`, inter-planet distance is up to ~3,400 units, so `~100/s` ⇒ transits ~20–35 s. |
| Ore deposit size | `WorldGeneration__IronOrePool` | 50000 (`WorldGenOptions.cs:7`) | **see 4.2** | Governs whether depletion fires in-window. |
| Ore / ingot storage cap | `WorldGeneration__IronOre/IronIngotStorageCapacity` | 10000 / 5000 | **see 4.2** | Governs whether storage-full fires in-window. |
| Starting ore / ingots | `WorldGeneration__StartingIronOre/StartingIronIngots` | 500 / 100 | **bump** (e.g. 2000 / 800) | So construction doesn't starve before the economy ramps. |
| World size | `WorldGeneration__SolarSystemCount` / `PlanetsPerSystem` | 5 / 3 | **≥ 40 / 3** | Enough uncolonized planets for N users + colonize legs without 503s (`AppFixture.cs:29` already bumps to 80 for the same reason). |

Rates **are** tunable (this doc's earlier "not tunable" claim is stale as of the
deterministic-engine work in #95). Drill ore rate, Refinery consumption, the 1:2
`RefineryIngotOutputFactor`, generator/draw energy, and the 5% halted/shipyard-idle floors all
live in `EconomyRates` (`src/Voidforge.Api/Domain/EconomyRates.cs`), bound from the **`Economy`**
config section (`Program.cs:125-128`) and installed into the process-global `BuildingSpecs` table at
`Program.cs:139` — so `Economy__DrillOreRatePerSecond`, `Economy__RefineryOreConsumptionPerSecond`,
`Economy__RefineryIngotOutputFactor`, `Economy__HaltedDrawFactor`, etc. are all `__`-env-bindable
exactly like `Balance`/`WorldGeneration`. The defaults equal the old constants (Drill `+10 ore/s`,
Refinery `5 ore/s → 10 ingot/s`), so leaving `Economy` unset preserves the §4.2 math. The genuinely
non-tunable rules are the per-type building *shapes* (which resource each type produces) and the
`ShipsBalanceOptions` cargo/speed structure. **A change to any `Economy` value moves the Tier-2
baseline** and must be called out in the PR (see §3.3), just like `BuildingSpecs` constants were.

### 4.2 The central tension — you tune for what you want to observe

The homeworld seeds one Drill + one Refinery + one Generator, all Operational
(`PlayerEndpoints.cs:128-135`). With the fixed rates that means, per homeworld:

- Deposit drains at `10/s` ⇒ default 50,000-pool empties in **5,000 s** (~83 min) — *far*
  outside a 5-minute window. **Depletion never fires** at default pool size.
- Ore buffer fills at `+10 − 5 = +5/s` ⇒ default 10,000 cap from 500 fills in **1,900 s**
  (~31 min). **Ore storage-full never fires** at default cap.
- Ingot buffer fills at `+10/s` (minus construction drain) ⇒ default 5,000 cap from 100 fills
  in **490 s** (~8 min). **Ingot storage-full barely misses** the window at default cap.

So **big pools/caps mean the depletion and storage-full cascades never fire inside 5 minutes.**
The tuning must *match the cascade you want to observe*:

- **To observe depletion:** shrink `IronOrePool` to ~**3,000–4,000** (empties in 300–400 s with
  one drill), or script a second drill to double the drain, or both. Assert Tier-3 "a deposit
  hit 0 and its drills went `Halted`/`ResourceDepleted`."
- **To observe ore storage-full:** shrink `IronOreStorageCapacity` to ~**2,000** (fills in
  ~300 s at +5/s from a bumped start), and *don't* have a fleet cart the ore away.
- **To observe ingot storage-full:** shrink `IronIngotStorageCapacity` to ~**2,000** and keep
  ingot construction drain low so the buffer actually fills.
- **To observe starvation cascades:** run the deposit dry (above) so the refinery drains the ore
  buffer and then halts `InputStarved`, which starves ingot construction/ship builds
  (`DepletionCascadeTests.cs` is the deterministic analogue).

A single soak scenario can't maximize all of these at once — a full deposit that stays rich for
depletion is the opposite of a small deposit that depletes. Pick a scenario **theme** (§8 tunes
for depletion + one storage-full) and set the baseline accordingly. Run multiple themed
scenarios if broad cascade coverage is wanted.

## 5. Harness Architecture

### 5.1 Option (a): external process over HTTP vs. Option (b): in-process Alba host + Marten reads

| Concern | (a) External process hitting HTTP | (b) In-process `AlbaHost.For<Program>()` + direct Marten reads |
|---------|-----------------------------------|----------------------------------------------------------------|
| Fidelity of the *app under test* | Highest — a fully separate deployed binary | High — same `Program`, same real Wolverine Solo scheduler, real HTTP pipeline through Alba scenarios (auth, `ConcurrencyConflictExceptionHandler`, ProblemDetails all real) |
| Driving the app | Real HTTP client | Real HTTP via Alba `Scenario(...)` — reuses the entire `IntegrationApiExtensions` SDK unchanged |
| **Observing full world state** | **Weak** — no full-world-snapshot endpoint, no list-all-planets, no leaderboard (all deferred). Would have to fan out `GET /api/solar-systems` (`?pageSize=200`, gives `PlanetIds`, `IntegrationApiExtensions.cs:404-412`) then one `GET /api/planets/{id}` per planet — N+1, and still can't see `wolverine_dead_letters` | **Strong** — `store.LightweightSession()` + `session.Query<Planet>()/<Fleet>()/<Player>().ToListAsync()` reads the *complete* world in one shot (tests already do this — `DepletionCascadeTests.cs:85-91`), and the same connection can query `wolverine_dead_letters` / incoming envelopes for I3/I6 |
| Setup / teardown | Needs process orchestration, a separate DB, health-wait, port wiring | Reuses the proven `AppFixture` boot path (`AppFixture.cs:52-66`): test-DB safety check, schema drop, `WorldSeeder` reseed |
| CI cost | Higher (container/process management) | Lower (one xUnit test process) |

**Recommendation: Option (b) — in-process Alba host, drive over real HTTP, observe via direct
Marten snapshot reads.** It keeps everything faithful that matters for *this* verifier (real
scheduler, real clock, real concurrency, real HTTP handler pipeline) while giving **complete,
cheap** state observation — which the API surface cannot provide (there is deliberately no
full-world-snapshot / list-all / leaderboard endpoint). Reserve Option (a) for a later
*deployment smoke* against a real container in a pre-release environment, if wanted; it is not
the right tool for the correctness soak because it is half-blind to global state.

> **Marten read discipline (`testing.md:90-99`):** never `await using` the DI-owned
> `IDocumentStore` — that disposes the singleton and kills Npgsql for the rest of the run.
> `var store = host.Services.GetRequiredService<IDocumentStore>();` then
> `await using var session = store.LightweightSession();`.

### 5.2 Spawning N simulated users

Register N players up front and capture each `RegisterPlayerResponse` (carries `PlayerId`,
`ApiKey`, `HomeworldId`) via `host.RegisterPlayer(prefix)`
(`IntegrationApiExtensions.cs:21-33`). Every subsequent request for that user sends
`X-API-Key: {ApiKey}` (the helper SDK already does this on every call). Registration is
anonymous (`PlayerEndpoints.cs:28`), claims a distinct homeworld under the guarded
`Random.Shared` pick + optimistic-concurrency retry (`PlayerEndpoints.cs:67-88,118`), and seeds
that homeworld's starting economy — so N registrations give N independent, contending players
out of the box.

### 5.3 The scripted-scenario driver over time

A driver is a sequence of per-user timelines. Structure it as small async tasks — one per
simulated user — each running its own script and sharing a `Stopwatch` deadline:

```csharp
// sketch — reuses IntegrationApiExtensions verbatim
var deadline = Stopwatch.StartNew();
var players = await Task.WhenAll(Enumerable.Range(0, N)
    .Select(i => host.RegisterPlayer($"Soak{i}_")));

var driver = players.Select(p => RunUserScript(host, p, deadline, TimeSpan.FromMinutes(5)));
await Task.WhenAll(driver);          // real concurrency: users contend on planet streams
```

Each `RunUserScript` interleaves: place buildings (`PlaceBuilding`), queue ships
(`QueueShip` / `BuildRosterShips`), assemble + launch fleets (`AssembleFleet` → `Launch`,
`IntegrationApiExtensions.cs:165-203`), colonize/transport/recall, with small real `Task.Delay`
pauses so actions spread across polls. **Record every response status** (for I4/I5) and,
optionally, capture periodic intermediate snapshots (for I11 monotonicity and Tier-3 cascade
detection). The driver must **not** invoke handlers directly (`CompleteFleetArrivalHandler`
etc.) the way the deterministic tests do — the whole point is to let the *real* durable
scheduler deliver completions and arrivals. (Contrast: `LaunchAndArriveInstantly` /
`CompleteArrivalWithRetry` at `IntegrationApiExtensions.cs:345-387` short-circuit the scheduler;
those are for deterministic tests and are the wrong tool here.)

### 5.4 Snapshot capture & knowing when the run is "done"

"Done" is two phases:

1. **Stop driving** when `Stopwatch >= window` (5 min). Simulated users stop issuing commands.
2. **Drain the scheduler** before snapshotting. Because completions/arrivals/cascade-checks fire
   on the ~5 s poll (architecture.md:229) with up to the ~7 s race margin (ADR 0002), a snapshot
   taken the instant driving stops would show spurious `UnderConstruction`/`InTransit` that are
   merely *pending*, not *stuck* — false I6 failures. So after stopping, **quiesce**: poll the
   world until no `Planet`/`Fleet` aggregate has a `CompletesAt`/`ArrivesAt` in the past-minus-margin,
   or a hard drain cap (e.g. 3 × poll ≈
   15–20 s) elapses. Only then take the **single authoritative snapshot** with one fixed
   `now = TimeProvider.System.GetUtcNow()` reused for the whole snapshot (§2 Tier-2 jitter rule).

Snapshot = `Query<Planet>()`, `Query<Fleet>()`, `Query<Player>()` to lists, plus a raw SQL
`SELECT count(*) FROM {schema}.wolverine_dead_letters` (schema `voidforge`, architecture.md:180)
over an Npgsql command on the same connection string. Feed all of it to the Tier 1/2/3 asserters.

## 6. Nondeterminism Ledger

Every source of run-to-run variation, and which tier absorbs it:

| Source | Reference | Absorbed by |
|--------|-----------|-------------|
| Read-time value = `checkpoint + rate × elapsed(realtime)` | `ResourcePool.cs:15-19` | **Tier 2** (ε/%) and the §5.4 single-`now` drained snapshot; never exact-diffed |
| Event-order race (scheduled completion vs HTTP command) | ADR 0001; `Program.cs:34,60-66` | **Tier 1** invariants (order-independent by design) + **Tier 2** for totals |
| Under-credit residual on inverted delivery | ADR 0002 §"Residual under-credit"; `ResourcePool.cs:9-14` | **Tier 2** ε on conservation reconciliation; bounded, never Tier-1 (never corrupts) |
| ~5 s poll granularity (events fire late) | architecture.md:229,318 | **Tier 3** timing slack (`k × pollInterval`); §5.4 drain before snapshot |
| Concurrency retry backoff timing (50 ms–1 s) | `Program.cs:60-66` | **Tier 1** I5/I6 (conflicts resolve, nothing lost); adds to I6 margin |
| Unseeded planet coordinates → travel distances/times vary | `WorldSeeder.cs:57,96-99` | **Tier 3** slack (transit deadlines are `+k×poll`); **Tier 2** count-based, not distance-based |
| `Guid.NewGuid()` ids everywhere | `WorldSeeder.cs:61,69` | Not asserted on — verifier never checks specific ids, only counts/relationships (I8) |
| `Random.Shared` homeworld pick (unseeded default) over the id-ordered candidate query | `PlayerEndpoints` (`OrderBy(p => p.Id)`) | **Tier 3** (owns ≥N planets), not "owns planet X" |
| Cap-clamp overflow loss | `ResourcePool.cs:18` | **Tier 2** ε on conservation; **Tier 1** as inequality only (I11, produced ≤ theoretical) |

The guiding rule: **anything time- or order-derived is a Tier-2/3 band; only order-*independent*
guarantees are Tier-1.**

## 7. Operational Concerns

### 7.1 CI cost & gating

A single run is ~5 min wall-clock plus boot (~schema migrate) plus drain (~20 s) — call it
6–7 min. That is too slow for per-push. **Gate it as a nightly job and/or a pre-merge gate on
protected branches**, not on the `unit` fast lane. The existing lane split is the model:
`Category=Unit` runs DB-free in seconds (local Stop-hook + CI `unit` job), the full
`Category=Integration` lane runs on the CI `test` job (`testing.md:53-58`). The soak run is a
*third* lane — tag it `[Trait("Category", "Soak")]` and run it only in the nightly workflow, so
it never blocks a normal push and never gets pulled into the coverage gate.

### 7.2 Dedicated database isolation (critical)

The soak run **must use its own database**, e.g. `voidforge_soak`, not the shared
`voidforge_test`. Two reasons:

- The project's Stop-hook quality gate auto-runs the suite on stop, and a concurrent
  `dotnet test` against the shared test DB corrupts the shared state — this is a *known,
  recorded* hazard (`quality-gate-hook-races-test-runs`; `ci-test-job-flaky-kill`). A 5-minute
  soak sharing `voidforge_test` would collide with any hook- or CI-triggered run for its entire
  duration, corrupting both.
- The `AppFixture` DB-name safety check refuses to drop a schema unless the DB name contains
  `test` (`AppFixture.cs:52-57`). Name the soak DB `voidforge_soak_test` (or relax/duplicate
  that guard for a `soak` substring) so the drop-and-reseed reset still works while staying
  clearly isolated from `voidforge_test`.

Set it the same way the fixture sets everything else: `ConnectionStrings__Marten` env var before
boot (`AppFixture.cs:13-20`, the `WithWebHostBuilder`-avoiding path — do not regress to the
overload, `testing.md:20`).

### 7.3 Flakiness control

- **Tier 1 is the hard gate.** Any I1–I11 violation fails the nightly, red. These are chosen to
  never fire on a legal run.
- **Tier 2 is headroom.** A miss warns / soft-fails and pings for review; it does not by itself
  turn CI red (it is jitter until proven otherwise). Persistent Tier-2 drift across nightlies is
  the regression signal.
- **Tier 3 carries slack.** Every deadline is `expected + k × pollInterval (k ≥ 3)`; a Tier-3
  miss is a real "the scripted story didn't happen," worth failing, but only after the slack.
- **Teardown hang is cosmetic.** After all assertions pass, Wolverine's durability agent may
  retry a disposed Npgsql source and hang teardown (`testing.md:108-110`). Wrap the soak job in
  `timeout` so a cosmetic hang doesn't look like a failure.
- **Flaky-kill awareness.** A soak (like the `test` job) can be killed mid-run; treat a run that
  produced no snapshot + a dup-key/connection flood as *infrastructure*, re-run once before
  diagnosing (`ci-test-job-flaky-kill`).

## 8. Proposed Component Breakdown + Example Scenario

### 8.1 Components

| Component | Responsibility |
|-----------|----------------|
| `SoakHostFixture` | Boots `AlbaHost.For<Program>()` against the dedicated `voidforge_soak_test` DB with the §4 soak config env-vars; mirrors `AppFixture` (`AppFixture.cs`). |
| `ScenarioScript` | Declarative per-user timeline (register → build → queue ships → fleet missions → colonize), executed with real `Task.Delay` pacing. Reuses `IntegrationApiExtensions`. |
| `SoakDriver` | Runs N `ScenarioScript`s concurrently under one `Stopwatch` deadline; records every HTTP status; captures periodic intermediate snapshots. |
| `WorldSnapshot` | Post-drain reader: `Query<Planet/Fleet/Player>` + `wolverine_dead_letters` count, all at one fixed `now`. |
| `Tier1Invariants` | Asserts I1–I11. Hard gate. |
| `Tier2Baseline` | Loads `soak-baseline.json`, computes aggregates (incl. `ScoreCalculator.Compute`), compares within tolerance. Soft. |
| `Tier3Outcomes` | Existence/threshold checks with slack against the scenario's declared intent. |
| `SoakReport` | Emits pass/warn/fail per tier + the raw aggregates (for re-blessing, §3.2). |

### 8.2 A concrete 5-minute scenario: "Two-user economy with a transport lifeline and a depletion"

> ⚠️ **v1 reshape (implemented):** the "transport lifeline from A to B" below is **infeasible** —
> Transport requires a same-owner destination (`FleetEndpoints.cs:459-461`), so A→B 403s. The
> shipped v1 driver instead runs a **within-player supply line**: Player A colonizes a second planet
> in another system and Transports ore from its homeworld to that **own** colony (launches 200,
> delivers + auto-unloads on arrival); Player B keeps Colonize → mid-transit Recall. Read the A/B
> walkthrough below as the *original* design intent — the code in `src/Voidforge.SoakTests/` is the
> source of truth for the shipped scenario.

**Config theme:** tuned for depletion + an ingot-storage-full near the end. Key overrides:
`IronOrePool=4000`, `IronIngotStorageCapacity=2500`, `StartingIronOre=2000`,
`StartingIronIngots=800`, all build durations `20 s`, ship durations/costs `15 s`/`60`,
`CargoVessel__SpeedPerSecond=100`, `SolarSystemCount=40`.

**Player A (the industrialist), homeworld H_A:**
- t≈0 s: register. Homeworld seeds Drill+Refinery+Generator (`PlayerEndpoints.cs:128-135`).
- t≈2 s: place a **second Drill** → deposit now drains at `~20/s`, so the 4,000-pool empties
  near **t≈200 s** — a depletion inside the window.
- t≈5 s: place a **Shipyard**; once Operational (~t≈25 s) queue **3 CargoVessels**
  (`BuildRosterShips`, up to `ShipyardParallelBuilds=3`, `BuildingSpecs.cs:45`).
- t≈60 s: assemble a fleet of 2 CargoVessels, **load ore**, **launch Transport** to Player B's
  homeworld (a ~20–35 s transit → `InTransit` observed across polls).
- t≈200 s: the deposit empties → both Drills halt `ResourceDepleted`; the Refinery drains the
  ore buffer then halts `InputStarved`; ingot construction/ship builds starve
  (the live analogue of `DepletionCascadeTests.cs`).

**Player B (the colonizer), homeworld H_B:**
- t≈0 s: register.
- t≈5 s: place Shipyard; queue **1 ColonyShip** (15 s build).
- t≈40 s: assemble the ColonyShip into a fleet, **launch Colonize** at a `FindUncolonizedPlanet`
  target (`IntegrationApiExtensions.cs:418-441`) → owns a 2nd planet on arrival.
- t≈90 s: recall one in-flight fleet mid-transit (exercises `FleetRecalled`, the return leg).
- Meanwhile B **receives** A's ore transport (its ore buffer rises with no local extra drill),
  and with `IronIngotStorageCapacity=2500` its seeded ingot inflow (`+10/s` from 800) hits the
  cap near **t≈170 s** → ingot **storage-full** cascade (Refinery halts `OutputStorageFull`).

**Assertions this scenario checks:**

- *Tier 1:* I1–I2 (no pool <0 or >cap on any of the ~120 planets); I3 (`wolverine_dead_letters`
  empty despite two users contending on their streams + parallel ship completions sharing a
  poll); I4 (no 5xx); I5 (any 409s the colonize/register race produced were mapped, not 500);
  I6 (after drain, no build past `CompletesAt+margin`, no fleet past `ArrivesAt+margin` — A's
  starved ship builds are `Halted`, a *modeled* terminal-ish state, not "stuck under
  construction"); I7 (H_A has Drill+Drill+Refinery+Generator+Shipyard = 5 ≤ 6 slots); I8 (every
  built ship is on exactly one roster or in one fleet); I9 (A's loaded fleet cargo ≤ capacity);
  I10 (multipliers ≤ 1); **I11 (both deposits monotonically non-increasing across the periodic
  snapshots; H_A's hit exactly 0)**.
- *Tier 2:* `oreMinedTotal` for A ≈ `4000` (deposit fully drained) within ε; B's score within
  ±10% of baseline; buildings-completed and ships-built counts within ±1.
- *Tier 3:* B owns ≥ 2 planets (colonize succeeded); ≥ 4 ships reached rosters across both
  users; ≥ 1 `ResourceDepleted` halt observed (A) **and** ≥ 1 `OutputStorageFull` halt observed
  (B) in the captured snapshots; ≥ 1 Transport delivered (B's ore buffer rose); the recalled
  fleet returned `Stationed` at origin with nothing delivered (`FleetRecalled` semantics,
  architecture.md:310).

## 9. Open Questions / Decisions for the Team

1. **Drain-completeness signal. — RESOLVED for v1 via aggregate-quiesce (no envelope query).** §5.4
   quiesces by polling `Planet`/`Fleet` aggregates only (no envelope query). Is querying
   `wolverine_outgoing_envelopes` for due-but-undelivered messages an acceptable "scheduler is idle"
   signal, or should we expose a small health/introspection hook? Getting this wrong is the most
   likely source of false I6 failures. **v1 decision:** poll aggregates for anything overdue past a
   ~10 s margin, then wait a fixed settle (~15 s) capped at ~30 s — no dependency on the (unverified)
   `wolverine_*_envelopes` schema. Held across the validation runs with zero false I6 failures.
   Envelope introspection stays open only as an optional precision hardening.
2. **How many scenario themes?** One themed run can't maximize depletion *and* both storage-full
   variants (§4.2). Do we run one broad scenario nightly, or a small matrix of themed scenarios
   (depletion / ore-full / ingot-full / starvation), each with its own baseline?
3. **Tier-2 hardness.** Should a persistent Tier-2 drift (e.g. 3 consecutive nightlies outside
   band) auto-escalate to a hard failure, or always stay human-reviewed?
4. **Baseline storage & N.** How many blessing runs (§3.2) constitute a trustworthy envelope,
   and do we store point values + tolerance or explicit min/max ranges? Where do baselines
   live — committed JSON next to this doc, or a CI artifact store?
5. **Dedicated DB provisioning in CI.** The nightly needs its own `voidforge_soak_test`
   database/service container isolated from `voidforge_test` (§7.2). Confirm the CI Postgres
   service can host a second DB, and decide whether to relax the `AppFixture` name-guard
   (`AppFixture.cs:52-57`) for a `soak` substring or keep `test` in the name.
6. **Run length vs. signal.** Is 5 minutes the right window, or should the nightly run longer
   (e.g. 15–30 min) to stress the inversion/residual behavior and longer cascade chains, given
   it's off the per-push path anyway?
7. **Relationship to golden-diff.** Should a Tier-1 soak failure automatically trigger a
   golden-diff run on the same commit to localize whether the bug is in the pure math (sibling
   catches it) or purely in the scheduler/concurrency interaction (only the soak catches it)?
8. **Post-MVP #81 interaction.** When rewind-and-reapply lands (ADR 0002 §"post-MVP path",
   issue #81), the under-credit residual shrinks and the Tier-2 conservation ε should tighten.
   Track the ε as a balance-owned constant so it moves with that change.
