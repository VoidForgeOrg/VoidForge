# ADR 0002 — Event-ordering invariant: inversion bound and the conservative guarantee

- **Status:** Accepted — 2026-08-15 (**MVP-resolves #44 — structural guard + this record; exact correctness stays post-MVP**)
- **Date:** 2026-08-15
- **Deciders:** Tomas Grbalik
- **Related:** issues [#44](https://github.com/VoidForgeOrg/VoidForge/issues/44), [#47](https://github.com/VoidForgeOrg/VoidForge/issues/47), [#39](https://github.com/VoidForgeOrg/VoidForge/issues/39); `technical-design/adr/0001-completion-event-resolution.md`; `src/Voidforge.Api/Domain/ResourcePool.cs`; `src/Voidforge.Tests/Planets/PlanetEventOrderingTests.cs`

## Context

`ResourcePool` uses lazy calculation (ADR 0001): a pool's current value is `checkpoint + rate × elapsed`, and each state-changing event checkpoints the pool at its own `at` timestamp before mutating rates or stored value. This is only sound if the `at` timestamps applied to a single Planet stream are **non-decreasing**. They are not guaranteed to be.

This ADR is the durable record of the #44 survey: it states *what protects us today*, *how far a timestamp can invert*, *why the obvious clamp is rejected*, and *what residual remains for post-MVP*. It moves that analysis out of the #44 GitHub comment and into the repo.

## The shipped protection (#47)

Two lines in `ResourcePool` already make an out-of-order event **inert and conservative** — the load-bearing fix landed in #47:

- **`GetCurrentValue` floors elapsed at zero:** `var elapsed = Math.Max(0m, (decimal)(now - CheckpointTime).TotalSeconds);`. A read stamped *before* the checkpoint yields `elapsed == 0`, not a negative one. Without the floor a negative `elapsed` silently drains an accruing pool, and a negative `elapsed` multiplied by a negative `rate` fabricates resources outright.
- **`Checkpoint` never moves `CheckpointTime` backwards:** `CheckpointTime = now > CheckpointTime ? now : CheckpointTime`. A backwards checkpoint freezes time and locks in the value accrued up to the current head; it is a no-op on time, not a rewind. Letting it regress would re-accrue the inverted interval on every subsequent read, compounding the error instead of absorbing it once.

**Consequence:** an inverted event can only ever **under-credit** — the inverted window accrues at the older (pre-transition) rate. It can never over-credit, drive a value negative, exceed `StorageCapacity`, or corrupt the stream. `Math.Clamp` in `GetCurrentValue` bounds the stored value into `[0, StorageCapacity]` as a second belt.

## Why timestamps invert along a Planet stream

An event's `at` is not wall-clock-at-append; it is the *intended effective instant*. Two sources produce a backwards `at`:

1. **Command / completion races — bounded ~7 s.** A durable completion (ADR 0001) is stamped with its scheduled `CompletesAt = T` but commits after a poll delay. If a player command already committed at wall-clock `W > T`, the completion lands behind it. The window is bounded by ADR 0001's ~5 s durable-message poll lag plus the #39 `ConcurrencyException` retry backoff (~1.9 s) — on the order of **~7 s**.
2. **Scheduled fleet arrivals — bounded by outage / travel, NOT 7 s.** `CompleteFleetArrivalHandler` stamps `CargoDeliveredToStorage` (Transport) and `PlanetColonized` (Colonize, via `Planet.Claim`) with `message.ArrivesAt`. After a host-down window or a dead-letter replay, that message is delivered long after `ArrivesAt` — which can therefore be **arbitrarily far in the past**. The inversion window here is bounded by the outage / travel duration, not by the ~7 s race window.

## Why we do not clamp scheduled-completion timestamps

The intuitive fix — clamp each event's `at` up to the current head — is rejected because those `at` values **double as validate-on-arrival match tokens**:

- `Fleet.Arrive` no-ops unless `ArrivesAt == at`.
- `Planet.CompleteBuilding` no-ops unless `slot.CompletesAt == at`.
- `Planet.CompleteShipBuild` no-ops unless `build.CompletesAt == at`.

Clamping the token breaks the equality guard, the completion never fires, and the aggregate is stuck permanently in `UnderConstruction` / `InTransit`. PR #47 analysed the `matchAt` / `effectiveAt` split that would be needed to clamp the *effective* instant while preserving the *match* token, and found the result **numerically identical to the shipped floor** for the pools involved — so the split buys nothing over what already ships.

**Wall-clock player-command appends provably cannot invert.** Every event's `at` is ≤ wall-clock at application time (the scheduler never fires before its scheduled instant), so a planet's `CheckpointTime` is never ahead of wall-clock; a new command at `now` always has `now ≥ CheckpointTime`. A `max(head, now)` clamp on those sites is a no-op — no clamp is needed there.

## What #44 delivers

- **`Apply(PlanetColonized)` routed through the guarded checkpoint.** It previously used a raw `with` that overwrote `CheckpointTime` outright — guarded by convention, not by the type. It now calls `IronOre.Checkpoint(@event.ColonizedAt) with { CheckpointValue = @event.IronOreStored }` (likewise ingot), so the non-regressing invariant is **type-enforced**. Numerically identical on the zero-rate/zero-value claim-time pool this event always lands on.
- **This ADR** — the durable record of the bound and the conservative guarantee.
- **Regression tripwires** in `PlanetEventOrderingTests` — the reverse-order completion tests plus a far-past-arrival delivery test that pins the conservative outcome (non-regressing checkpoint, values in `[0, capacity]`, under-accept never corrupt).

## Residual under-credit and the post-MVP path

The conservative guarantee leaves an **under-credit residual**: the inverted window accrues at the older rate. `ReverseOrderShortfallIsTheInvertedWindowAtThePreCompletionRate` pins this exactly — a 30 s inversion of a `5/s → 15/s` rate change leaves 300 units uncredited, a constant offset that does not widen. Eliminating the residual requires **rewind-and-reapply** — retroactively re-deriving the pool from the rewound timestamp under the post-transition rates — which is explicitly out of MVP scope (D14) and tracked as **the post-MVP rewind-and-reapply issue (#81)**.

That issue also carries a candidate refinement surfaced by the survey: a deterministic `max(planetHead, at)` clamp on the **planet-side arrival stamps only** (the `CargoDeliveredToStorage` / `PlanetColonized` effective instant — never the fleet `ArrivesAt` match token, which must stay exact). It is deferred because it subtly changes `AcceptCargoDelivery` headroom timing: headroom would be computed at the clamped-later instant rather than the true arrival instant, a behaviour change for marginal MVP benefit.

## Decision

Accept the shipped `ResourcePool` floor + non-regressing `Checkpoint` as the MVP invariant, make it type-enforced at the one convention-bypassing site (`Apply(PlanetColonized)`), and record the bound and the conservative guarantee here. Exact ordering correctness (rewind-and-reapply) stays post-MVP.

## Consequences

- The event-ordering invariant is now enforced by `ResourcePool` (type) plus this record, not by per-call-site convention. New pool-mutating `Apply` methods must checkpoint through `ResourcePool.Checkpoint`, never a raw `with` on `CheckpointTime`.
- Scheduled-completion / arrival `at` timestamps remain **exact** (unclamped) because they are match tokens; the floor in `ResourcePool` absorbs any inversion they cause.
- A known under-credit residual persists on inverted windows; it is bounded, conservative, and pinned by regression tests. Full correctness is deferred to the post-MVP rewind-and-reapply issue (#81).
- No change to ADR 0001 — this ADR builds on its durable-scheduling model.
