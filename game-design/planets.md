# Planets

## Overview

Planets are the core territorial entity in Voidforge. They exist in space at specific coordinates and can be either **colonized** (owned by a player) or **uncolonized** (unclaimed).

All planets — owned or not — share the same set of properties. The difference is simply whether a player has claimed control.

## Ownership

- A planet is owned by **exactly one player**, or by no one.
- Ownership is gained through colonization (details TBD).
- Ownership can be lost (e.g., through conquest — details TBD).
- No shared/alliance ownership in MVP scope.

## Spatial Model

- Planets exist within **solar systems**.
- Solar systems (and planets within them) are positioned using a **coordinate system** in space.
- Distance between locations is meaningful — it will factor into travel time and fleet logistics (see fleet/travel docs).

## Solar Systems

Solar systems are groupings of planets. They may share characteristics (e.g., star type) that affect the planets within them.

> **MVP Note:** Solar systems exist as structural groupings but have **no unique mechanics** in MVP scope. System-level traits and effects are planned for future iterations.

## Visibility & Discovery

- **Colonized planets** belonging to a player are fully visible to that player.
- **Uncolonized planets** are **not visible** to players until scouted/discovered.
- Scouting mechanics will reveal planet properties (details TBD in fleet/espionage docs).

## Planet Properties

All planets (colonized and uncolonized) share the same property set:

- **Resource Pools** — Finite, per-resource-type deposits (e.g., Ore: 50,000). Depletable. (See [resources.md](resources.md))
- **Building Slots** — Fixed number of slots available for construction. (See [buildings.md](buildings.md))
- **Planetary Storage** — Fixed capacity for holding extracted/refined resources. (See [resources.md](resources.md))
- **Energy Grid** — Net energy balance from Generators vs. building consumption. (See [resources.md](resources.md))

> **Post-MVP:** Planet types/traits (e.g., Volcanic, Frozen) that modify resource pools, slot counts, or building efficiency are planned for future iterations.
