# Voidforge

## Project Overview

Voidforge is a persistent, long-form space strategy MMO built as a **headless engine** — the game exists as rules and state accessible via an API. The official WebUI is just one possible client; players are encouraged to build their own interfaces.

## Architecture

- **Headless / API-first** — All game logic is enforced server-side. Clients interact exclusively through the API.
- **Event-driven engine** with lazy calculation — no tick/heartbeat. State is computed on demand; changes are resolved via scheduled events and checkpoints.
- **Open-source** — Community-driven balance and governance.

## MVP Scope

The MVP focuses on core economic and expansion mechanics. **No combat** in MVP.

### Core Systems
- **Planets** — 3D coordinate system, solar systems as groupings (no system-level mechanics yet), fog of war with scouting
- **Resources** — Physical economy (Iron Ore → Iron Ingots at 1:2 ratio), finite depletable pools, per-type storage caps, energy as flow resource
- **Buildings** — Drill, Refinery, Shipyard, Generator. Fixed building slots per planet. Continuous resource consumption for construction.
- **Fleets** — Colony Ship, Cargo Vessel, Scout Vessel. Linear point-to-point travel. Missions: Colonize, Transport, Scout, Move.
- **Scoring** — Player score from all owned assets (planets, buildings, ships, resources including in-transit)

### Key Design Decisions
- Buildings and ships consume resources **over time** during construction (not upfront)
- Halted buildings consume **5% energy** (storage full, no input, idle shipyard)
- Construction does **not** consume energy — only completed buildings do
- Any ship can scout; Scout Vessels are just cheaper and faster
- Cancellation yields **no resource refund** (buildings, ships, fleets)
- Resource distribution among competing consumers is **even split**
- Cascading events (depletion chains, energy rebalancing) are MVP scope

## Game Design Documents

All detailed design docs live in `game-design/`:
- `overview.md` — High-level summary of all MVP systems
- `planets.md` — Planets, ownership, solar systems, starting conditions, visibility
- `resources.md` — Resource types, pools, storage, energy, distribution
- `buildings.md` — Building types, construction, demolition, energy dependency
- `fleets.md` — Ship types, missions, travel, ship roster, fleet lifecycle
- `engine.md` — Event-driven engine, lazy calculation, checkpoints, cascading events
- `scoring.md` — Score calculation and leaderboards
- `player-actions.md` — Complete list of MVP player actions

## Development Notes

- Tech stack and implementation details are **not yet decided** — design docs focus purely on game mechanics
- Specific numeric values (build times, costs, speeds, storage caps) are **TBD** — to be defined during balancing
- Post-MVP features noted in docs: combat, alliances, tech tree, galactic market, planet traits, advanced travel (hyperlanes/jump gates), branching resource trees
