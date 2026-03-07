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
- **Planets** — 3D coordinate system, solar systems as groupings (no system-level mechanics yet), full visibility (no fog of war in MVP)
- **Resources** — Physical economy (Iron Ore → Iron Ingots at 1:2 ratio), finite depletable pools, per-type storage caps, energy as flow resource
- **Buildings** — Drill, Refinery, Shipyard, Generator. Fixed building slots per planet. Continuous resource consumption for construction.
- **Fleets** — Colony Ship, Cargo Vessel. Linear point-to-point travel. Missions: Colonize, Transport, Move.
- **Scoring** — Player score from all owned assets (planets, buildings, ships, resources including in-transit)

### Key Design Decisions
- Buildings and ships consume resources **over time** during construction (not upfront)
- Halted buildings consume **5% energy** (storage full, no input, idle shipyard)
- Construction does **not** consume energy — only completed buildings do
- Cancellation yields **no resource refund** (buildings, ships, fleets)
- Resource distribution among competing consumers is **even split**
- Cascading events (depletion chains, energy rebalancing) are MVP scope

## Game Design Documents

All detailed design docs live in `game-design/`:
- `_overview.md` — High-level summary of all MVP systems
- `planets.md` — Planets, ownership, solar systems, starting conditions, visibility
- `resources.md` — Resource types, pools, storage, energy, distribution
- `buildings.md` — Building types, construction, demolition, energy dependency
- `fleets.md` — Ship types, missions, travel, ship roster, fleet lifecycle
- `engine.md` — Event-driven engine, lazy calculation, checkpoints, cascading events
- `scoring.md` — Score calculation and leaderboards
- `player-actions.md` — Complete list of MVP player actions

## Technical Design

Architecture and implementation docs live in `technical-design/`:
- `architecture.md` — Tech stack, database schema, event queue, lazy calculation engine, deployment
- `project-structure.md` — Folder layout, conventions, build configuration
- `domain-model.md` — Current aggregates, events, documents, and how to add new ones
- `authentication.md` — API key auth flow, handler, authorization policy
- `testing.md` — Test host setup, known pitfalls, coverage

## Development Notes

- Specific numeric values (build times, costs, speeds, storage caps) are **TBD** — to be defined during balancing
- Post-MVP features noted in docs: fog of war/scouting, combat, alliances, tech tree, galactic market, planet traits, advanced travel (hyperlanes/jump gates), branching resource trees

## Working Conventions

- When implementing a new feature or system, update or create the relevant doc in `technical-design/` to reflect the current state
- Tests must pass before completing any task

## Quick Reference

```bash
dotnet restore src/Voidforge.slnx     # Restore packages
dotnet build src/Voidforge.slnx       # Build solution
dotnet test src/Voidforge.slnx        # Run tests
dotnet format src/Voidforge.slnx      # Format code
```
