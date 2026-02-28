# Player Actions (MVP)

## Overview

This is the complete list of actions a player can perform in Voidforge MVP. Every interaction with the game maps to one of these actions, submitted via the API.

## Planet Actions

### Build a Building
- Select a planet with an available building slot.
- Choose a building type (Drill, Refinery, Shipyard, Generator).
- Construction begins, consuming Iron Ingots continuously from planetary storage.
- The building occupies a slot immediately but does not consume energy until completed.

### Cancel Construction
- Cancel a building that is currently under construction.
- No resources are refunded.
- The building slot is freed immediately.

### Demolish a Building
- Select a completed building to demolish.
- Demolition takes time. The building stops functioning immediately.
- No resources are refunded.
- The building slot is freed once demolition completes.

## Shipyard Actions

### Queue Ship Construction
- Select a Shipyard on a planet.
- Choose a ship type (Colony Ship, Cargo Vessel) and add it to the build queue.
- Up to 3 ships are built in parallel. Additional ships wait in queue and start automatically as slots free up.
- Each ship consumes Iron Ingots continuously from planetary storage during its build.

### Cancel Ship Construction
- Cancel a specific ship that is under construction or queued.
- No resources are refunded.
- If the ship was actively building, the parallel build slot is freed immediately and the next queued ship (if any) begins.

## Fleet Actions

### Assemble Fleet
- Select ships from a planet's ship roster.
- Combine them into a fleet (any mix of ship types).
- If the mission involves cargo, specify resources to load from planetary storage. Cargo is distributed automatically across Cargo Vessels by tonnage.

### Send Fleet on Mission
- Assign a mission to an assembled fleet:
  - **Colonize** — Target: uncolonized planet. Requires at least one Colony Ship.
  - **Transport** — Target: own planet. Requires at least one Cargo Vessel.
  - **Move** — Target: any planet. Any fleet composition.
- Fleet departs and enters transit.

### Cancel Fleet in Transit
- Cancel a fleet that is currently traveling.
- The fleet turns around and returns to its origin, taking the same time it has already traveled.

### Disband Fleet
- Disband a fleet stationed at a planet.
- All ships return to the planet's ship roster.
- Any cargo remains on the ships until manually unloaded (if applicable).

### Trigger Unload
- Unload cargo from a fleet stationed at an owned planet.
- Resources are moved from Cargo Vessels into planetary storage.
- If storage is full, partial unload occurs — storage is filled to capacity, remainder stays on ships.
