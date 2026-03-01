# Voidforge MVP — Implementation Plan

## Approach

Build a **walking skeleton** — wire the full domain chain end-to-end first, then layer on complexity. Each phase adds the next link in the chain rather than completing features. Edge cases, cancellation flows, and polish come last.

```
Player → Planet → Resources → Buildings → Energy → Refineries → Shipyard → Ships → Fleets → Missions
```

## Phases

| Phase | Focus | Outcome |
|-------|-------|---------|
| [Phase 1](phase-1-foundation.md) | Infrastructure | Running app, Docker, CI, auth middleware |
| [Phase 2](phase-2-domain-skeleton.md) | Domain skeleton | Player → Planet → Resource pools → Buildings → Lazy calc |
| [Phase 3](phase-3-production-chain.md) | Production chain | Energy grid → Refineries → Shipyards → Ships |
| [Phase 4](phase-4-fleets-expansion.md) | Fleets & expansion | Ship roster → Fleet assembly → Travel → Missions |
| [Phase 5](phase-5-hardening.md) | Hardening | Depletion, storage caps, halting, cascading, cancellation, scoring, API polish |

## Labels

- `infra` — Project setup, CI/CD, deployment
- `domain:core` — Lazy calculation engine, cascading events
- `domain:planets` — Planet aggregate, solar systems
- `domain:resources` — Resource pools, storage, distribution
- `domain:buildings` — Building types, construction, energy
- `domain:fleets` — Ships, fleets, travel, missions
- `domain:players` — Registration, auth, scoring
- `api` — HTTP endpoints

## Dependency Chain

```
Phase 1: Infrastructure
  └─► Phase 2: Domain Skeleton (player owns planet, planet has resources, buildings extract)
        └─► Phase 3: Production Chain (energy, refining, shipbuilding)
              └─► Phase 4: Fleets & Expansion (travel, colonize, transport)
                    └─► Phase 5: Hardening (edge cases, cascading, scoring, API polish)
```

## Notes

- Each phase includes minimal endpoints alongside domain work — enough to test and verify via HTTP, not the full polished API.
- Specific numeric values (build times, costs, speeds, storage caps) remain TBD and will be defined during balancing, likely as configuration.
- Post-MVP features (fog of war, combat, alliances, tech tree, etc.) are not tracked here.
