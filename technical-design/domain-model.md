# Domain Model

## Overview

The domain uses two storage patterns from Marten:

1. **Event-sourced aggregates** (`Domain/`) — state rebuilt from an event stream, persisted as inline snapshots
2. **Documents** (`Documents/`) — plain JSONB documents, no event history

## Aggregates

### Player

- **File**: `Domain/Player.cs`
- **Events**: `PlayerRegistered(Name, RegisteredAt)`
- **Snapshot fields**: `Id`, `Name`, `RegisteredAt`
- **Unique index**: `Name` (DB-level enforcement)
- **Marten config**: `opts.Projections.Snapshot<Player>(SnapshotLifecycle.Inline)`

Created during registration via `session.Events.StartStream<Player>(...)`.

## Documents

### ApiKey

- **File**: `Documents/ApiKey.cs`
- **Fields**: `Id`, `HashedKey`, `PlayerId`, `CreatedAt`
- **Unique index**: `HashedKey`

Stores SHA-256 hashed API keys. The raw key is returned once at registration and never stored.

## Relationships

```
Registration (atomic transaction):
┌─────────────────────────────────────────┐
│  1. StartStream<Player>(playerId, event)│  → mt_events + mt_doc_player
│  2. Store(new ApiKey { ... })           │  → mt_doc_apikey
│  3. SaveChangesAsync()                  │  → single DB transaction
└─────────────────────────────────────────┘

Authentication:
  X-API-Key header → SHA-256 hash → query ApiKey doc → PlayerId → ClaimsPrincipal
```

## Adding New Aggregates

1. Create the aggregate class in `Domain/` with `Apply()` methods for each event
2. Create event records in `Domain/Events/`
3. Register inline snapshot in `Program.cs`: `opts.Projections.Snapshot<T>(SnapshotLifecycle.Inline)`
4. Add unique indexes if needed: `opts.Schema.For<T>().UniqueIndex(...)`
