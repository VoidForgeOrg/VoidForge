# Epoch 1 Frontend Scope

Epoch 1 is the documented MVP: a non-combat economy and expansion loop built around planets, resources, buildings, shipbuilding, fleets, colonization, transport, and scoring.

This document maps what the frontend needs to contain. It does not define visual design.

## 1. Account And Auth

The frontend needs:

- Player registration by name.
- One-time display of the generated API key.
- API-key entry/storage for returning players.
- Use of `X-API-Key` on authenticated requests.
- Current player profile display.
- Clear handling for missing, invalid, or expired credentials.

Current backend support:

- `POST /api/players/register`.
- `GET /api/players/me`.
- API-key authentication with `X-API-Key`.

## 2. Empire Overview

The frontend needs an empire-level landing view that summarizes:

- Owned planets.
- Stored resources across the empire.
- Active building construction.
- Active ship construction.
- Stationed and in-transit fleets.
- Important operational alerts.
- Player score and rank once scoring exists.

Alerts should cover:

- Storage full.
- Missing resource input.
- Depleted ore pool.
- Energy shortage or overload.
- Idle shipyard.
- Construction complete.
- Ship complete.
- Fleet arrival.

## 3. Universe And Planet Browser

MVP has full visibility and no fog of war, so the frontend needs a way to browse all known space.

The frontend needs:

- Solar-system list or map.
- Planet list per solar system.
- Planet ownership state: owned, unowned, or owned by another player.
- Coordinates or distance context.
- Planet resource pool remaining.
- Building slot count.
- Storage capacities.
- Colonization eligibility.

Current backend support:

- `GET /api/solar-systems`.
- `GET /api/planets/{id}`.

Current backend gaps:

- No broad planet list endpoint.
- No owned-planets endpoint.
- No colonization state beyond basic owner/null ownership.

## 4. Planet Overview

The planet overview is the core management screen.

The frontend needs to show:

- Planet name, owner, and solar system.
- Iron Ore stored and storage capacity.
- Iron Ingots stored and storage capacity.
- Remaining Iron Ore pool.
- Building slots.
- Current production and consumption rates.
- Energy generated, consumed, and net balance.
- Halted and idle reasons.
- Local ships.
- Stationed fleets.
- Relevant incoming and outgoing fleets.

Current backend support:

- Planet name.
- Solar system ID.
- Owner ID.
- Iron Ore pool.
- Building slot count.
- Iron Ore stored and capacity.
- Iron Ingots stored and capacity.

Current backend gaps:

- Buildings.
- Production and consumption rates.
- Energy.
- Ships.
- Fleets.
- Halt reasons.

## 5. Building Management

Epoch 1 building types:

- Drill.
- Refinery.
- Shipyard.
- Generator.

The frontend needs:

- Building slot grid or list.
- Empty slot build menu.
- Building type details.
- Existing building status.
- Construction progress and ETA.
- Continuous resource consumption during construction.
- Cancel construction action.
- Demolish completed building action.
- Halted state and reason.

Rules the UI must make visible:

- Buildings and ships consume resources over time during construction, not upfront.
- Construction does not consume energy.
- Completed buildings consume energy.
- Halted completed buildings consume 5% energy.
- Cancellation gives no resource refund.
- Resource distribution among competing consumers is an even split.

Halt reasons should include:

- Storage full.
- Missing input resource.
- No active shipyard work.
- Energy shortage.
- Depleted resource pool.

## 6. Resource Economy

Epoch 1 resources:

- Iron Ore.
- Iron Ingots.
- Energy as a flow resource.

The frontend needs:

- Current amount per stored resource.
- Storage cap per stored resource.
- Net rate per resource.
- Production sources.
- Consumption sinks.
- Time until storage full.
- Time until depletion.
- Refinery conversion visibility: Iron Ore -> Iron Ingots at `1:2`.
- Explanation of even resource distribution among competing consumers.

Because the backend uses lazy calculation, the frontend should display server-derived current values, rates, ETAs, and halt reasons rather than acting as the authority for simulation.

## 7. Energy

The frontend needs a planet-level energy summary:

- Generator output.
- Building energy consumption.
- Surplus or deficit.
- Productivity penalty if overloaded.
- Energy draw per building.
- Idle or halted 5% energy drain.

Energy should be visible both in the planet overview and wherever building decisions are made.

## 8. Shipyard And Ship Construction

Epoch 1 ship types:

- Colony Ship.
- Cargo Vessel.

The frontend needs:

- Shipyard status.
- Ship build queue.
- Up to 3 active ship builds per Shipyard.
- Queued ships after active build slots.
- Build progress and ETA.
- Required resources.
- Cancel ship construction action.
- Idle shipyard indication.

## 9. Ship Roster

For each planet, the frontend needs to show available ships:

- Ship ID or name.
- Ship type.
- Cargo capacity.
- Speed.
- Current location.
- Assignment state: idle, in fleet, building, or in transit.

The ship roster is the source for fleet assembly.

## 10. Fleet Management

The frontend needs to show:

- Stationed fleets.
- In-transit fleets.
- Fleet composition.
- Origin planet.
- Destination planet.
- Mission type.
- Cargo.
- Departure time.
- Arrival time.
- ETA.
- Fleet status.

Fleet actions needed:

- Cancel in-transit fleet.
- Disband stationed fleet.
- Unload cargo.

## 11. Mission Planner

Epoch 1 mission types:

- Move.
- Transport.
- Colonize.

The frontend needs:

- Origin planet selection.
- Ship selection.
- Destination planet selection.
- Mission type selection.
- Cargo loading for transport missions.
- Travel time preview.
- Validation before launch.
- Launch confirmation.

Validation rules:

- Colonize requires a Colony Ship.
- Colonize targets an unowned planet.
- Transport requires a Cargo Vessel.
- Transport targets an owned planet.
- Move can target another planet.
- Cargo cannot exceed fleet cargo capacity.
- Cargo cannot exceed available stored resources.

## 12. Colonization Flow

The frontend needs:

- Discovery of unowned planets.
- Colonization target details.
- Colony Ship requirement display.
- Launch confirmation.
- Fleet-in-transit tracking.
- Arrival and settlement result display.
- Warning that new colonies may need transported support resources.

## 13. Scoring And Leaderboard

Epoch 1 scoring includes value from:

- Owned planets.
- Buildings.
- Ships.
- Stored resources.
- In-transit resources.

The frontend needs:

- Player score.
- Leaderboard.
- Rank.
- Score breakdown if the API supports it.

Current backend gap:

- Scoring and leaderboard endpoints do not exist yet.

## 14. Notifications And Event Feedback

The frontend needs to surface event-driven state changes clearly.

Events and state changes to show:

- Building construction completed.
- Ship construction completed.
- Fleet arrived.
- Planet colonized.
- Resource depleted.
- Storage full.
- Building halted.
- Building resumed.
- Energy balance changed.
- Cargo unloaded.

MVP can use polling. Realtime push is not required for Epoch 1.

## 15. API-First Client Needs

Voidforge is headless and API-first. The official frontend should respect that model.

The frontend should include:

- API key entry and management.
- Clear API error messages.
- Copyable IDs where useful, such as planet, fleet, and player IDs.
- Optional links to Swagger or API docs.

The frontend must not become the source of truth for game rules or simulation.

## Not In Epoch 1

These are explicitly out of scope for Epoch 1:

- Combat.
- Fog of war.
- Scouting.
- Alliances.
- Tech tree.
- Market or trade system.
- Planet traits.
- Hyperlanes or jump gates.
- Advanced diplomacy.
- Realtime push requirements.

## Current Backend Reality

Implemented today:

- Player registration.
- API-key authentication.
- Current player endpoint.
- Solar-system listing.
- Single planet detail.
- Seeded planets with basic resources and storage.

Design-doc only today:

- Buildings.
- Building construction.
- Resource production and consumption rates.
- Energy.
- Ships.
- Ship construction.
- Fleets.
- Missions.
- Colonization by fleet.
- Transport.
- Scoring.
- Leaderboard.
- Gameplay alerts and event feedback.

## Source Documents

- `game-design/_overview.md`.
- `game-design/planets.md`.
- `game-design/resources.md`.
- `game-design/buildings.md`.
- `game-design/fleets.md`.
- `game-design/engine.md`.
- `game-design/scoring.md`.
- `game-design/player-actions.md`.
- `technical-design/architecture.md`.
- `technical-design/domain-model.md`.
- `technical-design/authentication.md`.
- `technical-design/project-structure.md`.
