# Project Structure

## Solution Layout

```
src/
├── Voidforge.slnx                 # Solution file
├── Directory.Build.props           # Centralized MSBuild properties (all projects inherit)
├── .editorconfig                   # Formatting + analyzer severity rules
├── coverlet.runsettings            # 70% line coverage threshold
├── Voidforge.Api/                  # Main application
│   ├── Auth/                       # Authentication handler + defaults
│   ├── Documents/                  # Flat Marten documents (non-event-sourced)
│   ├── Domain/                     # Event-sourced aggregates
│   │   └── Events/                 # Domain events
│   ├── Endpoints/                  # Wolverine HTTP endpoints + DTOs
│   └── Program.cs                  # App bootstrap (Marten, Wolverine, auth, middleware)
└── Voidforge.Tests/                # Integration + unit tests
    ├── Auth/                       # Auth-related tests
    ├── Players/                    # Player registration tests
    ├── AppFixture.cs               # Shared test host (Alba + PostgreSQL)
    └── IntegrationCollection.cs    # xUnit collection for shared fixture
```

## Folder Conventions

| Folder | Contains | Example |
|--------|----------|---------|
| `Domain/` | Event-sourced aggregates (Marten inline snapshots) | `Player.cs` |
| `Domain/Events/` | Domain event records | `PlayerRegistered.cs` |
| `Documents/` | Flat Marten documents (no event stream) | `ApiKey.cs` |
| `Endpoints/` | Wolverine HTTP endpoint classes + request/response DTOs | `PlayerEndpoints.cs`, `RegisterPlayerRequest.cs` |
| `Auth/` | Authentication handler, options, defaults | `ApiKeyAuthenticationHandler.cs` |

**Rule**: One public type per file (enforced by Meziantou.Analyzer MA0048).

## Build Configuration

`Directory.Build.props` applies to all projects:
- `TargetFramework`: net9.0
- `Nullable`: enable
- `TreatWarningsAsErrors`: true
- `AnalysisLevel`: latest-Recommended
- `EnforceCodeStyleInBuild`: true
- Analyzers: Roslynator.Analyzers, Meziantou.Analyzer

## Test Conventions

- Integration tests use `[Collection(IntegrationCollection.Name)]` to share a single `AppFixture` host
- `AppFixture` boots the app via `AlbaHost.For<Program>()` with env var for DB connection
- Test DB: `voidforge_test` on localhost PostgreSQL
- Each test class receives the fixture via constructor injection
- Test names describe behavior: `RegisterReturnsPlayerIdAndApiKey`, `MeWithoutAuthReturns401`
