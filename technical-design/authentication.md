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
- **Accessing player identity**: `ClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier)` returns the PlayerId as string

### Post-MVP

JWT (OAuth2/OIDC) for the official web client with external identity providers (Discord, Google). API keys remain available for third-party clients and bots. Both schemes coexist via ASP.NET Core multi-scheme authentication.
