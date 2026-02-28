# Planets

## Overview

Planets are the core territorial entity in Voidforge. They exist in space at specific coordinates and can be either **colonized** (owned by a player) or **uncolonized** (unclaimed).

All planets — owned or not — share the same set of properties. The difference is simply whether a player has claimed control.

## Ownership

- A planet is owned by **exactly one player**, or by no one.
- Ownership is gained through colonization (sending a Colony Ship to an uncolonized, scouted planet).
- No shared/alliance ownership in MVP scope.

> **MVP Note:** In MVP, there is **no way to lose a planet**. Ownership is permanent once established. Conquest mechanics are planned for post-MVP.

## Spatial Model

- Planets exist within **solar systems**.
- Solar systems (and planets within them) are positioned using a **3D coordinate system** in space.
- Distance between locations is meaningful — it will factor into travel time and fleet logistics (see fleet/travel docs).

## Solar Systems

Solar systems are groupings of planets. They may share characteristics (e.g., star type) that affect the planets within them.

> **MVP Note:** Solar systems exist as structural groupings but have **no unique mechanics** in MVP scope. System-level traits and effects are planned for future iterations.

## Starting Conditions (New Player)

Every new player begins with a **Homeworld** — a pre-colonized planet that serves as the seed of their empire.

The Homeworld comes with:
- **1 Drill** (already built and operational)
- **1 Refinery** (already built and operational)
- **1 Generator** (already built and operational)
- **Starting resources** in planetary storage (specific amounts TBD)

> **Design Intent:** The starting setup gives players a functional economy from the start — ore is being mined, refined into ingots, and the player can immediately begin building. Their first decisions are what to construct next: more industry, a Shipyard to expand, etc. The starting Generator must produce enough energy for the initial buildings **plus headroom** for early growth.

**New colonies** (non-Homeworld) start **empty** — no buildings, no energy. Players must transport Iron Ingots to the new colony and build infrastructure from scratch. Colonizing is a serious logistical investment: a Colony Ship alone claims the planet, but a Cargo Vessel with building materials should accompany it.

> **Design Intent:** This makes expansion a strategic commitment, not just a land grab. Players must plan supply chains to support new colonies.

## Visibility & Discovery

- The **star map** is visible to all players — players can see solar system locations and know that planets exist within them.
- However, **planet details** (resource pools, building slots, storage capacity) are **hidden** until scouted.
- Scouting is done by sending **any fleet** to a target planet — all ships can perform scouting on arrival. **Scout Vessels** are simply cheaper and faster, making them ideal for exploration (see [fleets.md](fleets.md)).
- Once scouted, a planet becomes **permanently discovered** (always visible on the map). However, the intel is a **snapshot** — it can become outdated as the planet changes. Re-scouting refreshes the information.

## Planet Properties

All planets (colonized and uncolonized) share the same property set:

- **Resource Pools** — Finite deposits per resource type (MVP: Iron Ore pool, e.g., 50,000 units). Depletable. (See [resources.md](resources.md))
- **Building Slots** — Fixed number of slots available for construction. (See [buildings.md](buildings.md))
- **Planetary Storage** — Fixed capacity for holding extracted/refined resources. (See [resources.md](resources.md))
- **Energy Grid** — Net energy balance from Generators vs. building consumption. (See [resources.md](resources.md))

> **Post-MVP:** Planet types/traits (e.g., Volcanic, Frozen) that modify resource pools, slot counts, or building efficiency are planned for future iterations.
