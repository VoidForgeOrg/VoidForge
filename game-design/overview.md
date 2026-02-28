# Voidforge MVP — Design Overview

## What is Voidforge?

Voidforge is a persistent space strategy MMO where players build empires across a universe of planets, manage economies, and expand through exploration and colonization. The game is built as a **headless engine** — all logic runs server-side via an API, and any client (web, mobile, CLI, VR) can interact with it.

## The Universe

The game world is a **3D space** containing **solar systems**, each with multiple **planets**. All solar system locations are visible on the star map, but planet details are hidden until scouted.

- **Planets** are the core entity — each has resource pools, building slots, storage capacity, and an energy grid
- **Solar systems** group planets together (no system-level mechanics in MVP)
- **3D coordinates** determine distances, which affect fleet travel times

## New Player Experience

A new player receives a **Homeworld** with:
- 1 Drill, 1 Refinery, 1 Generator (all operational)
- Starting resources in storage
- Enough energy headroom for early growth

From here, the player builds up their economy, constructs a Shipyard, builds ships, scouts nearby planets, and eventually colonizes new worlds.

## Economy

Voidforge uses a **physical economy** — resources exist on specific planets, not in a global bank.

### Resource Chain (MVP)
```
Iron Ore (extracted by Drills) → Iron Ingots (refined by Refineries, 1:2 ratio) → Ships & Buildings
```

### Key Mechanics
- **Resource pools** are finite and depletable — once mined out, they're gone forever
- **Planetary storage** has fixed capacity per resource type — full storage halts production
- **Energy** is a flow resource (MW) — generators power buildings; overload reduces all productivity
- **Resource distribution** among competing buildings is an even split
- Resources must be **physically transported** between planets via fleets

## Buildings

Buildings occupy **fixed slots** on planets. MVP building types:

| Building | Function | Energy |
|----------|----------|--------|
| **Drill** | Extracts Iron Ore from resource pool | Consumes (5% when halted) |
| **Refinery** | Converts Iron Ore → Iron Ingots (1:2) | Consumes (5% when halted) |
| **Shipyard** | Builds ships (3 parallel, unlimited queue) | Consumes (5% when idle) |
| **Generator** | Produces energy (MW) | Produces |

### Construction & Demolition
- Buildings cost Iron Ingots, consumed **continuously over the build duration** (not upfront)
- Construction occupies a slot but **does not consume energy** until complete
- Multiple buildings can be constructed in parallel
- Construction can be cancelled (no refund, slot freed immediately)
- Demolition takes time, yields no resources, building stops functioning immediately

## Ships & Fleets

Ships are built at Shipyards and stored on a **planet's ship roster**. Players assemble ships into **fleets** and send them on missions.

### Ship Types (MVP)

| Ship | Purpose | Notes |
|------|---------|-------|
| **Colony Ship** | Colonize planets | Consumed on use |
| **Cargo Vessel** | Transport resources | Fixed tonnage capacity, mixed cargo |
| **Scout Vessel** | Exploration | Cheapest and fastest ship |

### Missions (MVP)

| Mission | Target | Requirements | On Arrival |
|---------|--------|-------------|------------|
| **Colonize** | Uncolonized, scouted planet | Colony Ship | Colony Ship consumed, cargo auto-unloads, planet claimed |
| **Transport** | Own planet | Cargo Vessel | Cargo unloaded (partial if storage full) |
| **Scout** | Any planet | Any ship | Planet intel revealed/updated |
| **Move** | Own or scouted planet | Any ship | Ships relocated |

### Fleet Mechanics
- Fleet speed = slowest ship
- Linear point-to-point travel based on 3D distance
- Fleets can be cancelled mid-transit (return time = time already traveled)
- Fleets can be disbanded (ships return to planet roster)
- Any ship can scout; Scout Vessels are just cheaper and faster
- Failed colonization (planet already taken): colony ship preserved, fleet idles, intel gathered

## Engine

Voidforge uses an **event-driven engine with lazy calculation** — no global tick/heartbeat.

- **Lazy calculation**: State is computed on demand using `checkpoint_value + (rate * elapsed_time)`
- **Scheduled events**: Deterministic events (depletion, fleet arrival, build completion) are queued at known times
- **Checkpoints**: When events fire or players act, the engine snapshots current state and recalculates rates
- **Cascading events**: Chain reactions (e.g., ore depletion → refinery halt → energy rebalance) are resolved within a single checkpoint

## Scoring

Players have a **score** based on all owned assets:
- Planets, buildings (including under construction), ships (including under construction and in transit), resources (including in transit)
- Recalculated on demand (lazy calculation)
- Visible on leaderboards to all players

## Player Actions (Complete MVP List)

### Planet
- Build a building / Cancel construction / Demolish a building

### Shipyard
- Queue ship construction / Cancel ship construction

### Fleet
- Assemble fleet from roster / Send fleet on mission / Cancel fleet in transit / Disband fleet / Trigger cargo unload

## What's NOT in MVP

The following are planned for post-MVP:
- Combat and military ships
- Alliances / clans
- Tech tree (Astrophysics, Materials Science, Applied Weaponry)
- Galactic market / player trading
- Planet types and traits (Volcanic, Frozen, etc.)
- Advanced travel (hyperlanes, jump gates)
- Branching resource trees (multiple ore types, alloys)
- Solar system-level mechanics
- Expandable storage buildings
- Fuel-consuming generators
