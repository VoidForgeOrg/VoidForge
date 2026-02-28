# Game Engine: Event-Driven & Lazy Calculation

## Overview

Voidforge does **not** use a traditional tick/heartbeat engine. Instead, the game state is resolved through **lazy calculation** and **scheduled events**. Nothing "runs" in the background constantly — state is computed on demand and updated at discrete event points.

## Lazy Calculation

When any game state is queried (by a player, the API, or an internal process), the engine computes the **current value** based on:

- The last known checkpoint value
- The production/consumption rate at that checkpoint
- The elapsed time since the checkpoint

**Example:** A Drill mines 2t ore/hr. Player checks ore at hour 3 since last checkpoint. Engine calculates: `last_known_ore + (2t/hr * 3hr)` = current ore.

No processing happens between queries — the state is always "current" when accessed.

## Scheduled Events

Many game events are **deterministic** — their timing is known the moment the triggering action occurs. These are placed on an event queue and resolved when their time comes.

### Examples of Scheduled Events

- **Resource depletion** — Drill mines 2t/hr, pool has 1,000t → depletion event scheduled at 500 hours.
- **Fleet arrival** — Fleet departs, distance and speed are known → arrival event scheduled at exact time.
- **Construction completion** — Building starts, build time known → completion event scheduled.
- **Ship completion** — Ship queued at Shipyard → completion event scheduled.

## Checkpoints

When an event fires or a state-changing action occurs, the engine **checkpoints** the affected entities — saves their current calculated state and resets the baseline for future lazy calculations.

### Examples of Checkpoint Triggers

- A building comes online (changes energy balance, production rates)
- A resource pool is depleted (extraction stops)
- A fleet arrives (resources unloaded into storage)
- A player issues a new command (start building, send fleet)

## State Integrity

The engine remains the **ultimate arbiter**. Every player action submitted via the API is validated against the current calculated state before being accepted. The event queue and lazy calculation ensure a consistent, deterministic source of truth.

> **Post-MVP Note:** Cascading events (e.g., ore depletion causing refinery stall, energy overload changing production rates) and complex player-vs-player interactions will require careful handling of dependency chains. These details will be resolved during technical refinement.
