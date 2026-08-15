# #45 — Concurrent Same-Name Registration → 409 Implementation Plan

**Goal:** Two concurrent registrations with the same name yield exactly one 200 and one **409** (today the loser is a **500**), by catching the `Player.Name` unique-index violation at `SaveChangesAsync` instead of letting it escape.

**Root cause (from survey):** `PlayerEndpoints.Register` pre-checks name uniqueness on its own session (`Query<Player>().AnyAsync`), then writes the `Player` stream inside `TryClaimHomeworld` at `SaveChangesAsync` (`PlayerEndpoints.cs:135`), whose `catch` handles only `ConcurrencyException`. A duplicate `Player.Name` insert throws a Marten `MartenCommandException` wrapping `Npgsql.PostgresException { SqlState = "23505" }` — a different type — so it escapes as 500.

**Tech Stack:** .NET 9, Marten, Wolverine HTTP, xUnit + Alba.

## Global Constraints
- `TreatWarningsAsErrors`; MA0048 one-public-type-per-file.
- Commits conventional, suffixed `(#45)`. Branch off updated `phase-5` (after #62 merges).
- Verification via CI (full suite); locally only `dotnet build -warnaserror` to avoid the shared-test-DB race.

## File Structure
```text
src/Voidforge.Api/Http/MartenExceptions.cs   (new — IsUniqueViolation helper, reused by #46)
src/Voidforge.Api/Endpoints/PlayerEndpoints.cs (modify — catch unique-violation at the claim SaveChanges)
src/Voidforge.Tests/Players/PlayerRegistrationTests.cs (modify — concurrent-race test)
```

## Plan-level decisions
1. **Fix at the endpoint, not the global handler.** `Register`'s return type already includes `Conflict<string>`, and the 409 message ("Player name is already taken.") is domain-specific — unlike the generic concurrency handler's "please retry." Keep the global `ConcurrencyConflictExceptionHandler` untouched.
2. **New `MartenExceptions.IsUniqueViolation(Exception)` helper** — walks the exception chain for an `Npgsql.PostgresException` with `SqlState == PostgresErrorCodes.UniqueViolation` ("23505"). Introduced here, reused by #46's seed guard. Isolated so the Npgsql-detection detail lives in one place.
3. **Keep the up-front pre-check.** It serves the common sequential case cheaply; the new catch only covers the race. Both return the same 409.

### Task 1: `IsUniqueViolation` helper
**Files:** Create `src/Voidforge.Api/Http/MartenExceptions.cs`

- [ ] **Step 1:** Write the helper. Verify the exact Npgsql type/namespace on first build.
```csharp
using Npgsql;

namespace Voidforge.Api.Http;

/// <summary>Detects the Postgres unique-constraint violation (23505) that Marten surfaces
/// when a duplicate hits a document unique index or primary key.</summary>
internal static class MartenExceptions
{
    public static bool IsUniqueViolation(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return true;
            }
        }

        return false;
    }
}
```
- [ ] **Step 2:** `dotnet build src/Voidforge.slnx -warnaserror` → clean. (`Npgsql` is transitive via Marten; if the using doesn't resolve, confirm the package reference — do not add a new one without checking.)

### Task 2: Catch the violation in the claim path
**Files:** Modify `src/Voidforge.Api/Endpoints/PlayerEndpoints.cs` (the `try/catch (ConcurrencyException)` around `SaveChangesAsync`, ~lines 133-144)

- [ ] **Step 1:** Add a `catch` for the unique violation that returns the claimed-name conflict. Shape (adapt to the actual `TryClaimHomeworld` return tuple/`ClaimOutcome`):
```csharp
catch (Exception ex) when (MartenExceptions.IsUniqueViolation(ex))
{
    // Lost the name race: another registration committed this Player.Name first.
    return (ClaimOutcome.NameTaken, null);
}
```
Add a `ClaimOutcome.NameTaken` case in `Register` that returns `TypedResults.Conflict("Player name is already taken.")`, matching the pre-check message. If `TryClaimHomeworld` currently returns only `Claimed/NoUncolonizedPlanets/LostRace`, extend the enum minimally; do **not** conflate name-taken with the planet `LostRace` retry (that would retry forever on a genuine duplicate name).
- [ ] **Step 2:** `dotnet build src/Voidforge.slnx -warnaserror` → clean.

### Task 3: Concurrent-race test
**Files:** Modify `src/Voidforge.Tests/Players/PlayerRegistrationTests.cs` (inline-scenario style — this file deliberately does **not** use the #62 shared helpers; `Register` is `[AllowAnonymous]`).

- [ ] **Step 1:** Add a test firing N (e.g. 8) concurrent same-name anonymous registrations, each reading status via `s.IgnoreStatusCode()` + `result.Context.Response.StatusCode`; assert exactly one 200 and the rest 409, and **zero** 500s.
```csharp
[Fact]
public async Task ConcurrentSameNameRegistrationsYieldOneWinnerAndConflicts()
{
    var name = $"Race_{Guid.NewGuid():N}";

    async Task<int> Register()
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new RegisterPlayerRequest(name)).ToUrl("/api/players/register");
            s.IgnoreStatusCode();
        });
        return result.Context.Response.StatusCode;
    }

    var codes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Register()));

    Assert.Equal(1, codes.Count(c => c == 200));
    Assert.Equal(7, codes.Count(c => c == 409));
    Assert.DoesNotContain(codes, c => c == 500);
}
```
- [ ] **Step 2:** `dotnet build src/Voidforge.slnx -warnaserror` → clean. (Do not run the suite locally — CI verifies.)

### Task 4: PR
- [ ] Commit each task (`fix:`/`test:` suffixed `(#45)`), push branch, open PR base `phase-5`, "Closes #45 (and #61, closed as its duplicate)". Self-merge on green CI.
