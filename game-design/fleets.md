# Fleets & Space Travel

## Overview

Fleets are the primary way players project power and move resources across space. A fleet is an entity composed of individual ships, launched from a planet and traveling linearly through space to a destination.

## Fleets

- A fleet is a **group of one or more ships** sent from a planet.
- Fleets can contain a **mix of ship types**.
- A fleet travels as a unit — **the slowest ship dictates the fleet's speed**.
- Fleets travel **linearly** (point-to-point) between planets based on coordinate distance.

> **Post-MVP Note:** Advanced travel mechanics (hyperlanes, jump gates, warp corridors, etc.) are planned for future iterations. MVP uses simple linear travel only.

## Ship Types (MVP)

### Colony Ship
- Purpose: **Colonize** an uncolonized planet.
- **Consumed on use** — the ship lands and becomes the colony.
- Built at a Shipyard from Iron Ingots.
- Has a travel speed.

### Cargo Vessel
- Purpose: **Transport resources** between planets.
- Each Cargo Vessel has a **fixed cargo capacity**.
- A fleet's total cargo capacity is the **sum of its individual Cargo Vessels**.
- Resources are **bound to individual ships** — if a ship is destroyed, the resources it carries are lost (or left as wreckage).
- Built at a Shipyard from Iron Ingots.
- Has a travel speed.

> **Design Intent:** Tying cargo to individual ships means combat has real economic consequences. Losing part of a fleet means losing the resources those specific ships were carrying.

> **Post-MVP Note:** Scout Vessels (cheap, fast exploration ships) and scouting/espionage mechanics are planned for future iterations.

## Mission Types (MVP)

### Colonize
- Fleet must contain at least one **Colony Ship**.
- Target must be an **uncolonized planet**.
- On arrival, **one** Colony Ship is consumed and the planet becomes owned by the player. Any additional Colony Ships remain in the fleet.
- After colonization, **cargo is automatically unloaded** into the new planet's storage (same partial-unload rules apply if storage is full).
- Remaining ships are added to the new planet's ship roster.
- **If the target planet is already colonized** (by another player who arrived first): the Colonize mission **fails**. The Colony Ship is **not consumed**. The fleet idles at the planet.

### Transport
- Fleet must contain at least one **Cargo Vessel**.
- Resources are loaded from the **origin planet's storage** before departure.
- Cargo is distributed **automatically** across Cargo Vessels based on capacity. A single Cargo Vessel can carry a **mix of resource types** — capacity is measured in **tonnage**, not per-type.
- On arrival, resources are unloaded into the **destination planet's storage**.
- **If destination storage is full:** Partial unload — storage is filled to capacity, remaining resources stay on the ships. Players can **trigger another unload** later once storage frees up.
- Destination must be a planet **owned by the same player**.

### Move
- Any fleet composition.
- Simply **relocates ships** from one planet to another.
- No cargo, no mission objective — just repositioning.
- Destination must be **any planet** (own, uncolonized, or other player's).

> **Post-MVP Note:** Additional mission types are planned: Scout (reconnaissance/espionage), Trade (send to other players via market), Interception (combat fleets in transit or at orbit), and military missions.

## Travel

- Travel is **linear** — fleets move in a straight line from origin to destination.
- Travel time is based on the **3D coordinate distance** between the two planets and the **fleet's speed** (determined by its slowest ship).
- Fleets in transit **can be cancelled**. A cancelled fleet turns around and returns to its origin planet, taking the **same amount of time it has already traveled** to get back. (e.g., if cancelled 5 hours into a 12-hour trip, the return takes 5 hours.)

## Ship Roster

Each planet maintains a **ship roster** — a list of ships stationed there and available for fleet assembly.

- Ships completed by a Shipyard are added to the **local planet's roster**.
- When a fleet arrives and completes its mission, surviving ships are added to the **destination planet's roster**.
- A fleet can be **disbanded** at any planet, returning all its ships to that planet's roster.
- Ships on a roster can be **selected and assembled into new fleets**.
- Ships can remain on the roster of unowned planets indefinitely.

## Fleet Lifecycle

1. **Assembly** — Ships are selected from a planet's roster. Resources are loaded onto Cargo Vessels if applicable.
2. **Departure** — Fleet leaves the planet and enters transit. Ships are removed from the planet's roster.
3. **Transit** — Fleet travels through space for a duration based on distance and speed.
4. **Arrival** — Fleet arrives at destination and executes its mission (colonize, unload cargo).
5. **Post-mission** — Non-consumed ships are added to the destination planet's ship roster.
