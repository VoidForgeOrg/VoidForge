# Scoring System

## Overview

Voidforge calculates a **score** for each player based on everything they own. This score is used for leaderboards and general progression tracking.

## Score Inputs

The score calculation takes into account **all player-owned assets**, including incomplete ones:

- **Planets** — Number of colonized planets.
- **Buildings** — Number and type of buildings across all planets, **including those under construction**.
- **Ships** — All ships: on planet rosters, in transit, and **under construction** in Shipyards.
- **Resources** — Total resources across all planetary storage **and in transit** (loaded on cargo vessels).

Point values / weighting live in one place — `ScoringSpecs` (`src/Voidforge.Api/Domain/ScoringSpecs.cs`), a static rules table (like `BuildingSpecs`): `PointsPerPlanet`, `BuildingPoints(type)`, `ShipPoints(type)`, `ResourcePointsPerUnit(type)`. Current values are **placeholders pending balancing** (Shipyard > Drill, Colony Ship > Cargo Vessel, ingot > ore per unit).

## Behavior

- Score is **recalculated on demand** by the read-side `ScoreCalculator` (`src/Voidforge.Api/Scoring/`), consistent with the engine's lazy-calculation principle — never a stored field. Exposed as the `score` on `GET /api/players/me` (#67); resource contributions are evaluated from pool checkpoints at query time, so they are never stale.
- Score reflects **current state** — if a player loses a planet or a fleet is destroyed, their score drops accordingly. Buildings count unless tombstoned (Cancelled/Demolished); a ship is counted once whether on a roster, in a fleet, or still under construction.
- Score is **visible** on leaderboards to all players (the ranked read is #68, which reuses `ScoreCalculator` over persisted `ScoreComponents`).

> **Post-MVP Note:** Score categories could be broken down on leaderboards (e.g., top economic player, top military player, most planets) to encourage different playstyles.
