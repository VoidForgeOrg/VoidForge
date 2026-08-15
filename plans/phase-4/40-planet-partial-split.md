# #40 — Split the `Planet` aggregate via partial classes

**Branch:** `feat/planet-partial-split` → `phase-4` · **Merge gate:** existing suite green with **zero test edits**.

## Context

`Domain/Planet.cs` (345 lines) carries four concerns: resource pools + the shared rate engine, energy, building lifecycle, and ship-queue/roster lifecycle. Marten needs a single type for `Apply` discovery and the inline snapshot, so the split uses partial classes — a pure mechanical move. Audit result: nothing else qualifies today (`ShipEndpoints.cs` 164, `Program.cs` 117, `BuildingSpecs.cs` 47 lines), so the audit lands as a guideline note, not further splits.

## Steps

1. **Split into four partial files** (move-only; no signature, visibility, or behavior changes):
   - `Planet.cs` — state fields, `Apply(PlanetCreated)`, `Apply(PlanetColonized)`, `RebaseRates`, `CheckpointAllResources`
   - `Planet.Energy.cs` — `GetEnergyGenerationMw`, `GetEnergyConsumptionMw`, `GetProductivityMultiplier`
   - `Planet.Buildings.cs` — `PlaceBuilding`, `StartConstruction`, `CompleteBuilding` + their `Apply`s
   - `Planet.Ships.cs` — `QueueShip`, `CompleteShipBuild`, `CancelShipBuild`, shipyard-capacity + auto-start helpers, `IndexOfShipBuild` + the ship `Apply`s
2. **Analyzer check:** build; if MA0048 objects to `Planet.*.cs`, configure its accepted pattern rather than inventing non-standard names.
3. **Record the audit + guideline:** add the soft size/responsibility heuristic and today's audit outcome to `technical-design/project-structure.md`.
4. **Verify:** `dotnet build` + full `dotnet test` + `dotnet format` — and confirm the PR diff touches no test files.

## Non-goals

No renames, no behavior change, no speculative Fleet-facing seams (those are #48's job).
