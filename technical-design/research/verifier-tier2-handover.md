# Handover — Soak Verifier Tier 2 (Baselines & Blessing)

> **✅ DONE (#99).** Tier 2 shipped as designed here. New files under `src/Voidforge.SoakTests/`:
> `SoakAggregates`, `Tier2Status`/`Tier2ToleranceKind`/`Tier2Result`/`Tier2Report`,
> `SoakBaselineMetric`/`SoakBaseline`, `Tier2Baseline` (render-only), `SoakBaselineEmitter`, and the
> blessed `baselines/soak-baseline.json` (N=5 × 300s envelope). Wired into `SoakReport` + the test with
> **no Tier-2 assert**. Deviations from this doc: Tier 3 was **left untouched** (Option B — the shared
> `SoakAggregates` is Tier-2-only for now; converge later once a CI net exists) and the `.csproj` uses a
> `baselines/*.json` **glob** (not an explicit filename) so the project builds cleanly before blessing.
> The kept-open follow-ups are unchanged (see §10).
>
> **For the next agent.** Tier 1 (invariants) and Tier 3 (structural outcomes) are shipped and on
> `main`. This is the handover to build **Tier 2 — tolerance comparison vs a blessed baseline**, the
> last of the three assertion tiers. Read this end-to-end before coding; it is written to be executable.
>
> **Design source of truth:** `technical-design/research/verifier-live-soak-run.md` — §2 "Tier 2",
> §3 "Baseline Recording & Blessing", §6 "Nondeterminism Ledger", §7.3 "Flakiness control", and the
> open questions §9 (#2, #3, #4, #8) all bear directly on this work.

## 1. Where things stand

The soak harness lives at **`src/Voidforge.SoakTests/`**, a standalone xUnit project **deliberately
out of `src/Voidforge.slnx`** — no CI lane, no Stop-hook. Run it manually against the isolated
`voidforge_soak_test` DB:

```bash
dotnet build src/Voidforge.SoakTests/Voidforge.SoakTests.csproj                       # analyzers run as errors
dotnet test  src/Voidforge.SoakTests/Voidforge.SoakTests.csproj                       # 120s default window
SOAK_WINDOW_SECONDS=300 dotnet test src/Voidforge.SoakTests/Voidforge.SoakTests.csproj # full story
```

Shipped:
- **Tier 1** (`Tier1Invariants.cs`) — I1–I11, hard-assert (#96).
- **Tier 3** (`Tier3Outcomes.cs`) — O1–O6 structural outcomes, hard-assert; O4–O6 window-gated at
  `SOAK_WINDOW_SECONDS >= 300` (#98, closed #97).

The single test `TwoUserEconomySoakTests.TwoContendingUsersLeaveEveryTier1InvariantIntact`:
1. `SoakDriver.RunAsync(window)` — drives the two-user scenario AND drains the scheduler internally
   (the snapshot loop captures deposits + building halts throughout).
2. `SoakSnapshotReader.ReadAuthoritativeAsync` — one post-drain read at a fixed `now` →
   `WorldSnapshot { Planets, Fleets, Players, Now, DeadLetterCount, HttpStatuses, DepositSeries }`.
3. `Tier1Invariants.Evaluate/AssertAll` + `Tier3Outcomes.Evaluate/AssertAll`, rendered by
   `SoakReport.Render` **before** asserting.

## 2. Goal of Tier 2

Compare run **aggregates** against a recorded, **blessed baseline** within a per-metric tolerance
(ε absolute or X% relative). Per §2/§7.3 this tier is **advisory, not a hard gate**: a miss is a
**WARN**, not a test failure — jitter is expected, and persistent drift across runs is the regression
signal. Concretely for the manual/nightly harness:

> **Tier 2 must NOT fail the xUnit test.** Only Tier 1 and Tier 3 hard-assert. Tier 2 computes,
> compares, and renders a PASS/WARN matrix. (When the nightly CI lane lands — a separate follow-up —
> that lane can parse the WARN lines and raise a soft signal / ping for review. See §9-#3: whether a
> persistent multi-night drift auto-escalates to hard is an open decision — leave it human-reviewed
> for v1.)

## 3. What already exists to reuse — do NOT reinvent

- **`SoakReport.cs` already computes the seed aggregates** and says so in its header comment ("The
  seed for a future Tier-2 blessing"):
  - `AppendOreMined` — per-planet `IronOreDeposit.StorageCapacity − GetCurrentValue(now)` (the exact,
    monotonic ore-mined quantity; **the lowest-jitter metric there is**).
  - `AppendScores` — per-player `ScoreCalculator.Compute(ownedPlanets, ownedFleets, now)` (the single
    scalar folding planets + buildings + ships + resources).
  Lift these formulas into a shared aggregate computer rather than duplicating them.
- **`Tier3Outcomes.cs` already computes the count aggregates** you'll bless:
  - colonies won = `Planets.Count(OwnerId is not null) − Players.Count`
  - ships produced = roster ships + non-Disbanded fleet ships + colonies won
  - ore mined total = `Σ (deposit.StorageCapacity − deposit.GetCurrentValue(now))`
  - observed halt reasons = union of `DepositSeries[*].Halts` and final `Buildings[*].HaltReason`
  Consider extracting a `SoakAggregates.Compute(WorldSnapshot)` used by BOTH Tier 3 and Tier 2 so the
  two tiers can't drift apart.
- **Tier shape to mirror exactly:** `InvariantResult`/`OutcomeResult` (record) →
  `Tier1Report`/`Tier3Report` (list + summary) → `Evaluate` + `AssertAll` static class, rendered by a
  `SoakReport.Append*` method. Copy this shape for Tier 2 (but `AssertAll` becomes a no-op / render-only
  — see §2).
- **`ScenarioIntent.cs`** — the pattern for centralising tunables as data + a window gate. The Tier 2
  baseline JSON is the analogous "declared expectations" artifact.
- **`ScoreCalculator`** is DI-resolved in the test (`host.Services.GetRequiredService<ScoreCalculator>()`).

## 4. Proposed design

Mirror the existing tiers. New files under `src/Voidforge.SoakTests/`:

| File | Responsibility |
|------|----------------|
| `SoakAggregates.cs` | `record` of the computed run aggregates + `Compute(WorldSnapshot, ScoreCalculator)`. Reuse the formulas above. Shared with Tier 3 if you refactor. |
| `SoakBaseline.cs` | The deserialized baseline model: `scenarioId`, `config` (echoed env theme), `windowSeconds`, `tolerances`, and `expected` metrics (value + kind + tolerance ref). |
| `Tier2Result.cs` | One metric's comparison: id, observed, expected, tolerance, `Tier2Status { WithinBand, Warn }`. (Two states — no hard fail.) |
| `Tier2Report.cs` | List of results + `AllWithinBand` (advisory) + a WARN summary. |
| `Tier2Baseline.cs` | `Evaluate(SoakAggregates actual, SoakBaseline baseline)` → `Tier2Report`; loads the JSON. Render-only, never asserts. |
| `baselines/soak-baseline.json` | The committed blessed baseline (see §5 for schema). |

**Baseline loading (project is out-of-solution):** add to the `.csproj` so the JSON copies to output:
```xml
<ItemGroup>
  <Content Include="baselines\soak-baseline.json" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```
Load with `System.Text.Json` (in-framework, no package) from
`Path.Combine(AppContext.BaseDirectory, "baselines", "soak-baseline.json")`. If the file is absent,
render "Tier 2: no baseline — run in emit mode to bless" and skip (don't throw).

**Emit / re-bless mode:** add a `SOAK_EMIT_BASELINE=1` env switch. When set, after computing
`SoakAggregates` the test serialises them to a machine-readable JSON (write to the test output dir, or
log a single-line JSON blob the blessing script can grep). This is what feeds the N-run envelope in §6
— far better than eyeballing `SoakReport`'s prose.

**Wiring in `TwoUserEconomySoakTests`:**
```csharp
var aggregates = SoakAggregates.Compute(snapshot, scoreCalculator);
var tier2 = Tier2Baseline.EvaluateOrSkip(aggregates, SoakConfig.WindowSeconds); // loads JSON, may be "skipped"
_output.WriteLine(SoakReport.Render(snapshot, tier1, tier2, tier3, scoreCalculator, driverResult.Events));
if (SoakConfig.EmitBaseline) SoakBaselineEmitter.Write(aggregates);
Tier1Invariants.AssertAll(tier1);
Tier3Outcomes.AssertAll(tier3);
// NOTE: no Tier2 assert — advisory only.
```
Extend `SoakReport.Render` with a `Tier2Report` param and an `AppendBaseline` matrix (mirror
`AppendOutcomes`): `[BAND]`/`[WARN] <metric>: observed X vs expected Y ±tol`.

## 5. Baseline JSON — schema & what to store

Store **only low-jitter, run-stable quantities**, plus the config that produced them. Do **NOT** store
raw buffer levels, per-event timestamps, ids, or coordinates — they rot every run (§3.1, §6).

```jsonc
{
  "scenarioId": "two-user-economy-v1",
  "windowSeconds": 300,                       // baselines are WINDOW-SPECIFIC — bless at 300s
  "config": {                                 // echo the SoakConfig theme this baseline is valid for
    "WorldGeneration__IronOrePool": "4000",
    "WorldGeneration__IronIngotStorageCapacity": "2500",
    "Balance__Drill__BuildDurationSeconds": "20"
    // ... the full set SoakConfig.ApplyEnvironmentOverrides sets
  },
  "tolerances": { "scorePct": 15, "countAbs": 1, "oreEpsilon": 250 },
  "expected": {
    "oreMinedTotal":     { "value": 7100, "kind": "exact-ish", "tol": "oreEpsilon" },
    "shipsProduced":     { "value": 4,    "kind": "count",     "tol": "countAbs" },
    "planetsColonized":  { "value": 1,    "kind": "count",     "tol": "countAbs" },
    "haltReasonsSeen":   { "value": 2,    "kind": "count-min", "tol": "countAbs" },
    "playerScoreMax":    { "value": 830,  "kind": "scalar",    "tol": "scorePct" }
  }
}
```

**Metrics to bless (ordered tightest → jitteriest):**

| Metric | Source | Jitter | Notes |
|--------|--------|--------|-------|
| `oreMinedTotal` | `Σ(deposit.cap − current)` | very low | Exact & monotonic (I11). Tightest anchor. |
| `planetsColonized` | colonies-won formula | low | Count. In this scenario reliably 1 at 300s (A colonizes, B recalls). |
| `shipsProduced` | roster+fleets+colonies | low | Count. Observed 4 in validation runs. |
| `haltReasonsSeen` | distinct observed HaltReasons | low | `count-min` ≥ 2 (ResourceDepleted + OutputStorageFull). |
| `playerScoreMax` (or per-player) | `ScoreCalculator.Compute` | medium | Scalar; use ±% not ±abs. Best single health signal but jittery — widest band. |

**Observed values (from the two shipped 300s validation runs, use as a starting point, then re-bless
per §6):** oreMinedTotal ≈ 7173, shipsProduced = 4, planetsColonized = 1, haltReasonsSeen = 2. Do NOT
treat these as blessed — they are two points; bless properly with N runs.

**Advanced / optional metric — conservation reconciliation.** §2 discusses ore/ingot conservation as a
**bounded inequality**, not an equality (cap-clamp discards overflow; the ADR-0002 under-credit
residual; the refinery draws the stored buffer). If you add it, it is a Tier-2 ε-band check, e.g.
`ingotsEverProduced ≤ 2×oreMined + startingIngots`. **Recommendation: defer to a v2** — "ingots ever
produced" is not directly on the snapshot (the ingot pool is current, capped, and drained), so it needs
new bookkeeping. Start Tier 2 with the counts + score + ore, which are all already computed.

## 6. Blessing workflow (§3.2)

1. On a **known-good `main`**, run the 300s soak **N times** (design suggests 5; §9-#4 is open — pick 5,
   note it in the PR). Use `SOAK_EMIT_BASELINE=1` so each run emits machine-readable aggregates.
2. Take the **min/max envelope** (or mean ± 2σ) of each low-jitter metric across the N runs; widen by
   the configured tolerance. That becomes the stored `expected` + `tolerances`.
3. Commit `baselines/soak-baseline.json` alongside `verifier-live-soak-run.md`.

**Re-bless discipline (§3.3) — put this in the PR template / doc:**
- A change to `Balance` / `WorldGeneration` / `Economy` / `Scoring` config or their defaults is a
  **legit game change**: re-bless as part of that PR; the baseline diff is reviewable evidence.
- A Tier-2 drift with **no** such change is a **regression candidate** — investigate, do **not** re-bless
  to make it green. That is how a baseline silently absorbs a bug.
- The baseline is keyed by `scenarioId` **and** the embedded `config` block **and** `windowSeconds`.

## 7. Open decisions (from design §9)

- **#2 How many themes?** One 300s themed run can't maximise depletion AND both storage-full variants.
  v1: one baseline for the shipped `two-user-economy-v1` theme. A themed matrix is a later follow-up.
- **#3 Tier-2 hardness.** Should a persistent multi-night drift auto-escalate to hard fail? **Keep
  human-reviewed for v1** (Tier 2 never fails the test).
- **#4 Storage & N.** Committed JSON next to the doc; N = 5 (suggested). Point value + tolerance, or
  explicit min/max range — pick one and be consistent.
- **#8 Conservation ε.** If/when you add the reconciliation metric, track its ε as a balance-owned
  constant so it tightens when rewind-and-reapply (#81, now closed) semantics change.

## 8. Gotchas (learned the hard way)

- **Analyzers run as errors** even though the project is out of the slnx: Meziantou **MA0048**
  (one type per file — give the enum/record their own files), **MA0002** (string ordering needs a
  comparer — order enums by the enum, not `.ToString()`), **CA1859** (use concrete collection types on
  private members). Build locally after every change; CI will NOT catch these (project not in slnx).
- **No `dotnet format` gate** on this project — match the existing style by hand (file-scoped
  namespaces, the `SoakReport.Emit` invariant-culture helper for interpolated `AppendLine`).
- **DB isolation:** `voidforge_soak_test` only. Never run the slnx suite (`voidforge_test`) concurrently
  with a soak or a Stop-hook run — shared-DB corruption is a recorded hazard.
- **Window matters:** the 120s default does NOT reach the cascades; **bless and compare at 300s**. At
  120s, `EvaluateOrSkip` should return "skipped" (baseline `windowSeconds` won't match), exactly like
  Tier 3's O4–O6 SKIP.
- **Non-determinism (§6):** the soak runs **unseeded** — coordinates, ids, and the `Random.Shared`
  homeworld pick vary run-to-run, and concurrent registration is itself a race. Seeding
  (`WorldGeneration__Seed`, from #95) fixes world-gen but **not** the concurrent-registration order, so
  it does not remove score jitter. Absorb the jitter with tolerances; do not chase exact values.

## 9. Verification

1. `dotnet build src/Voidforge.SoakTests/…` — clean (0 warnings under analyzers).
2. **No baseline file yet:** `SOAK_WINDOW_SECONDS=300 dotnet test …` → Tier 1 + Tier 3 still hard-pass;
   Tier 2 renders "no baseline — skipped". Test stays green.
3. **Emit mode:** `SOAK_EMIT_BASELINE=1 SOAK_WINDOW_SECONDS=300 dotnet test …` ×5 → collect aggregates,
   build `soak-baseline.json` per §6.
4. **With baseline:** `SOAK_WINDOW_SECONDS=300 dotnet test …` → Tier 2 matrix shows all `[BAND]`; test
   green. Then deliberately perturb one `Economy`/`Balance` value and confirm the relevant metric flips
   to `[WARN]` (proves the comparison bites) — revert after.
5. `dotnet build src/Voidforge.slnx` unaffected (Tier 2 touches only the out-of-solution project).

## 10. Suggested first steps

1. Extract `SoakAggregates.Compute(WorldSnapshot, ScoreCalculator)` from the formulas in
   `SoakReport`/`Tier3Outcomes` (refactor Tier 3 to consume it — keeps the two tiers consistent).
2. Add `SoakBaseline` + `Tier2Result`/`Tier2Report` + `Tier2Baseline.EvaluateOrSkip` (render-only).
3. Add `SOAK_EMIT_BASELINE` to `SoakConfig` + a `SoakBaselineEmitter`.
4. Wire into `SoakReport.Render` (Tier 2 matrix) and the test (no Tier-2 assert).
5. Bless with 5×300s runs; commit `baselines/soak-baseline.json`; open the PR (new issue,
   branch from `main`) and note the blessing runs + N in the description.

**Backlog is empty and we're past the phase plan** — open a fresh issue ("Soak verifier: Tier 2
baselines & blessing") and branch from `main`. The other deferred follow-ups remain: **nightly CI/soak
lane + dedicated DB** (arguably do this *with or before* Tier 2 so the WARN signal has somewhere to
land), **multi-theme matrix**, and the **golden-diff sibling** (`verifier-golden-diff.md`).
