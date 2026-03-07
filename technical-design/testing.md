# Testing

## Stack

- **xUnit** — test framework
- **Alba** — integration testing for ASP.NET Core (HTTP scenario testing)
- **coverlet** — code coverage (70% line threshold)

## Test Host Setup

### Critical Pattern

`AppFixture` uses the env var approach — set `ConnectionStrings__Marten` before calling `AlbaHost.For<Program>()` with no arguments.

```csharp
Environment.SetEnvironmentVariable("ConnectionStrings__Marten", connStr);
Host = await AlbaHost.For<Program>();
```

**Do NOT** use `AlbaHost.For<Program>(Action<IWebHostBuilder>)` — it triggers an `ObjectDisposedException` in .NET 9 due to a `WithWebHostBuilder` + `RunJasperFxCommands` disposal race.

### Shared Fixture

All integration tests share a single `AppFixture` via xUnit collection:

```csharp
[Collection(IntegrationCollection.Name)]
public sealed class MyTests(AppFixture fixture)
{
    private readonly IAlbaHost _host = fixture.Host;
}
```

This avoids booting the app per test class (Marten schema migration is slow).

## Known Pitfalls

### Do Not Dispose DI-Owned IDocumentStore

```csharp
// WRONG — disposes the singleton, kills Npgsql for all subsequent tests
await using var store = _host.Services.GetRequiredService<IDocumentStore>();

// CORRECT — DI owns the lifetime
var store = _host.Services.GetRequiredService<IDocumentStore>();
await using var session = store.LightweightSession();
```

### Test Database

- DB name: `voidforge_test`
- Default connection: `Host=localhost;Port=5432;Database=voidforge_test;Username=postgres;Password=voidforge_dev`
- PostgreSQL runs via Docker (container: `dockerfiles-postgres-1`)
- Reset between full runs: drop and recreate the DB if schema changes

### Wolverine Teardown Hang

After all tests pass, Wolverine's durability agent may retry connections to a disposed Npgsql data source, causing the test process to hang during teardown. This is cosmetic — all tests complete successfully. Use `timeout 120 dotnet test ...` in scripts if needed.

## Coverage

Enforced via `src/coverlet.runsettings`:
- Threshold: 70% line coverage
- Excludes: `[Voidforge.Tests]*`
- Format: cobertura
- Applied in quality gate and CI
