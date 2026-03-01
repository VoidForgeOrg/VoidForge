# Phase 1 — Infrastructure

**Goal:** A running .NET 9 application with PostgreSQL, authentication, and CI. No game logic — just the skeleton that everything plugs into.

## Issues

### 1. Project scaffolding & Docker Compose setup
**Labels:** `infra`

Set up the solution structure and infrastructure so the app boots and connects to PostgreSQL.

**Scope:**
- .NET 9 solution with a web API project (e.g., `src/Voidforge.Api`)
- NuGet dependencies: `WolverineFx`, `WolverineFx.Http`, `WolverineFx.Marten`, `Marten`, `Swashbuckle`
- `docker-compose.yml` with PostgreSQL 16 and the app container
- Application bootstrap: Marten config, Wolverine config (`DurabilityMode.Solo`), Swagger UI
- Health check endpoint (`GET /health`) confirming database connectivity
- `.gitignore`, `Directory.Build.props`, `global.json` for .NET 9 SDK pinning

**Depends on:** Nothing.

---

### 2. CI pipeline with GitHub Actions
**Labels:** `infra`

Automated build, test, and format checks on every push/PR.

**Scope:**
- GitHub Actions workflow: `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes`
- PostgreSQL service container for integration tests
- Test project (e.g., `tests/Voidforge.Tests`) with Alba for integration testing
- Smoke test: app starts, health endpoint returns 200

**Depends on:** #1

---

### 3. API key authentication middleware
**Labels:** `infra`

The auth infrastructure — middleware, key storage, validation. No registration flow yet (that's Phase 2), just the plumbing.

**Scope:**
- `ApiKey` Marten document: hashed key (SHA-256), associated player ID, created timestamp
- Custom ASP.NET Core authentication handler validating `X-API-Key` header
- Key generation logic (secure random, shown once on creation)
- `[Authorize]` applied globally via `RequireAuthorizeOnAll()`, with `[AllowAnonymous]` on health
- Integration test: seed a test key, request without key → 401, request with valid key → 200

**Depends on:** #1

---

## Phase Completion

- `docker compose up` starts the app and PostgreSQL
- `/health` returns 200
- Swagger UI accessible at `/swagger`
- Unauthenticated requests return 401
- CI passes on GitHub
