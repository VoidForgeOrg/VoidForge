# Verification Configuration Profile

This document describes how to boot the Voidforge engine in a **deterministic, fully-specified**
state for the verification tooling (`technical-design/research/verifier-live-soak-run.md` and
`verifier-golden-diff.md`). It is the bridge between the engine's configuration surface and the
verifier harnesses that consume it.

## What is now controllable

Every game variable is specifiable from configuration. Config sources layer in the standard ASP.NET
order (`appsettings.json` → `appsettings.{Environment}.json` → environment variables), and each
section maps to `Section__Key` environment variables (the `__` → `:` convention already used by the
integration test host, `src/Voidforge.Tests/AppFixture.cs`).

| Section | Bound type | Controls |
|---------|-----------|----------|
| `WorldGeneration` | `WorldGenOptions` | World size, pools, storage caps, coordinate range/spread, starting stores, **and the determinism `Seed`** |
| `Balance` | `BalanceOptions` | Per-building/ship construction ingot cost & duration, ship speed & cargo capacity, demolition duration |
| `Economy` | `EconomyRates` | Drill/refinery throughput, the ore→ingot factor, generator/draw energy, halted & shipyard-idle floors, shipyard parallel builds |
| `Scoring` | `ScoringOptions` | Per-asset scoring weights |

Two rules that are **not** configurable and are structural, by design:
- `BuildingSpecs.ProducedResource` — the building-type → output-resource mapping (Drill→ore,
  Refinery→ingot).
- API-key secrets — generated with a cryptographic RNG (`PlayerEndpoints.GenerateApiKey`) and
  deliberately **not** determinized.

## Determinism

`WorldGeneration:Seed` (a nullable int; null in production) is the single determinism switch:

- **World generation** — when set, planet/solar-system coordinates **and** ids are drawn from a
  seeded PRNG in a fixed order (`WorldSeeder.BuildWorld`), so the same seed reproduces a
  byte-identical starting board.
- **Homeworld assignment** — when set, registration claims the lowest-id uncolonized planet (the
  candidate query is `OrderBy(Id)`), instead of a random pick.

**Caveat — concurrency.** Seeded homeworld assignment is reproducible only when players register
**sequentially**. Under concurrent registration the registration *order* is itself a race, so
who-gets-which-homeworld is not guaranteed even with a seed. The golden-diff harness should register
sequentially; the soak harness does not depend on homeworld determinism (it asserts "owns ≥ N
planets", not "owns planet X").

The engine clock is injected as a singleton (`TimeProvider.System`), but that registration is currently
**unconditional** (`Program`): substituting a fake/controlled clock is a **planned seam that still
requires a small engine change** (e.g. a config-gated `FakeTimeProvider`; see `verifier-golden-diff.md`
§2.1), not yet a supported configuration switch. The economy rate table is process-global and installed
once at startup (`BuildingSpecs.Configure`), so a run with differing economy rates must be its **own
process** — do not attempt to boot two hosts with different `Economy` values in one process.

## Example: a deterministic, rich-in-5-minutes profile

An illustrative environment-variable bundle (mirrors the soak-run research doc §4 tuning, plus a
fixed seed and clean round-number rates for readable golden fixtures):

```bash
# Determinism
WorldGeneration__Seed=1

# World shape
WorldGeneration__SolarSystemCount=40
WorldGeneration__IronOrePool=4000
WorldGeneration__IronIngotStorageCapacity=2500
WorldGeneration__StartingIronOre=2000
WorldGeneration__StartingIronIngots=800

# Construction balance (fast completions across several scheduler polls)
Balance__Drill__BuildDurationSeconds=20
Balance__Refinery__BuildDurationSeconds=20
Balance__Generator__BuildDurationSeconds=20
Balance__Shipyard__BuildDurationSeconds=20
Balance__ColonyShip__BuildDurationSeconds=15
Balance__ColonyShip__IngotCost=60
Balance__CargoVessel__BuildDurationSeconds=15
Balance__CargoVessel__IngotCost=60
Balance__Ships__CargoVessel__SpeedPerSecond=100
Balance__Ships__ColonyShip__SpeedPerSecond=100

# Economy rates (clean values; defaults shown — override any leaf)
Economy__DrillOreRatePerSecond=10
Economy__RefineryOreConsumptionPerSecond=5
Economy__RefineryIngotOutputFactor=2
```

All values above are illustrative and belong to the verifier's own baseline; the defaults committed
in `appsettings.json` reproduce today's balancing placeholders exactly, so an unconfigured boot is
unchanged.

## Where the verifier lives

The verifier tooling itself (the soak driver, snapshot readers, and tiered asserters) is a **separate
folder / project** and a later plan. It boots the real `Program` host the same way `AppFixture` does
— env vars set before `AlbaHost.For<Program>()` (avoiding the `WithWebHostBuilder` overload,
`AppFixture.cs`) — against a **dedicated** database (`voidforge_soak_test`, per the research doc §7.2)
with the profile above.
