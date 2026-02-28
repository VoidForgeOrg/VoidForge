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
- Ship construction **consumes Iron Ingots continuously** at a steady rate over the build duration (same model as building construction).
- A single Shipyard can build up to **3 ships in parallel**. Each ship has its own build duration and ingot consumption rate.
- Ships are **built over time** — each ship type has a build duration and total ingot cost.
- **Halts** when Iron Ingots are unavailable. Resumes when supply is restored.
- The Shipyard itself consumes energy while operational, but individual ships under construction **do not add extra energy draw**.

### Generator (Energy)
- Produces **energy (MW)** for the planet.
- **Passive generation** — does not consume resources.
- Powers all other buildings on the planet.

> **Post-MVP Note:** Fuel-consuming generators (e.g., burning Ore for higher output) could be introduced as an advanced option later.

## Construction

- Buildings are **constructed over time** — each building type has a build duration and a total Iron Ingot cost.
- Construction **consumes Iron Ingots continuously** from planetary storage at a steady rate over the build duration (total cost / build time = consumption rate).
- A building under construction **occupies a slot** and consumes ingots, but **does not consume energy**. Energy draw begins only once the building is completed and operational.
- **If Iron Ingots run out mid-construction:** The build **halts** until more ingots are available. Build progress resumes when supply is restored.
- **Multiple buildings can be constructed in parallel** on the same planet (limited by available slots and ingot supply). Each construction consumes ingots independently.
- Each planet manages its own construction independently.

> **Design Intent:** Continuous consumption means players don't need all resources upfront. They can start ambitious builds early and feed them over time, including via transport fleets from other planets. It also integrates naturally with the lazy calculation engine — construction is just another rate-based process.

## Demolition

- Buildings can be **demolished** to free up a building slot.
- Demolition **takes time** (duration TBD per building type).
- **No resources are refunded** — demolition yields nothing back.
- The building stops functioning once demolition begins.

## Energy Dependency

- All buildings (except Generators) consume energy to operate.
- If a planet's total energy consumption exceeds generation, **all buildings on that planet suffer reduced productivity** proportionally.
- Players must balance their building composition to maintain adequate energy supply.
