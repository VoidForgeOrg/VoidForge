# Buildings

## Overview

Buildings are constructed on planets in **building slots**. They form the backbone of a player's economy — extracting resources, refining materials, producing ships, and generating energy.

## Building Slots

- Each planet has a **fixed number of building slots**.
- All slots are **identical** — any building can be placed in any slot.
- Slots are a hard constraint: players must make strategic choices about what to build on each planet.

> **Post-MVP Note:** Slot types and specialization (e.g., dedicated industrial vs. research slots) are planned for future iterations.

## Building Types (MVP)

### Drill (Extraction)
- Extracts **Iron Ore** from a planet's resource pool at a continuous rate.
- Ore is deposited into planetary storage.
- **Halts** when the resource pool is depleted or storage is full (consumes only 5% energy when halted).
- Consumes energy.

### Refinery (Processing)
- Converts **Iron Ore → Iron Ingots** at a continuous rate.
- Pulls Iron Ore from planetary storage, outputs Iron Ingots to planetary storage.
- **Halts** when input (Iron Ore) is unavailable or output storage is full (consumes only 5% energy when halted).
- Consumes energy.

### Shipyard (Production)
- Constructs **ships** from Iron Ingots.
- Pulls materials from planetary storage at the start of each build.
- Ships are **built over time** (queued production — each ship has a build duration).
- **Halts** when required materials are unavailable (consumes only 5% energy when halted).
- Consumes energy.

### Generator (Energy)
- Produces **energy (MW)** for the planet.
- **Passive generation** — does not consume resources.
- Powers all other buildings on the planet.

> **Post-MVP Note:** Fuel-consuming generators (e.g., burning Ore for higher output) could be introduced as an advanced option later.

## Construction

- Buildings are **constructed over time** — each building type has a build duration.
- All buildings cost **Iron Ingots** to construct (specific amounts TBD per building type).
- Ingots are consumed from planetary storage at the **start** of construction.
- Each planet manages its own construction queue independently.

## Demolition

- Buildings can be **demolished** to free up a building slot.
- Demolition **takes time** (duration TBD per building type).
- **No resources are refunded** — demolition yields nothing back.
- The building stops functioning once demolition begins.

## Energy Dependency

- All buildings (except Generators) consume energy to operate.
- If a planet's total energy consumption exceeds generation, **all buildings on that planet suffer reduced productivity** proportionally.
- Players must balance their building composition to maintain adequate energy supply.
