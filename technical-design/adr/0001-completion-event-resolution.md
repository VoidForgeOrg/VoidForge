# ADR 0001 — Resolving deterministic future events (build/ship completion, depletion)

- **Status:** Accepted — 2026-06-20 (**Option B — durable scheduled messages**)
- **Date:** 2026-06-20
- **Deciders:** Tomas Grbalik
- **Related:** issues [#26](https://github.com/VoidForgeOrg/VoidForge/issues/26), [#27](https://github.com/VoidForgeOrg/VoidForge/issues/27); `technical-design/architecture.md` §4–6; `game-design/engine.md`

## Context

Phase 3 introduces the first **deterministic future events**: building completion and ship completion. Phase 5 adds resource depletion, storage-full, and the halting cascades. All share a shape: *a state transition happens at a known future time `T`.*

Lazy calculation (`ResourcePool`) already handles the **continuous** values between events — current ore/ingots are computed on read from `checkpoint + rate × elapsed`. The open question is narrower: **how is the discrete transition at `T` triggered and recorded?**

When the decision was parked, it was framed as "lazy resolution vs. durable scheduled messages, the latter requiring us to leave `DurabilityMode.Solo`." Two corrections surfaced while writing this ADR:

1. **`DurabilityMode.Solo` is not in-memory.** With `.IntegrateWithWolverine()` and `AutoApplyTransactions()` — both already present in `Program.cs` (lines 34, 38) — Wolverine persists scheduled messages to Postgres (`wolverine_outgoing_envelopes`) **in the same transaction** as the triggering event, and the single-node durability agent delivers them, surviving restarts. `Solo` only means "assume this is the sole node; skip multi-node coordination." **Adopting scheduled messages requires no durability-mode change and no new infrastructure.**
2. **`architecture.md` §4–6 already commits to durable scheduled messaging** as the engine, with concrete message types (`BuildingCompleted`, `ShipCompleted`, `ResourceDepleted`, `StorageFull`) and a "schedule optimistically, validate on arrival" pattern. This ADR exists to make that choice explicit for Phase 3 and to record the trade-offs against the simpler alternative.

## Decision drivers

- **Cascade correctness when nobody is observing** — depletion → halt → energy rebalance → rate changes must resolve consistently even on a planet no client is polling.
- **Truthfulness of the event stream / snapshot** at any instant, not just "next time someone reads."
- **Async projections & push** — the `Leaderboard` async projection (architecture.md §3) and the post-MVP SSE/SignalR push path (§2) *react to appended domain events*. They need the transition events to actually exist at `T`.
- **Implementation cost now vs. reuse in Phase 4/5.**
- **Testability / determinism.**
- **Alignment with the documented architecture.**

## Considered options

### Option A — Lazy-only resolution (resolve breakpoints on read/command)

Breakpoint times are *derived* from aggregate state (e.g. `UnderConstruction` since `X`, cost `C`, rate `r` → completes at `X + C/r`). On any read or command, fast-forward the aggregate: process due breakpoints in timestamp order, checkpoint at each, apply the transition/cascade, then continue to `now`. **No scheduled messages.**

**Pros**
- No Wolverine handlers/messages to build in Phase 3 — smallest immediate code surface.
- Fully deterministic and idempotent: reading at `T` always yields the same state; nothing can fire late or twice.
- Trivial restart story — nothing is pending; the schedule is a pure function of state.
- Test-friendly with an injected clock; no scheduler to advance.
- Zero work for planets nobody looks at.

**Cons**
- The **read path must contain the full cascade engine** and breakpoint derivation — reads stop being simple projections, and the cascade logic risks being duplicated across read and write paths.
- The event stream and snapshot **lag reality**: `BuildingCompleted` / `ResourceDepleted` are not appended until a *command* next touches the stream. Until then, history is incomplete.
- Consequently, **async projections (Leaderboard) and push notifications never fire** for transitions on a quiescent planet — scoring and notifications silently stall until a command lands. This conflicts with architecture.md §3 (async leaderboard) and §2 (push path).
- A pure `GET` must either stay read-only (history keeps lagging) or **write on read** (`GET` causes event appends) — an unusual, surprising design. Neither is clean.
- **Cross-aggregate transitions can't be modelled this way.** Fleet arrival (Phase 4) deposits ships on *another* planet's roster; "resolve on read of self" cannot express that — it inherently needs a push/scheduled trigger. So Phase 4 reintroduces scheduling regardless.
- Diverges from the documented architecture; future contributors must learn a bespoke resolver.

### Option B — Durable scheduled messages (adopt architecture.md §4)

When construction starts, compute the completion time and `Schedule` a `BuildingCompleted` / `ShipCompleted` message at `T`, persisted in the same transaction (transactional outbox). At `T` (± the polling interval, default 5 s), the handler loads the planet, checkpoints **at the scheduled time**, appends the real transition events, recomputes energy/rates, and reschedules downstream deadlines. Invalidated messages are handled by **"schedule optimistically, validate on arrival"** — the handler no-ops if state shows the build was cancelled or superseded.

**Pros**
- Event stream and snapshot are **truthful within ~5 s** without anyone reading — history is complete.
- **Async projections (Leaderboard) and the post-MVP push path work naturally** — they observe the appended events.
- Idiomatic Critter Stack; **reuses infrastructure already wired** (`IntegrateWithWolverine`, outbox, Solo durability agent). No durability-mode change.
- **Single-handler atomic cascade resolution** (architecture.md §6): triggering event + all downstream events + new scheduled deadlines commit together, or roll back together.
- The **same machinery Phase 4 fleet sagas and Phase 5 cascades require** — built once, reused.
- Read path stays a pure lazy-calc projection — simpler than Option A's read path.

**Cons**
- More moving parts in Phase 3: message types, handlers, scheduling-on-start, reschedule/validate-on-arrival on rate changes.
- **Reschedule churn** — completion/depletion times shift when rates change (halt/resume, energy rebalance). Mitigated by *not* cancelling: let stale messages fire and validate/no-op on arrival (the documented pattern). Requires a robust idempotency/version guard in every handler.
- **Up to ~5 s latency** between the true `T` and the event being recorded. Negligible for a strategy game, **and values stay exact** as long as the handler checkpoints at the message's *intended* time, not `UtcNow` at delivery. (Implementation rule, called out below.)
- Tests that exercise delivery need to advance/trigger the scheduler. Mitigated by keeping domain logic in pure aggregate methods (below) and unit-testing those directly; reserve scheduler-driven tests for a couple of integration cases.

### Why there is no separate "hybrid" option

architecture.md's design **already is** the hybrid: lazy calc for continuous read values **plus** scheduled messages for discrete transitions. Option B *includes* the lazy read path. Option A is the deviation that tries to drop scheduled messages entirely.

## Decision

**Accepted Option B on 2026-06-20.** Phase 3 (#26, #27) and the later cascade work (Phase 5) resolve deterministic future events via durable Wolverine scheduled messages, per the recommendation below.

## Recommendation

**Adopt Option B for Phase 3**, consistent with architecture.md §4–6.

- The durable-scheduling infrastructure is **already wired** — the incremental cost is handlers + messages, not infrastructure or a durability change.
- It keeps the event stream/snapshot **truthful**, which the async Leaderboard projection and the post-MVP push path depend on.
- Phase 4 (fleet sagas) and Phase 5 (depletion/halting cascades) need this machinery **regardless**; a lazy-only resolver now is throwaway work that, in the interim, breaks async projections.
- Option A's apparent simplicity is mostly **relocation** of complexity into the read path plus a correctness gap (history lag / stalled projections) — not a true reduction.

**Keep Option A's best property — deterministic, clock-injected tests — by structure, not by mechanism:** put completion and cascade logic in **pure aggregate methods** (`Planet.CompleteBuilding(slotIndex, at)`, `Planet.RecalculateEnergy()`, `Planet.CalculateDepletionDeadlines(now)`) that the scheduled-message handler merely *calls*. The handler is a thin durable trigger; the domain logic stays pure and unit-testable with an explicit timestamp. This yields B's truthfulness with A's testability.

## Consequences

- Phase 3 introduces the first Wolverine **message handlers** (`BuildingCompleted`, `ShipCompleted`) and the **schedule-on-start / validate-on-arrival** pattern — the template for Phase 4/5.
- Introduce an injectable clock (**`TimeProvider`**) in place of direct `DateTimeOffset.UtcNow`, so handlers/tests are deterministic and handlers can checkpoint at the *scheduled* time. Small refactor; touches endpoints too.
- Handlers must be **idempotent and stale-aware** (no-op on cancelled/superseded builds). Add a small guard helper.
- Keep `DurabilityMode.Solo` for MVP (single node). Scheduled work already persists; multi-node is a later `Balanced` switch (architecture.md §7).
- Correct the Phase-3 issue/epic/plan wording that implied `Solo` is non-durable or that scheduling needs a durability change.
- No change to architecture.md — this ADR **affirms** §4–6.

## Note / correction

This ADR reverses the lean toward "lazy-only" expressed when the decision was parked. That lean rested on a wrong premise — that `DurabilityMode.Solo` is in-memory and would lose scheduled messages on restart. It is not, and does not (with `IntegrateWithWolverine`, envelopes persist to Postgres). With that corrected, and given architecture.md already commits to scheduled messaging, **Option B is both the consistent and the lower-total-cost choice.**
