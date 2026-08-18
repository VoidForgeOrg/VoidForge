# Research: Deterministic Golden-Diff Verifier

> **This is the ALTERNATIVE / complementary approach.** The team's primary plan is the
> live soak run described in the sibling doc [`verifier-live-soak-run.md`](verifier-live-soak-run.md)
> — a long-running host driven through the real HTTP API and the real Wolverine scheduler,
> asserting *invariants* (conservation, non-negativity, bounded staleness) rather than exact
> equality. This document researches the byte-exact counterpart: run the engine in-process
> with the verifier owning the clock and the event schedule, replay a fixed script of inputs,
> dump the resulting state, and diff it against a committed golden snapshot. Read the soak-run
> doc first; this one assumes its framing and only argues where exact reproducibility earns its
> extra cost. The two are complementary — see [§7](#7-complementarity-what-each-catches).

## 1. Thesis & scope

A golden-diff verifier answers exactly one question: **"did the game *logic* change?"** It runs a
scripted sequence of player actions against a world seeded from a fixed seed, drains every
scheduled game event in a canonical order at computed virtual timestamps, serializes the final
`Player` / `Planet` / `Fleet` state to a canonical form, and compares it — byte for byte — against
a recorded golden file. Any divergence is a regression (or an intended change that must be
re-blessed). Because the comparison is total and exact, it catches drift that invariant checks miss:
an off-by-one in a checkpoint value, a rate-rebasing tweak, a changed tie-break, a reordered
cascade that lands on a different-but-still-"valid" state.

**What it does *not* test — by construction.** The verifier deliberately removes the live runtime
from the loop. It does not exercise the Wolverine durability agent's ~5 s wall-clock polling
(architecture.md:229), the `EventAppendMode.Quick` optimistic-concurrency retry ladder
(`Program.cs:59-65`), out-of-order delivery, or any real concurrency. Those are precisely what the
sibling live-soak run is for. The golden-diff verifier tests the **pure domain math and the
resolution order of the cascade**, holding the scheduler constant; the soak run tests the
**scheduler and concurrency behaviour**, holding exactness loose. Neither subsumes the other.

**It is not a black-box network client.** Exact reproducibility is unattainable against the app as
it ships today (`Program.cs`) because the app fires future events from a wall-clock poller that
never consults `TimeProvider`. The verifier must therefore run the engine **in-process** — the same
`AlbaHost.For<Program>()` composition the test suite uses (testing.md:13-18) — with two seams
installed: a controllable clock, and verifier ownership of the event schedule. Everything below is
about paying for those two seams honestly.

### The determinism blocker is the runtime, not the math

The domain math is already deterministic. `ResourcePool.GetCurrentValue(now)` and
`ResourcePool.Checkpoint(now)` (`Domain/ResourcePool.cs:15-31`) are pure functions of a passed-in
`now`; aggregate methods take `now` as data (e.g. `Planet.CompleteBuilding(slot, completesAt)`,
`Fleet.Arrive(at)`). All resource quantities are `decimal` to avoid float drift (architecture.md:184).
There is exactly one stray `DateTimeOffset.UtcNow` in the whole API assembly, and it is auth
metadata, not game logic: `Documents/ApiKey.cs:8`. Verified:

```
$ grep -rn "DateTime.UtcNow\|DateTimeOffset.UtcNow" src/Voidforge.Api --include=*.cs
src/Voidforge.Api/Documents/ApiKey.cs:8:    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
```

So the math is not the problem. The problem is that *future game events are Wolverine durable
scheduled messages* dispatched by the durability agent on a ~5 s wall-clock poll (architecture.md:229,
`Program.cs:47` `DurabilityMode.Solo`), and that poller does **not** read `TimeProvider`. Advancing a
fake clock alone therefore fires nothing. The completion/check handlers, however, are thin,
idempotent, "validate-on-arrival" no-ops that checkpoint from the message's *carried* timestamp and
take `now` as data (`Endpoints/CompleteBuildingConstructionHandler.cs:24`,
`Endpoints/CheckStorageFullHandler.cs:25`, `Endpoints/CompleteFleetArrivalHandler.cs:28`). Invoking
them **directly** at a computed instant is fully deterministic — the tests already do this
(`Tests/Support/IntegrationApiExtensions.cs:345-387`, `LaunchAndArriveInstantly` /
`CompleteArrivalWithRetry` invoke `CompleteFleetArrivalHandler.Handle(...)` directly), and
testing.md documents the "direct-handler-invocation + `Predict*` deadline math" pattern (testing.md:40,
:85). The verifier generalizes that one-shot helper into a full, canonical-order event drainer.

## 2. Requirements for exact reproducibility

Four things must be controlled. Two are cheap and local; two are harness-level.

### 2.1 A controllable clock

`Program.cs:92` registers the clock unconditionally:

```csharp
builder.Services.AddSingleton(TimeProvider.System);
```

Every command and read path resolves this singleton and calls `GetUtcNow()` —
`PlayerEndpoints.Register` (`PlayerEndpoints.cs:54`), `PlayerEndpoints.Me`
(`PlayerEndpoints.cs:206`), `ShipEndpoints.Queue` (`ShipEndpoints.cs:43`),
`FleetEndpoints.Assemble` (`FleetEndpoints.cs:137`), and so on. Swap the singleton for a
`Microsoft.Extensions.Time.Testing.FakeTimeProvider` and the **entire** read/command path obeys
virtual time: a command stamps events with the fake `now`, and a subsequent read materializes
`ResourcePool.GetCurrentValue(fakeNow)` at whatever virtual instant the verifier has advanced to.
This is the same lever the soak-run doc would use for *speed*; here we use it for *reproducibility*.

**Honest cost:** line 92 is an *unconditional* `AddSingleton`, and the test host boots via the
no-argument `AlbaHost.For<Program>()` precisely to avoid the `WithWebHostBuilder` overload that
disposal-races `RunJasperFxCommands` in .NET 9 (testing.md:20). There is no post-build DI hook to
swap a singleton without that overload. So a real (small) engine change is required: give the clock
registration a seam. Two candidates, both driven by the env-var path AppFixture already proves
(`AppFixture.cs:20,29`):

- **`TryAddSingleton` seam** — change line 92 to `builder.Services.TryAddSingleton(TimeProvider.System)`,
  and register a `FakeTimeProvider` *before* it in a verifier-only composition. Still needs a service
  hook, so on its own it does not dodge the disposal race.
- **Config-gated registration (recommended)** — when a `Verifier:Enabled` (or env
  `Verifier__Enabled=true`) flag is set, register a `FakeTimeProvider` seeded from a fixed virtual
  epoch (`Verifier__Epoch`) instead of `TimeProvider.System`. Pure env-var configuration, so it
  rides the proven `AlbaHost.For<Program>()` path with no `WithWebHostBuilder`. The verifier then
  resolves the instance back out (`Host.Services.GetRequiredService<TimeProvider>()` cast to
  `FakeTimeProvider`) to advance it during the run.

### 2.2 Verifier ownership of the event schedule

This is the crux. Today, progression is emitted as durable messages: every mutation site and every
check handler calls `bus.ScheduleAsync(message, at)`. The full set of scheduled message types:

| Message | Scheduled by | Carried instant |
|---|---|---|
| `CompleteBuildingConstruction` | `BuildingEndpoints.Place` (`BuildingEndpoints.cs:61`) | `CompletesAt` |
| `CompleteShipConstruction` | `ShipConstructionScheduling` (`ShipConstructionScheduling.cs:16`) | `CompletesAt` |
| `CompleteBuildingDemolition` | `BuildingEndpoints` (`BuildingEndpoints.cs:169`) | `CompletesAt` |
| `CompleteFleetArrival` | `FleetEndpoints.Launch` (`FleetEndpoints.cs:90`), recall (`:274`) | `ArrivesAt` |
| `CheckStorageFull` | `StorageHaltScheduling.cs:18` | `PredictedAt` |
| `CheckPoolDepleted` | `StorageHaltScheduling.cs:40`, `CheckPoolDepletedHandler.cs:48` | `At` |
| `CheckInputStarved` | `StorageHaltScheduling.cs:46`, `CheckInputStarvedHandler.cs:60` | `At` |
| `CheckIngotStarved` | `StorageHaltScheduling.cs:52`, `CheckIngotStarvedHandler.cs:48` | `At` |

Each handler, on firing, may schedule **more** messages (the cascade re-scheduling chains — e.g.
`CompleteBuildingConstructionHandler` reschedules all cascade checks from the fresh post-commit
aggregate at `:62-68`; `CheckStorageFullHandler` self-reschedules the same resource at `:37-52`). The
deadline instants are computed by pure predictors on the aggregate: `PredictStorageDeadlines`,
`PredictDepletionDeadline`, `PredictBufferEmpty`, `PredictIngotBufferEmpty` (`StorageHaltScheduling.cs:35-52`).

The verifier must **become the scheduler**:

1. **Gate the live durability agent off** so it never dispatches. Candidates: run Wolverine so the
   scheduled-job poller never starts (e.g. `DurabilityMode.MediatorOnly`, or a verifier host that
   omits the agent), configured by the same `Verifier__Enabled` flag. (architecture.md:318 notes the
   poll interval is "Configurable via `opts.Durability.ScheduledJobPollingTime`" — the exact
   agent-off knob is an implementation detail to confirm; see [§8](#8-open-questions--decisions).)
2. **Maintain a single-threaded priority queue** of due events keyed by
   `(effectiveInstant, deterministicTieBreak)`. After each scripted command commits, sweep the
   newly-persisted scheduled messages into this queue and remove them from the outbox so nothing
   else can fire them. (Two ways to capture: read `wolverine_outgoing_envelopes` and its
   `execution_time`, sorting deterministically — lowest engine change, keeps the real bus; or
   substitute a capturing `IMessageBus` that records `(message, at)` straight into the queue —
   cleaner ownership, needs a second DI seam. Prefer the outbox sweep first.)
3. **Drain in canonical order.** Pop the earliest-due event, invoke its handler *directly* on a fresh
   `IDocumentSession` at the message's carried instant (generalizing `CompleteArrivalWithRetry`,
   `IntegrationApiExtensions.cs:369-387`, to every message type — a `switch` over the eight types
   above). The handler's own reschedules land back in the queue. Repeat until the queue is empty or
   the next due instant passes the script's target virtual time.

**Why this erases the `domain-model.md:129` order-independence concession.** domain-model.md:129
concedes that the `ResourcePool` floor makes an inversion "*inert and conservative*, not
order-independent": under the *live* runtime an out-of-order delivery under-credits a pool by
`(rate delta) × (inversion window)`, and ADR 0002 documents that bound
(`adr/0002-event-ordering-invariant.md:27-28,50`). But the inversion window exists **only** because
the live poller + `Quick`-mode retries can deliver a completion stamped `T` after a command already
committed at `W > T`. A single-threaded drainer that always pops the *earliest* carried instant and
applies it before any later one **never produces an inversion** — `CheckpointTime` advances
monotonically, `GetCurrentValue`'s `Math.Max(0m, …)` floor (`ResourcePool.cs:17`) never triggers, and
the under-credit residual is identically zero. **Determinism is a harness choice, not an engine
rewrite.** No domain code changes; the same `ResourcePool` that is merely "conservative" under the
live agent is *exact* under the drainer. (Corollary: because the verifier suppresses the very
concurrency the conservative floor exists to absorb, it also cannot detect a regression *in that
floor* — that is soak-run territory.)

### 2.3 Seeded world generation and deterministic IDs

World gen is the largest entropy source, and it poisons everything downstream: coordinates set travel
distances (`Travel/LinearTravelPlanner.cs:19-25`), which set arrival instants, which set the entire
event timeline. The full entropy ledger, verified:

| Site | Entropy | Effect |
|---|---|---|
| `WorldSeeder.cs:57` | `new Random()` (unseeded) | planet/system coordinates → travel times → whole timeline |
| `WorldSeeder.cs:61` | `Guid.NewGuid()` system id | non-reproducible ids, unstable ordering |
| `WorldSeeder.cs:69` | `Guid.NewGuid()` planet id | non-reproducible ids |
| `PlayerEndpoints.cs:50` | `Guid.NewGuid()` player id | non-reproducible id |
| `PlayerEndpoints.cs:118` | `Random.Shared.Next` over an **unordered** query (`:108-111`) | which planet becomes the homeworld |
| `PlayerEndpoints.cs:138` | `Guid.NewGuid()` ApiKey id | non-reproducible (auth doc) |
| `PlayerEndpoints.cs:213` | `RandomNumberGenerator.GetBytes(32)` | the raw API key |
| `Documents/ApiKey.cs:8` | `DateTimeOffset.UtcNow` | key `CreatedAt` |
| `ShipEndpoints.cs:45` | `Guid.NewGuid()` build id | ship/build id |
| `FleetEndpoints.cs:152` | `Guid.NewGuid()` fleet id | fleet id |
| `Domain/Fleet.cs:255` | `.ThenBy(s => s.Id)` on Guid | tie-break in `ConsumeColonyShip` — see below |

To make raw snapshots diff (variant A), each must be made a function of the seed:

- **Seeded RNG in `WorldSeeder`.** `WorldGenOptions` has no seed field today (`WorldGenOptions.cs:1-15`).
  Add `int Seed`, construct `new Random(opts.Seed)` at `WorldSeeder.cs:57`, and the coordinate stream
  (`NextCoordinate`, `:96-99`) becomes reproducible.
- **Deterministic ids from `(seed, index)`.** Replace `Guid.NewGuid()` at the seeding sites with a
  deterministic derivation — e.g. a name-based UUIDv5 from a fixed namespace over `"system:{s}"` /
  `"planet:{s}-{p}"`, or a hash of `(seed, kind, index)`. The command-path ids
  (`PlayerEndpoints.cs:50`, `ShipEndpoints.cs:45`, `FleetEndpoints.cs:152`) similarly derive from a
  per-run counter seeded by the script.
- **Deterministic homeworld selection.** The query at `PlayerEndpoints.cs:108-111` has no `OrderBy`,
  so its row order is not stable even before the random pick at `:118`. Add a deterministic
  `OrderBy(p => p.Id)` (or planet name) and replace `Random.Shared.Next` with a seeded pick, so the
  same registration in the same world always claims the same homeworld.

**The one random-id leak into game logic:** `Fleet.ConsumeColonyShip` tie-breaks
`.OrderBy(s => s.CompletedAt).ThenBy(s => s.Id)` (`Domain/Fleet.cs:252-256`). The Guid tie-break only
bites when two colony ships share an exact `CompletedAt`; ships are fungible, so only the *surviving
ship's id* differs — a harmless divergence under variant A (ids are seeded anyway) and one that
normalization (variant B) erases. No fix needed in the domain itself.

### 2.4 The API-key / `CreatedAt` entropy

`GenerateApiKey` uses a CSPRNG (`PlayerEndpoints.cs:211-215`) — deliberately non-derivable, and it
would be wrong to seed it. The registration *response* returns the raw key
(`RegisterPlayerResponse(playerId, rawKey, homeworldId)`, `PlayerEndpoints.cs:147`), so the verifier
simply **relays the returned key** for that player's subsequent authenticated calls — the key never
needs to be reproducible, only *used*. The stored `ApiKey` document (hashed key, random `Id`,
`CreatedAt = UtcNow`) is auth metadata, not game state, so the verifier **excludes the entire
`ApiKey` document from the snapshot**. If for some reason it must be included, normalize `CreatedAt`
to the virtual epoch and remap `Id`. Either way, exclude/normalize; never try to reproduce it.

## 3. Two viable variants

### Variant A — full determinism at the source

Fix every entropy site in [§2.3](#23-seeded-world-generation-and-deterministic-ids) so the raw
snapshot is already reproducible, then diff raw JSON directly.

- **Pros:** the snapshot *is* the golden file; no transform to trust or maintain. Ids are stable and
  human-traceable across runs, which makes a failing diff point straight at the entity. Reproducibility
  becomes a property of the engine, reusable by the soak run and by any future replay tooling.
- **Cons:** touches production code paths that have nothing to do with verification —
  `WorldSeeder`, `PlayerEndpoints`, `ShipEndpoints`, `FleetEndpoints`. Deriving ids deterministically
  is a real behaviour change to id allocation, with its own risk (collisions, uniqueness-index
  interactions on `Player.Name`/`ApiKey.HashedKey`, `Program.cs:32,38`). Every new entropy source a
  future feature introduces silently breaks the verifier until also seeded — an ongoing tax.

### Variant B — random ids, normalize before diffing

Leave the engine's entropy alone. Dump the raw snapshot, then run a **canonicalizer** that rewrites
it into a seed-independent form before comparison. Algorithm:

1. **Collect** all `SolarSystem`, `Planet`, `Player`, `Fleet` documents.
2. **Assign stable keys** from seed-independent content, not Guids:
   - system → its name `"System {s}"` (`WorldSeeder.cs:87`);
   - planet → its name `"Planet {s}-{p}"` (`WorldSeeder.cs:73`) (globally unique by construction);
   - player → registration name (the script controls these);
   - fleet → a composite of `(owner-ordinal, origin-planet-name, assembly order)`, since fleets have
     no natural name.
3. **Sort** every collection by its stable key; sort nested lists (buildings by slot index — already
   append-only and stable per `IntegrationApiExtensions.cs:446`; ships by `(CompletedAt, type)`;
   resource pools by resource type).
4. **Build a Guid→ordinal map** by walking entities in stable-key order, minting `"planet-0001"`,
   `"player-0002"`, etc. Rewrite *every* Guid field (ids and all cross-references — `OwnerId`,
   `SolarSystemId`, `DestinationPlanetId`, `PlanetIds`, ship `Id`, fleet `Id`) through the map. Any
   Guid with no mapping is a leak and should fail loudly.
5. **Normalize timestamps** relative to the fixed virtual epoch (all stamps become `epoch + Δseconds`),
   and **drop/round** genuinely uninteresting ones. Exclude the `ApiKey` document entirely
   ([§2.4](#24-the-api-key--createdat-entropy)).
6. Emit the canonicalized tree through the same canonical JSON writer as variant A ([§4](#4-snapshot--comparison-mechanics)).

- **Pros:** **zero production-code change.** The engine keeps `Guid.NewGuid()` and unseeded RNG;
  normalization lives entirely in the verifier/test assembly. New entropy that lands *inside* an id or
  timestamp field is absorbed by the existing remap/normalize rules rather than breaking the run.
  Robust to id-allocation refactors.
- **Cons:** the golden file is a *derived* artifact — a bug in the canonicalizer can hide or fabricate
  a diff, so the normalizer itself needs tests. Requires a genuinely seed-independent key for every
  entity (fleets are the awkward case). Coordinates are still random (unseeded `WorldSeeder.cs:57`),
  so **travel times still vary run to run** — meaning arrival instants, and every downstream
  timestamp, differ. Normalization can round or relativize timestamps but cannot make an
  *ore-value-at-arrival* match if the arrival instant itself moved. So variant B in practice **still
  needs the `WorldSeeder` RNG seeded** ([§2.3](#23-seeded-world-generation-and-deterministic-ids),
  first bullet) — it only lets you skip deterministic *ids*.

### Recommendation

**Seed the `WorldSeeder` RNG (unavoidable for both) and the homeworld pick, then use variant B
normalization for ids.** Rationale: the single change both variants require anyway is the world-gen
seed, because coordinates drive the timeline and no amount of post-hoc normalization recovers a moved
arrival instant. Given that seed is in place, deterministic *ids* buy little — normalization remaps
them for free and keeps the id-allocation code (and its uniqueness-index interactions) untouched. So
the least-invasive robust combination is: **seed coordinates + deterministic homeworld ordering
(two small, well-contained `WorldSeeder`/`PlayerEndpoints` changes), random ids left alone,
canonicalize before diff.** Reach for full variant-A id determinism only if a future need (e.g. a
replay/debugger that must reference entities by stable id across runs) justifies the extra production
surface.

## 4. Snapshot & comparison mechanics

**What to serialize.** Dump the three snapshot aggregates straight from Marten
(`session.Query<Planet>() / <Fleet>() / <Player>().ToListAsync()`) — these are the inline snapshots
Marten already maintains (`Program.cs:35-37`), so they *are* the read model. Include `SolarSystem`
docs for world-shape coverage. Exclude `ApiKey` and all Wolverine tables.

**Checkpoint tuples vs. materialized values — pick one and commit.** A resource pool stores
`(CheckpointValue, Rate, CheckpointTime, StorageCapacity)` and only *materializes* a current number
via `GetCurrentValue(now)` (`ResourcePool.cs:15-19`). The stored tuple is stable and time-independent;
a materialized value depends on read-time `now`. Two consistent options:

- **Serialize the stored tuples** (recommended). The snapshot is then independent of *when* the dump
  runs, and it captures more information (rate + checkpoint, not just the blended scalar) — a rate bug
  that happens to net out at one instant still shows in the tuple. This is the more sensitive golden.
- **Materialize at a single fixed virtual `now`** — the script's target end instant, resolved from
  the `FakeTimeProvider`. Simpler to eyeball, but read-time-sensitive and lower-resolution.

Do **not** mix them, and never materialize at wall-clock `now` — that alone would make every run
differ. Prefer the stored tuples; optionally also emit materialized-at-`T_end` values as a secondary,
clearly-labelled block for readability.

**Canonical JSON.** Serialize with fixed, explicit rules rather than Marten's default writer:
sorted property names; no insignificant whitespace variation (pick one — pretty with fixed indent for
readable diffs, or minified); UTF-8; `\n` line endings; invariant culture.

**Float/decimal handling.** Resource quantities are `decimal` (architecture.md:184), so serialize with
`decimal`'s round-trip (`"G29"` / `decimal.ToString()` invariant) — no float formatting, no
locale-dependent separators. The one `double` in the pipeline is the travel-distance `Math.Sqrt` in
`LinearTravelPlanner.cs:23` (cast back to `decimal`): `Math.Sqrt` is IEEE-754 correctly-rounded and
deterministic across .NET platforms, so it is safe *given identical inputs* — which the seeded
coordinates guarantee. Round or fixed-format any derived `double` before it enters the snapshot to be
safe. Coordinates themselves are `decimal` (`WorldGenOptions.CoordinateRange`, `PlanetCreated.X/Y/Z`).

**Readable diffs on failure.** Write the canonical snapshot to a file and compare against the
committed golden with a structural/line diff (the pretty-printed, sorted form makes a text diff land
on the exact changed field). On mismatch, emit the unified diff and the paths to both files; keep the
"actual" artifact so a genuine intended change can be re-blessed by copying it over the golden.
Re-blessing is a deliberate, reviewed step — the golden file is a checked-in fixture, and a diff to it
should be as scrutinized as a code change (cf. the MEMORY note that plan-embedded/JIT changes deserve
PR-level review).

## 5. Required engine changes, sized

Nothing in the **domain math** changes — `ResourcePool`, `Planet`, `Fleet`, the predictors, and every
handler stay byte-for-byte as shipped. The work splits cleanly:

### Local / cheap (production code, well-contained)

| Change | Site | Invasiveness | Risk |
|---|---|---|---|
| Add `int Seed` to `WorldGenOptions`; `new Random(opts.Seed)` | `WorldGenOptions.cs`, `WorldSeeder.cs:57` | ~2 lines | Very low — default seed keeps prod behaviour; env-configurable like the other world knobs (`AppFixture.cs:29`) |
| Deterministic homeworld ordering + seeded pick | `PlayerEndpoints.cs:108-118` | small | Low — add `OrderBy`; the guarded-claim retry (`:67-88`) is unaffected |
| Config-gated clock seam (`FakeTimeProvider` when `Verifier__Enabled`) | `Program.cs:92` | small | Low–medium — must not perturb the default `TimeProvider.System` path; env-var-only to dodge the `WithWebHostBuilder` race (testing.md:20) |
| *(Variant A only)* deterministic ids from `(seed, index)` | `WorldSeeder.cs:61,69`, `PlayerEndpoints.cs:50`, `ShipEndpoints.cs:45`, `FleetEndpoints.cs:152` | moderate | Medium — id-allocation change; watch `Player.Name`/`ApiKey.HashedKey` unique indexes (`Program.cs:32,38`) |

### Harness-level (verifier/test assembly, the real work)

| Change | Invasiveness | Risk |
|---|---|---|
| Gate the durability agent off in verifier mode | small config, **but** the exact Wolverine knob needs confirming | Medium — if the agent still fires, it races the drainer and reintroduces the very non-determinism we removed |
| Canonical-order event drainer: priority queue keyed by `(instant, tie-break)`, `switch` over all 8 message types invoking each handler directly, sweeping reschedules | **this is the bulk of the effort** — generalizes the one-shot `CompleteArrivalWithRetry`/`LaunchAndArriveInstantly` (`IntegrationApiExtensions.cs:345-387`) to every type + cascade chains | Medium — must reproduce Wolverine's dispatch faithfully (each handler needs a session and, for the check/completion handlers, a bus that captures reschedules; `CompleteFleetArrivalHandler.Handle` takes only a session and does *not* reschedule) |
| Snapshot dumper + canonical JSON writer | small–moderate | Low |
| *(Variant B)* normalizer/canonicalizer | moderate | Medium — derived golden; needs its own tests |

The honest summary: the clock seam and world-gen seed are a couple of hours; the **event drainer is
the project**, because it must enumerate every scheduled message type and every reschedule path and
prove it drains them in the same logical order Wolverine would, minus the concurrency. It is a faithful
re-implementation of the scheduler's *sequencing*, which is exactly why it can be exact where the live
agent is only conservative.

## 6. When it is worth it

- **High-signal regression gate on cascade math.** Once the golden is blessed, any change to
  checkpointing, rate rebasing, even-split distribution, or cascade resolution order that alters *any*
  value anywhere fails the diff — far more sensitive than the invariant assertions the soak run makes.
- **Fast and hermetic.** No wall-clock waits (the drainer jumps straight to each due instant), so a
  full multi-hour game-time scenario runs in milliseconds — cheap enough for every CI run, unlike a
  soak.
- **Not worth it if** the world-gen seed can't be landed (coordinates stay random → timeline varies →
  no exact golden is possible), or if the team only cares about "does it stay internally consistent
  under real concurrency," which is the soak run's job.

## 7. Complementarity: what each catches

| Failure class | Golden-diff verifier | Live soak run |
|---|---|---|
| Off-by-one in a checkpoint / rate / distribution calc | **Caught** (exact value diff) | Missed unless it breaks an invariant |
| Changed cascade *resolution order* landing on a different valid state | **Caught** | Usually missed |
| Unintended change to a tie-break or id-derivation | **Caught** (variant A) / normalized-away (variant B) | Missed |
| Out-of-order delivery under-crediting a pool (`domain-model.md:129`, ADR 0002) | **Cannot catch** — the drainer removes the inversion window by design | **Caught** — this is its whole point |
| `Quick`-mode `ConcurrencyException` retry / dead-letter behaviour (`Program.cs:59-65`) | **Cannot catch** — no real concurrency | **Caught** |
| Durability-agent poll-lag / staleness bounds | **Cannot catch** | **Caught** |
| Real HTTP/auth/serialization contract regressions | Partial (in-process, real endpoints) | **Caught** (real network client) |

The two are duals: the golden-diff verifier freezes the scheduler to test the math exactly; the soak
run freezes exactness to test the scheduler. Run both.

## 8. Open questions / decisions for the team

1. **Add a real `Seed` to `WorldGenOptions`?** This is the one change *both* variants need. It is
   low-risk (default preserves prod behaviour, env-configurable like `SolarSystemCount`), and it also
   benefits the soak run and any future replay tooling. Recommend yes.
2. **How exactly to gate the durability agent** for the verifier/test host so it never dispatches
   while the drainer owns the schedule (`DurabilityMode.MediatorOnly`? a host that omits the
   scheduled-job poller? a very large `ScheduledJobPollingTime` is *not* sufficient — the agent still
   eventually fires). Needs a Wolverine-API spike. This is the single biggest correctness risk: a live
   agent racing the drainer reintroduces exactly the non-determinism we set out to remove.
3. **Clock seam shape** — config-gated `FakeTimeProvider` (recommended, env-var-only) vs.
   `TryAddSingleton` + a service hook. Constrained by the .NET 9 `WithWebHostBuilder` disposal race
   (testing.md:20).
4. **Variant A (deterministic ids) vs. Variant B (normalize)** — recommendation is B for ids atop a
   seeded world; revisit A only if stable cross-run ids become independently valuable.
5. **Stored checkpoint tuples vs. materialized-at-`T_end`** in the snapshot — recommendation is the
   tuples (time-independent, higher resolution), optionally with a labelled materialized block for
   readability.
6. **Golden re-bless workflow** — who reviews a golden update, and how is an intended change
   distinguished from a regression? Treat the golden file as a reviewed fixture, not an auto-updated
   artifact.
7. **Fleet stable key for variant B** — fleets have no natural name; agree a composite
   (`owner-ordinal + origin-planet-name + assembly order`) before writing the normalizer.
