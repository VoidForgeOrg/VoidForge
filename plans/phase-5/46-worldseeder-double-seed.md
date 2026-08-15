# #46 — WorldSeeder Double-Seed Race Implementation Plan

**Goal:** Concurrent `WorldSeeder.StartAsync` runs (multi-instance startup, or a restart racing itself) seed the world **exactly once**, instead of both passing the count check and each seeding a full world.

**Root cause (from survey):** `WorldSeeder.StartAsync` (`WorldSeeder.cs:14-67`) does a non-atomic read-then-act — `Query<SolarSystem>().CountAsync()`, skip if `> 0`, else build the world and `SaveChangesAsync`. Two seeders both read count 0 (TOCTOU) and both commit. No advisory lock, no marker, no unique constraint.

**Tech Stack:** .NET 9, Marten, `IHostedService`, xUnit.

## Global Constraints
- `TreatWarningsAsErrors`; MA0048 one-public-type-per-file.
- Commits conventional, suffixed `(#46)`. Branch off updated `phase-5` (after #62 and ideally #45 merge — reuses #45's `MartenExceptions.IsUniqueViolation`).
- Verification via CI; locally only `dotnet build -warnaserror`.

## File Structure
```text
src/Voidforge.Api/WorldGeneration/WorldSeedMarker.cs (new — single-row seed marker)
src/Voidforge.Api/WorldGeneration/WorldSeeder.cs      (modify — commit marker atomically, catch dup)
src/Voidforge.Api/Program.cs                          (modify — register marker schema if needed)
src/Voidforge.Tests/WorldGeneration/WorldSeederIdempotencyTests.cs (new)
```

## Plan-level decisions
1. **Marker-document + atomic commit, not an advisory lock.** Define `WorldSeedMarker` with a well-known constant `Id`; insert it in the **same** `session` as the world data so Marten commits both in one transaction. A second seeder's insert hits the primary-key `23505` and the whole batch (including its duplicate world) rolls back atomically. This reuses #45's `MartenExceptions.IsUniqueViolation` and needs no raw-SQL lock plumbing through Marten's session/transaction model.
2. **Keep the fast-path count check.** The common case (restart against an already-seeded DB) still short-circuits cheaply without attempting a doomed insert; the marker only arbitrates the genuine race.
3. **Loser logs "already seeded" and returns** — a duplicate is success, not an error. A hosted service throwing from `StartAsync` aborts host startup, so the catch must swallow the unique-violation specifically (rethrow anything else).

### Task 1: Marker document
**Files:** Create `src/Voidforge.Api/WorldGeneration/WorldSeedMarker.cs`
- [ ] **Step 1:** Minimal document; the fixed `Id` is the PK that enforces single-seed.
```csharp
namespace Voidforge.Api.WorldGeneration;

/// <summary>Single-row marker committed atomically with the seeded world so a second
/// concurrent seeder collides on the primary key (23505) instead of double-seeding.</summary>
public sealed class WorldSeedMarker
{
    // Well-known constant id — there is only ever one of these.
    public static readonly Guid WellKnownId = new("5eed0000-0000-0000-0000-000000000001");

    public Guid Id { get; set; }
}
```
- [ ] **Step 2:** If the codebase registers document schemas explicitly (Program.cs ~27-40 registers `Player`/`ApiKey`), add `opts.Schema.For<WorldSeedMarker>();` for parity. Build `-warnaserror` → clean.

### Task 2: Atomic seed
**Files:** Modify `src/Voidforge.Api/WorldGeneration/WorldSeeder.cs`
- [ ] **Step 1:** Keep the `existingCount > 0` fast-path. Before `SaveChangesAsync`, `session.Insert(new WorldSeedMarker { Id = WorldSeedMarker.WellKnownId });`. Wrap the save:
```csharp
try
{
    session.Insert(new WorldSeedMarker { Id = WorldSeedMarker.WellKnownId });
    await session.SaveChangesAsync(cancellationToken);
    LogWorldSeeded(logger, opts.SolarSystemCount, opts.PlanetsPerSystem);
}
catch (Exception ex) when (MartenExceptions.IsUniqueViolation(ex))
{
    // Another instance won the seed race; its world is authoritative. Not an error.
    LogWorldAlreadySeeded(logger, opts.SolarSystemCount);
}
```
(Reuse the existing log delegates; add `using Voidforge.Api.Http;` for `MartenExceptions`.)
- [ ] **Step 2:** Build `-warnaserror` → clean.

### Task 3: Idempotency test
**Files:** Create `src/Voidforge.Tests/WorldGeneration/WorldSeederIdempotencyTests.cs`
- [ ] **Step 1:** In the shared `[Collection(IntegrationCollection.Name)]`, resolve `IDocumentStore` from `fixture.Host.Services`, count `SolarSystem` (world already seeded once by the fixture boot). Construct a second `WorldSeeder` against the same store/options/logger and call `StartAsync` again; assert the `SolarSystem` count is unchanged (no second world). Optionally run two fresh `StartAsync` calls concurrently against a store and assert a single world's worth. Use `Microsoft.Extensions.Options.Options.Create(...)` and `NullLogger<WorldSeeder>.Instance`.
- [ ] **Step 2:** Build `-warnaserror` → clean. (CI runs the suite.)

### Task 4: PR
- [ ] Commit each task suffixed `(#46)`, push, PR base `phase-5`, "Closes #46". Self-merge on green CI.
