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
│   ├── WorldGeneration/            # World seeding (hosted service + config)
│   └── Program.cs                  # App bootstrap (Marten, Wolverine, auth, middleware)
└── Voidforge.Tests/                # Integration + unit tests
    ├── Auth/                       # Auth-related tests
    ├── Planets/                    # Planet aggregate + endpoint tests
    ├── Players/                    # Player registration tests
    ├── AppFixture.cs               # Shared test host (Alba + PostgreSQL)
    └── IntegrationCollection.cs    # xUnit collection for shared fixture

frontend/
├── epoch-1-scope.md                # Epoch 1 frontend scope inventory
└── app/                            # Bun + Vite React frontend
    ├── package.json                # Frontend scripts and dependencies
    ├── bun.lock                    # Bun lockfile
    ├── vite.config.ts              # Vite + Vitest configuration
    ├── eslint.config.js            # ESLint flat config
    ├── prettier.config.js          # Prettier config
    └── src/
        ├── app/                    # Providers, theme, router
        ├── features/               # Feature-local state and UI modules
        ├── routes/                 # Route components
        ├── shared/api/             # API client, Zod schemas, query hooks
        └── test/                   # Vitest setup
```

## Folder Conventions

| Folder | Contains | Example |
|--------|----------|---------|
| `Domain/` | Event-sourced aggregates (Marten inline snapshots) | `Player.cs` |
| `Domain/Events/` | Domain event records | `PlayerRegistered.cs` |
| `Documents/` | Flat Marten documents (no event stream) | `ApiKey.cs` |
| `Endpoints/` | Wolverine HTTP endpoint classes + request/response DTOs | `PlayerEndpoints.cs`, `RegisterPlayerRequest.cs` |
| `Auth/` | Authentication handler, options, defaults | `ApiKeyAuthenticationHandler.cs` |
| `WorldGeneration/` | World seeding hosted service + configuration | `WorldSeeder.cs`, `WorldGenOptions.cs` |
| `frontend/app/src/app/` | React application providers, theme, and TanStack Router setup | `AppProviders.tsx`, `router.tsx` |
| `frontend/app/src/routes/` | Page-level route components | `LoginPage.tsx`, `EmpireOverviewPage.tsx` |
| `frontend/app/src/shared/api/` | Fetch client, Zod response schemas, and TanStack Query hooks | `client.ts`, `schemas.ts`, `hooks.ts` |
| `frontend/app/src/features/` | Feature-local frontend state and UI modules | `auth/sessionStore.ts` |

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

## Frontend Commands

Run frontend commands from `frontend/app/`:

- `bun install` - restore frontend dependencies
- `bun run dev` - start Vite dev server
- `bun run build` - run TypeScript typecheck and production build
- `bun run lint` - run ESLint
- `bun run format:check` - check Prettier formatting
- `bun run test` - run Vitest

The frontend currently uses `VITE_API_BASE_URL` when set, otherwise it calls `http://localhost:5000`.
