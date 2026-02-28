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
- Extracts **Ore** from a planet's resource pool.
- Produces raw Ore each tick, deposited into planetary storage.
- Stops producing when the resource pool is depleted or storage is full.
- Consumes energy.

### Refinery (Processing)
- Converts **Ore → Ingots**.
- Pulls Ore from planetary storage, outputs Ingots to planetary storage.
- Consumes energy.

### Shipyard (Production)
- Constructs **ships** from Ingots (and potentially other refined materials).
- Pulls materials from planetary storage.
- Ships are built over time (queued production).
- Consumes energy.

### Generator (Energy)
- Produces **energy (MW)** for the planet.
- **Passive generation** — does not consume resources.
- Powers all other buildings on the planet.

> **Post-MVP Note:** Fuel-consuming generators (e.g., burning Ore for higher output) could be introduced as an advanced option later.

## Construction

- Buildings are **constructed over time** (construction queue per planet).
- Construction costs resources (Ingots and/or Ore — specific costs TBD).
- Each planet manages its own construction queue independently.

## Energy Dependency

- All buildings (except Generators) consume energy to operate.
- If a planet's total energy consumption exceeds generation, **all buildings on that planet suffer reduced productivity** proportionally.
- Players must balance their building composition to maintain adequate energy supply.
