# Technical Architecture: Initial Stack & System Design

## Technology Stack Summary

| Concern | Decision | Package / Tool |
|---------|----------|----------------|
| Language & Runtime | C# / .NET 9 | `net9.0` TFM |
| Application Framework | Critter Stack | Wolverine 5.x + Marten 8.x |
| HTTP Endpoints | Wolverine.HTTP | `WolverineFx.Http`, `WolverineFx.Http.Marten` |
| Event Sourcing & Documents | Marten | `Marten` (PostgreSQL JSONB) |
| Messaging & Scheduling | Wolverine | `WolverineFx`, `WolverineFx.Marten` |
| Database | PostgreSQL 16+ | Single instance for MVP |
| API Style | REST + OpenAPI | Swashbuckle |
| Authentication | API keys | Custom middleware (MVP) |
| Real-time Push | Polling | SSE/SignalR post-MVP |
| Deployment | Docker Compose | App container + PostgreSQL |
| CI/CD | GitHub Actions | Build, test, lint |

> **Upgrade path:** Wolverine 6.0 and Marten 9.0 (targeting .NET 10 LTS) are expected Q2–Q3 2026. Plan to upgrade when available — the migration should be straightforward as these releases focus on AOT compliance and cold-start optimization rather than API changes.

---

## 1. Language & Runtime

### Decision

**C# on .NET 9** using the **Critter Stack** — Wolverine 5.x (message handling, scheduling, HTTP) and Marten 8.x (event sourcing, document storage on PostgreSQL).

### Rationale

The Critter Stack was selected over Orleans, Akka.NET, and plain ASP.NET Core after evaluating all candidates against Voidforge's requirements (see `technical-design/reaseach-from-opus.md` for the full comparison). The key factors:

- **Native lazy calculation fit.** Marten's inline snapshot projections *are* the checkpoint-and-rate model described in `game-design/engine.md`. `FetchForWriting<Planet>()` loads the latest snapshot; `Apply()` methods update checkpoint values when events fire. No impedance mismatch.
- **Event sourcing built in.** Planets, fleets, and players are naturally modeled as event streams. Marten provides projections (inline, async, live), stream versioning, and optimistic concurrency out of the box.
- **Durable scheduled messaging.** Fleet arrivals, build completions, and resource depletions are deterministic future events. Wolverine's `ScheduleAsync()` persists these to PostgreSQL in the same transaction as the triggering event — surviving restarts without additional infrastructure.
- **Single infrastructure dependency.** PostgreSQL is the only external service. Marten uses it as both document store and event store; Wolverine uses it for message persistence, scheduling, and saga storage. No RabbitMQ, no Redis, no separate event store.
- **MIT licensed.** Both libraries are MIT-licensed, maintained by JasperFx Software. MassTransit moved to commercial licensing in 2025; NServiceBus is commercially licensed. The Critter Stack is the only full-featured open-source option in this space.
- **Developer experience.** Convention-based handlers (no interfaces), compile-time code generation, auto-provisioned schemas (`docker compose up` and code), pure-function handlers testable without mocks, and comprehensive LLM documentation files (`wolverinefx.net/llms-full.txt`, `martendb.io/llms-full.txt`).

### Trade-offs

- **No gaming precedent.** No known game projects use the Critter Stack. We are pioneering this use case.
- **Smaller community.** Wolverine (~2,000 GitHub stars) and Marten (~2,800) have smaller communities than Orleans (~10,700) or EF Core. Less StackOverflow coverage.
- **PostgreSQL is the ceiling.** Every entity access hits the database (no in-memory hot path like Orleans grains). At MVP scale (< 100 players), this is not a concern — strategy game access patterns are infrequent per-entity.
- **Escape hatch:** If profiling reveals database round-trips as a bottleneck at scale, hot-path entities can be migrated to Orleans grains while keeping Marten as the event store. The event stream data model transfers cleanly.

---

## 2. API Layer

### Decision

**REST API via Wolverine.HTTP** with **OpenAPI documentation** (Swashbuckle). Authentication via **API keys** for MVP.

### Protocol: REST via Wolverine.HTTP

Wolverine.HTTP provides HTTP endpoint discovery with the same conventions as message handlers. Endpoints are plain C# methods decorated with `[WolverineGet]`, `[WolverinePost]`, etc. The aggregate handler workflow integrates directly — a single endpoint method can load an event-sourced aggregate, validate, emit events, and return an HTTP response:

```csharp
[WolverinePost("/api/planets/{planetId}/buildings")]
public static (BuildingResponse, IEnumerable<object>) Post(
    StartBuilding command,
    [WriteAggregate] Planet planet)
{
    // Validate slots, resources, etc.
    var started = new BuildingStarted(command.BuildingType, command.SlotIndex);
    return (new BuildingResponse(planet.Id), new object[] { started });
}
```

Wolverine generates all the plumbing: load aggregate via `FetchForWriting`, append returned events, commit via outbox, return HTTP response. The handler is a pure function — trivially unit-testable.

**Why not GraphQL?** GraphQL adds a dependency (Hot Chocolate) and complexity without clear benefit for MVP. The game's query patterns are straightforward (get planet state, list fleets, check score). REST with OpenAPI is simpler, well-understood, and sufficient. GraphQL can be added post-MVP if client developers request it.

### OpenAPI

Wolverine.HTTP generates OpenAPI metadata from method signatures automatically. Combined with Swashbuckle, this provides interactive API documentation at `/swagger`:

```csharp
builder.Services.AddSwaggerGen();
// ...
app.UseSwagger();
app.UseSwaggerUI();
```

### Authentication: API Keys (MVP)

Voidforge is designed as a headless engine where players can build their own clients. API keys are the simplest auth model for this:

- Each player receives an API key on registration.
- Keys are sent via `X-API-Key` header or `Authorization: ApiKey <key>` header.
- Validated via custom ASP.NET Core authentication middleware.
- Keys are hashed (SHA-256) and stored in the database; the plaintext is shown once on creation.

This integrates with standard ASP.NET Core auth, so Wolverine endpoints use `[Authorize]` as normal:

```csharp
app.MapWolverineEndpoints(opts => opts.RequireAuthorizeOnAll());
```

**Post-MVP:** Add JWT (OAuth2/OIDC) for the official web client with support for external identity providers (Discord, Google). API keys remain available for third-party clients and bots. Both auth schemes coexist via ASP.NET Core's multi-scheme authentication.

### Real-time Push: Polling Only (MVP)

For MVP, clients poll the API for state updates. The lazy calculation model makes this natural — every query returns the current computed state regardless of when it's called.

**Post-MVP:** Add Server-Sent Events (SSE) or SignalR for push notifications (building completed, fleet arrived, resource depleted). Wolverine has first-class SignalR integration via `WolverineFx.SignalR`. Event forwarding from Marten to Wolverine handlers can trigger push notifications:

```csharp
// Post-MVP: forward events to SignalR
.IntegrateWithWolverine()
.EventForwardingToWolverine(opts =>
{
    opts.SubscribeToEvent<BuildingCompleted>()
        .TransformedTo(e => new BuildingCompletedNotification(e.StreamId));
});
```

---

## 3. Database

### Decision

**PostgreSQL 16+** (single instance for MVP). **Marten** as the application-level data access layer, using PostgreSQL as both document database and event store via JSONB.

### Why PostgreSQL

PostgreSQL is the only infrastructure dependency. Marten leverages JSONB columns to store .NET objects as documents (tables prefixed `mt_doc_`) and events (in `mt_events` + `mt_streams`). Wolverine stores message envelopes, scheduled messages, and saga state in the same database. This means:

- One database to back up, monitor, and scale.
- ACID transactions span game state, events, and outgoing messages.
- No ORM mapping configuration — Marten auto-generates schema from C# types.
- Schema auto-provisioned on startup in development; migration scripts via Weasel CLI for production.

### Schema Strategy

#### Event Streams (Marten Event Store)

Each core game entity is a **Marten event stream**:

| Entity | Stream Identity | Example Events |
|--------|----------------|----------------|
| Planet | `Guid` (PlanetId) | `BuildingStarted`, `BuildingCompleted`, `ResourcesDepleted`, `DrillHalted`, `EnergyRebalanced`, `ProductionRateChanged` |
| Fleet Mission | `Guid` (FleetMissionId) | Modeled as a **Wolverine Saga** rather than a raw event stream (see Section 4) |
| Player | `Guid` (PlayerId) | `PlayerRegistered`, `ApiKeyGenerated` |

Events are stored in `mt_events` (JSONB data + metadata) and `mt_streams` (stream state). Using `EventAppendMode.Quick` for ~40-50% better write performance (acceptable trade-off: `IEvent.Version` unavailable in inline projections, but we use `IRevisioned` on aggregates instead).

#### Inline Snapshot Projections (Read Models)

Each event stream has an **inline snapshot projection** — a persisted document that represents the current aggregate state, updated atomically within the same transaction as event appends:

```csharp
opts.Projections.Snapshot<Planet>(SnapshotLifecycle.Inline);
opts.Projections.Snapshot<Player>(SnapshotLifecycle.Inline);
```

These snapshots serve as both the read model (API queries load them directly) and the checkpoint for lazy calculation (see Section 5).

#### Document Storage

Non-event-sourced data uses Marten's document storage:

| Document | Purpose |
|----------|---------|
| `SolarSystem` | Static solar system data (coordinates, star type) |
| `ApiKey` | Hashed API keys for authentication |
| `Leaderboard` | Cached score rankings (async projection) |

#### Wolverine Tables

Wolverine auto-provisions its own tables alongside Marten:

- `wolverine_incoming_envelopes` — inbox for durable message handling
- `wolverine_outgoing_envelopes` — outbox for scheduled/outgoing messages
- `wolverine_dead_letters` — failed messages for inspection/replay

### Numeric Precision

All resource quantities use **C# `decimal`** type to avoid floating-point drift over long game sessions:

```csharp
public class ResourcePool
{
    public decimal CheckpointValue { get; set; }
    public decimal Rate { get; set; }          // units per second
    public decimal StorageCapacity { get; set; }
    public DateTimeOffset CheckpointTime { get; set; }
}
```

PostgreSQL's JSONB stores `decimal` values as JSON numbers. System.Text.Json (Marten's default serializer) preserves `decimal` precision through serialization round-trips. For any query-optimized fields, duplicated fields can specify `pgType: "numeric"` to ensure PostgreSQL-side precision.

### Connection Management

Npgsql's built-in connection pooling is sufficient for MVP (no PgBouncer needed). Marten sessions return connections to the pool between operations. Configuration:

```csharp
builder.Services.AddMarten(opts =>
{
    opts.Connection(builder.Configuration.GetConnectionString("Marten")!);
    opts.Events.AppendMode = EventAppendMode.Quick;
    opts.Projections.Snapshot<Planet>(SnapshotLifecycle.Inline);
    opts.Projections.Snapshot<Player>(SnapshotLifecycle.Inline);
    opts.Events.UseIdentityMapForAggregates = true;
})
.UseLightweightSessions()
.IntegrateWithWolverine();
```

**Post-MVP scaling:** Add Npgsql multi-host connection strings to route `IQuerySession` reads to replicas while writes go to the primary.

---

## 4. Event Queue

### Decision

**Wolverine's built-in durable scheduled messaging**, backed by PostgreSQL via the Marten integration. No separate message broker or custom scheduler for MVP.

### How It Works

When a game action creates a deterministic future event, the handler schedules a message using Wolverine's `ScheduleAsync()` or by returning a `TimeoutMessage`. The scheduled message is persisted to the `wolverine_outgoing_envelopes` table **within the same database transaction** as the triggering event — this is the transactional outbox guarantee.

A background `DurabilityAgent` polls the outbox for due messages and dispatches them. The default polling interval is **5 seconds** — acceptable for a strategy game where events happen on the scale of minutes to hours.

### Scheduled Event Types

| Game Event | Trigger | Scheduled Message |
|------------|---------|-------------------|
| Building completion | Player starts construction | `BuildingCompleted { PlanetId, SlotIndex }` scheduled at `now + buildDuration` |
| Ship completion | Shipyard starts building | `ShipCompleted { PlanetId, ShipyardId, ShipType }` scheduled at completion time |
| Resource depletion | Drill starts mining | `ResourceDepleted { PlanetId, ResourceType }` scheduled at `poolSize / extractionRate` |
| Fleet arrival | Fleet departs | Handled by Wolverine Saga `TimeoutMessage` (see below) |
| Storage full | Production rate changes | `StorageFull { PlanetId, ResourceType }` scheduled at `(capacity - current) / rate` |

### Fleet Missions as Wolverine Sagas

Fleet missions have a lifecycle (departure → transit → arrival → post-mission) that maps naturally to a **Wolverine Saga**. The saga persists its state as a Marten document and uses `TimeoutMessage` for the arrival event:

```csharp
public record LaunchFleet(Guid FleetMissionId, Guid OriginId, Guid DestinationId,
    MissionType Type, decimal TravelSeconds);

public record FleetArrived(Guid FleetMissionId) : TimeoutMessage(/* set dynamically */);

public class FleetMission : Saga
{
    public Guid Id { get; set; }
    public Guid OriginId { get; set; }
    public Guid DestinationId { get; set; }
    public MissionType Type { get; set; }
    public DateTimeOffset DepartureTime { get; set; }
    public DateTimeOffset ArrivalTime { get; set; }

    public static (FleetMission, OutgoingMessages) Start(LaunchFleet cmd)
    {
        var arrival = DateTimeOffset.UtcNow.AddSeconds((double)cmd.TravelSeconds);
        var saga = new FleetMission
        {
            Id = cmd.FleetMissionId,
            OriginId = cmd.OriginId,
            DestinationId = cmd.DestinationId,
            Type = cmd.Type,
            DepartureTime = DateTimeOffset.UtcNow,
            ArrivalTime = arrival
        };
        var messages = new OutgoingMessages();
        messages.Schedule(new FleetArrived(cmd.FleetMissionId), arrival);
        return (saga, messages);
    }

    public void Handle(FleetArrived arrived, IMessageBus bus)
    {
        // Dispatch arrival logic based on mission type
        bus.PublishAsync(new ExecuteArrival(Id, DestinationId, Type));
        MarkCompleted();
    }

    // Required: handle arrival after saga was already completed (e.g., cancelled fleet)
    public static void NotFound(FleetArrived arrived) { /* no-op */ }
}
```

### Cancellation and Invalidation

When a scheduled event becomes invalid (e.g., building cancelled, fleet recalled):

- **Fleet cancellation:** The saga handles a `CancelFleet` message, creates a return journey saga, and calls `MarkCompleted()`. If the original `FleetArrived` message fires after completion, the `NotFound` handler safely ignores it.
- **Building/ship cancellation:** The handler appends a cancellation event (e.g., `BuildingCancelled`). When the `BuildingCompleted` message fires, the handler loads the planet aggregate, checks the building's status, and no-ops if cancelled.
- **Resource depletion rescheduling:** When production rates change (new drill, energy rebalance), the handler calculates the new depletion time and schedules a new message. The old message fires but the handler detects the stale checkpoint and ignores it.

The pattern is **"schedule optimistically, validate on arrival"** — it's simpler and more reliable than trying to cancel specific scheduled messages in the outbox.

### Trade-offs

- **5-second polling granularity.** Events may fire up to 5 seconds late. For a strategy game where events are hours/days apart, this is negligible. Configurable via `opts.Durability.ScheduledJobPollingTime`.
- **Validate-on-arrival overhead.** Stale scheduled messages still fire and load aggregates, only to no-op. At MVP scale this is trivial. If it becomes a concern, Wolverine supports message filtering at the transport level.
- **Post-MVP optimization.** For higher volumes, Wolverine's PostgreSQL transport provides dedicated queue tables with competing-consumer semantics. Alternatively, add RabbitMQ as transport — an additive config change, no handler code changes.

---

## 5. Lazy Calculation Engine

### Decision

Implement lazy calculation using **Marten inline snapshot projections** with a **checkpoint-and-rate model**. All resource values are computed on demand from the last checkpoint using `decimal` arithmetic.

### Core Formula

```
current_value = min(checkpoint_value + rate × elapsed_seconds, storage_capacity)
```

Where:
- `checkpoint_value` — the known resource quantity at the last checkpoint
- `rate` — net production/consumption rate in units per second (can be negative)
- `elapsed_seconds` — time since the last checkpoint
- `storage_capacity` — hard cap per resource type

### Read Path (Querying Current State)

When a player queries a planet's state, the API:

1. Loads the planet's inline snapshot (a `Planet` document persisted by Marten).
2. Computes current resource values from checkpoint data + elapsed time.
3. Returns the computed state.

No events are appended. No checkpoint is created. This is a pure read:

```csharp
public class Planet
{
    public Guid Id { get; set; }
    public Dictionary<ResourceType, ResourcePool> Resources { get; set; } = new();

    public decimal GetCurrentResource(ResourceType type, DateTimeOffset now)
    {
        var pool = Resources[type];
        var elapsed = (decimal)(now - pool.CheckpointTime).TotalSeconds;
        var computed = pool.CheckpointValue + pool.Rate * elapsed;
        return Math.Clamp(computed, 0m, pool.StorageCapacity);
    }
}
```

### Write Path (State-Changing Events)

When an event fires (building completes, resource depletes, player issues a command):

1. Load the planet aggregate via `FetchForWriting<Planet>(planetId)`.
2. **Checkpoint** all affected resource pools — compute their current values and save as new checkpoint values.
3. Update rates based on the state change.
4. Append events describing what changed.
5. Schedule any new future events (depletion deadlines, etc.).
6. `SaveChangesAsync()` commits everything atomically.

```csharp
[AggregateHandler]
public static (Events, OutgoingMessages) Handle(BuildingCompleted cmd, Planet planet)
{
    var events = new Events();
    var messages = new OutgoingMessages();
    var now = DateTimeOffset.UtcNow;

    // Checkpoint all resources at current time
    planet.CheckpointAllResources(now);

    // Building comes online: update energy balance and production rates
    var building = planet.Buildings[cmd.SlotIndex];
    events += new BuildingBecameOperational(cmd.PlanetId, cmd.SlotIndex, building.Type);

    // Recalculate energy balance
    var energyEvents = planet.RecalculateEnergy();
    foreach (var e in energyEvents) events += e;

    // Recalculate production rates (may change due to energy)
    var rateEvents = planet.RecalculateProductionRates();
    foreach (var e in rateEvents) events += e;

    // Schedule new depletion events based on updated rates
    foreach (var depletion in planet.CalculateDepletionDeadlines(now))
        messages.Schedule(depletion.Message, depletion.Time);

    return (events, messages);
}
```

### How the Snapshot Stays in Sync

The `Planet` aggregate is registered as an inline snapshot:

```csharp
opts.Projections.Snapshot<Planet>(SnapshotLifecycle.Inline);
```

Marten calls the aggregate's `Apply()` methods for each event within `SaveChangesAsync()`, then persists the updated snapshot document atomically with the events. The snapshot always reflects the latest checkpoint state.

```csharp
public class Planet
{
    // Marten calls these during SaveChangesAsync
    public void Apply(ProductionRateChanged e)
    {
        var pool = Resources[e.Resource];
        pool.CheckpointValue = e.NewCheckpointValue;
        pool.Rate = e.NewRate;
        pool.CheckpointTime = e.CheckpointTime;
    }

    public void Apply(BuildingBecameOperational e)
    {
        Buildings[e.SlotIndex].Status = BuildingStatus.Operational;
    }
}
```

### Precision Strategy

- **`decimal` for all resource quantities, rates, and capacities.** This avoids IEEE 754 floating-point drift over months of continuous gameplay.
- **`DateTimeOffset` for checkpoint timestamps.** Elapsed time is computed as `(now - checkpoint).TotalSeconds` and cast to `decimal` for the multiplication.
- **Clamping.** Values are clamped to `[0, storageCapacity]` on read to handle edge cases (negative after rounding, overshoot).

### Trade-offs

- **No background processing.** The engine does zero work between player queries. This is a feature, not a limitation — it means a server with 100,000 planets but only 50 active players uses minimal resources.
- **Checkpoint granularity.** State is only "true" at checkpoint boundaries. Between checkpoints, values are interpolated. This is by design per `game-design/engine.md`.
- **Decimal performance.** `decimal` arithmetic is ~10x slower than `double`. At MVP scale with < 100 players, this is irrelevant. If profiling ever shows it as a bottleneck, rates can be converted to scaled integers.

---

## 6. Cascading Event Resolution

### Decision

Cascading events are resolved **within a single command handler** that computes all downstream effects and appends all resulting events **atomically in one `SaveChangesAsync()` call**. No multi-step message chains for cascades.

### Why Single-Handler Atomicity

From `game-design/engine.md`: *"The engine must resolve these dependency chains within a single checkpoint to maintain a consistent state."*

Marten's inline projections guarantee that the planet snapshot is updated atomically with all emitted events. If any part of the cascade fails, the entire transaction rolls back — no partial state corruption.

### Cascade Examples (from game design)

**Ore pool depletes:**
```
Ore pool hits zero
  → Drill halts (consumes 5% energy instead of full)
    → Energy freed up
      → If planet was overloaded: productivity recovers for all buildings
        → Production rates change for all buildings
          → New depletion/storage-full deadlines calculated
```

**New building comes online:**
```
Building completed
  → Energy consumption increases
    → If consumption > generation: planet becomes overloaded
      → All building productivity drops proportionally
        → Production rates change
          → New depletion deadlines recalculated
```

### Resolution Algorithm

The handler follows a deterministic resolution order:

1. **Apply the triggering event** (depletion, building completion, demolition).
2. **Update building states** — halt/resume buildings based on resource/energy availability.
3. **Recalculate energy balance** — sum generation vs. consumption, determine overload ratio.
4. **Recalculate production rates** — apply energy efficiency to all building throughputs, apply resource distribution (even split among competing consumers).
5. **Checkpoint all resource pools** — compute current values at `now`, store as new checkpoint values with updated rates.
6. **Schedule new future events** — calculate new depletion deadlines, storage-full times based on updated rates.

Each step emits domain events (`DrillHalted`, `EnergyRebalanced`, `ProductionRateChanged`, etc.) that the inline projection applies to keep the snapshot current.

### No Cycle Risk in MVP

The MVP resource chain is linear:

```
Iron Ore → Iron Ingots → Ships / Buildings
Energy: Generator → all buildings
```

There are no circular dependencies. A drill can't consume ingots, and a generator doesn't consume ore. The resolution order (resource availability → energy balance → production rates) is always safe.

**Post-MVP consideration:** If branching resource trees or fuel-consuming generators introduce cycles, the resolution algorithm will need a topological sort or fixed-point iteration. Document this as a known future concern.

### Code Sketch

```csharp
[AggregateHandler]
public static (Events, OutgoingMessages) Handle(ResourceDepleted cmd, Planet planet)
{
    var events = new Events();
    var messages = new OutgoingMessages();
    var now = DateTimeOffset.UtcNow;

    // Step 1: Checkpoint current resource values
    planet.CheckpointAllResources(now);

    // Step 2: Mark resource as depleted, halt dependent buildings
    events += new ResourcePoolExhausted(cmd.PlanetId, cmd.ResourceType);

    foreach (var building in planet.GetBuildingsDependingOn(cmd.ResourceType))
    {
        events += new BuildingHalted(cmd.PlanetId, building.Id, HaltReason.NoInput);
    }

    // Step 3: Recalculate energy (halted buildings use 5% energy)
    var energyDelta = planet.RecalculateEnergy();
    foreach (var e in energyDelta) events += e;

    // Step 4: Recalculate production rates
    var rateChanges = planet.RecalculateProductionRates();
    foreach (var e in rateChanges) events += e;

    // Step 5: Schedule new depletion deadlines
    foreach (var deadline in planet.CalculateDepletionDeadlines(now))
        messages.Schedule(deadline.Message, deadline.Time);

    return (events, messages);
}
```

---

## 7. Deployment & Infrastructure

### Decision

**Docker Compose** for MVP deployment. Single application container + PostgreSQL. `DurabilityMode.Solo` for Wolverine (no multi-node coordination needed).

### MVP Docker Compose

```yaml
services:
  postgres:
    image: postgres:16
    ports:
      - "5432:5432"
    environment:
      POSTGRES_USER: voidforge
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
      POSTGRES_DB: voidforge
    volumes:
      - pgdata:/var/lib/postgresql/data

  app:
    build: .
    ports:
      - "5000:8080"
    environment:
      ConnectionStrings__Marten: "Host=postgres;Database=voidforge;Username=voidforge;Password=${POSTGRES_PASSWORD}"
    depends_on:
      - postgres

volumes:
  pgdata:
```

### Application Bootstrap

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Host.ApplyJasperFxExtensions();

builder.Services.AddMarten(opts =>
{
    opts.Connection(builder.Configuration.GetConnectionString("Marten")!);
    opts.DatabaseSchemaName = "voidforge";
    opts.Events.AppendMode = EventAppendMode.Quick;
    opts.Projections.Snapshot<Planet>(SnapshotLifecycle.Inline);
    opts.Projections.Snapshot<Player>(SnapshotLifecycle.Inline);
    opts.Events.UseIdentityMapForAggregates = true;
})
.UseLightweightSessions()
.IntegrateWithWolverine();

builder.Host.UseWolverine(opts =>
{
    opts.Policies.AutoApplyTransactions();
    opts.Durability.Mode = DurabilityMode.Solo;
});

builder.Services.AddWolverineHttp();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();
app.MapWolverineEndpoints();
return await app.RunJasperFxCommands(args);
```

### CI/CD: GitHub Actions

Minimal pipeline for MVP:

1. **Build** — `dotnet build`
2. **Test** — `dotnet test` (unit + integration via Alba with PostgreSQL in a service container)
3. **Lint** — Format check via `dotnet format --verify-no-changes`

Integration tests use Alba with `DisableAllExternalWolverineTransports()` and `RunWolverineInSoloMode()` for fast, isolated testing against a real PostgreSQL instance.

### Schema Management

- **Development:** `AutoCreateSchemaObjects = AutoCreate.All` — Marten and Wolverine auto-provision all tables on startup.
- **Production:** Set `AutoCreate.None`. Generate migration scripts via `dotnet run -- marten-patch` and apply them as part of the deployment pipeline.

### Post-MVP Scaling Path

When the game outgrows single-instance deployment:

| Scale Concern | Solution | Effort |
|---------------|----------|--------|
| More app instances | Switch `DurabilityMode.Solo` → `Balanced`, Wolverine handles leader election and work distribution via PostgreSQL advisory locks | Config change |
| Read-heavy load | Add PostgreSQL read replicas, configure Npgsql multi-host connection string | Infrastructure + config |
| Connection pooling | Add PgBouncer (session mode to preserve advisory locks) or rely on Npgsql built-in pooling | Infrastructure |
| High message volume | Swap PostgreSQL message transport for RabbitMQ (`WolverineFx.RabbitMQ`) | Additive config, no handler changes |
| Hot-path entities | Migrate frequently-accessed planets to Orleans grains, keep Marten as event store | Significant refactor |
| Real-time push | Add SignalR hub via `WolverineFx.SignalR`, forward events from Marten to connected clients | New feature |
| Multi-region | Marten database-per-tenant with tenant = region | Architecture change |

The key insight: the Critter Stack's scaling path is **additive**. Each upgrade is a configuration or infrastructure change — handler code remains unchanged. The most drastic change (Orleans grains) is an escape hatch for extreme scale that is unlikely to be needed for a strategy MMO.
