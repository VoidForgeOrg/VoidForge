# Scoring System

## Overview

Voidforge calculates a **score** for each player based on everything they own. This score is used for leaderboards and general progression tracking.

## Score Inputs

The score calculation takes into account **all player-owned assets**:

- **Planets** — Number of colonized planets.
- **Buildings** — Number and type of buildings constructed across all planets.
- **Fleets** — Ships owned, including those in transit.
- **Resources** — Total resources stockpiled across all planetary storage.

> **TODO:** Define specific point values / weighting for each category. Some assets may be worth more than others (e.g., a Shipyard might score higher than a Drill, a Colony Ship higher than a Cargo Vessel).

## Behavior

- Score is **recalculated regularly** (per tick or on demand — TBD).
- Score reflects **current state** — if a player loses a planet or a fleet is destroyed, their score drops accordingly.
- Score is **visible** on leaderboards to all players.

> **Post-MVP Note:** Score categories could be broken down on leaderboards (e.g., top economic player, top military player, most planets) to encourage different playstyles.
