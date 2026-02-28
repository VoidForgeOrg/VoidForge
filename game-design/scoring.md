# Scoring System

## Overview

Voidforge calculates a **score** for each player based on everything they own. This score is used for leaderboards and general progression tracking.

## Score Inputs

The score calculation takes into account **all player-owned assets**, including incomplete ones:

- **Planets** — Number of colonized planets.
- **Buildings** — Number and type of buildings across all planets, **including those under construction**.
- **Ships** — All ships: on planet rosters, in transit, and **under construction** in Shipyards.
- **Resources** — Total resources across all planetary storage **and in transit** (loaded on cargo vessels).

> **TODO:** Define specific point values / weighting for each category. Some assets may be worth more than others (e.g., a Shipyard might score higher than a Drill, a Colony Ship higher than a Cargo Vessel).

## Behavior

- Score is **recalculated on demand** using lazy calculation (consistent with the event-driven engine).
- Score reflects **current state** — if a player loses a planet or a fleet is destroyed, their score drops accordingly.
- Score is **visible** on leaderboards to all players.

> **Post-MVP Note:** Score categories could be broken down on leaderboards (e.g., top economic player, top military player, most planets) to encourage different playstyles.
