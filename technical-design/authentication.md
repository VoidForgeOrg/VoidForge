# Authentication

## Current Implementation (MVP)

API key authentication via custom ASP.NET Core authentication handler.

### Flow

```
Registration (anonymous):
  POST /api/players/register { "name": "..." }
    → Generate key: "vf_" + 32 random bytes (hex) = 67 chars
    → Hash key: SHA-256
    → Store: ApiKey { HashedKey, PlayerId }
    → Start Player event stream
    → Return: { playerId, apiKey } (raw key shown once)

Authentication (every request):
  X-API-Key: vf_<64 hex chars>
    → SHA-256 hash the raw key
    → Query ApiKey document by HashedKey
    → Not found → 401
    → Found → ClaimsPrincipal with NameIdentifier = PlayerId
```

### Key Components

| File | Purpose |
|------|---------|
| `Auth/ApiKeyAuthenticationHandler.cs` | Reads header, hashes key, queries DB, creates principal |
| `Auth/ApiKeyAuthenticationDefaults.cs` | Scheme name (`ApiKey`), header name (`X-API-Key`) |
| `Auth/ApiKeyAuthenticationOptions.cs` | Options class (currently empty) |
| `Documents/ApiKey.cs` | Marten document for hashed key storage |

### Authorization Policy

- **Fallback policy**: `RequireAuthenticatedUser()` — all endpoints require auth by default
- **Anonymous endpoints**: `[AllowAnonymous]` on registration, health check, Swagger
- **Accessing player identity (D11, #74)**: the single primitive is `ClaimsPrincipal.PlayerId()` (`Auth/ClaimsPrincipalExtensions.cs`) — parses the `NameIdentifier` claim to a `Guid?` (null when absent/unparseable). This is the ONLY place that reads the claim; the former per-file `IsOwner`/`PlayerId` copies in Ship/Building/Fleet endpoints were removed.
- **Ownership checks**: each mutation endpoint resolves `principal.PlayerId()` then compares against the target aggregate's owner — `planet.IsOwnedBy(playerId)` for planet-scoped endpoints, `fleet.OwnerId` for fleet-scoped ones (Unload checks both fleet AND planet ownership). A null id or non-owner is a **403**; an unknown aggregate is a **404** (the 404-then-403 ordering is preserved per endpoint).

### Error Responses (D12, #74)

Every non-2xx response across the API is a **ProblemDetails** (RFC 7807) with a uniform shape: `AddProblemDetails(CustomizeProblemDetails …)` in `Program.cs` stamps `Instance` (request path) and a `traceId` extension; `title`/`type` default from the status. Endpoints emit errors via `TypedResults.Problem(detail, statusCode)` (each `Results<Ok<T>, ProblemHttpResult>`), and the `ConcurrencyConflictExceptionHandler` writes its 409 through the same `IProblemDetailsService`. Human-readable messages live in the `detail` field. Invalid enum query params (e.g. `?status=` on `GET /api/fleets`) return a 400 ProblemDetails rather than binding-failing or silently emptying (#63).

### Post-MVP

JWT (OAuth2/OIDC) for the official web client with external identity providers (Discord, Google). API keys remain available for third-party clients and bots. Both schemes coexist via ASP.NET Core multi-scheme authentication.
