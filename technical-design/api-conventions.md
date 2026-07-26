# API Conventions

## Pagination

Every collection-returning endpoint uses one pagination contract (introduced in #29).

### Request
- `page` — int, default `1`, minimum `1`.
- `pageSize` — int, default `50`, minimum `1`, maximum `200`.

Policy: `page < 1` or `pageSize < 1` → `400 Bad Request`; `pageSize > 200` is **clamped** to `200` (not rejected).

### Response envelope — `PagedResponse<T>`
```json
{
  "items": [ /* T[] */ ],
  "page": 1,
  "pageSize": 50,
  "totalItems": 1234,
  "totalPages": 25,
  "hasPrevious": false,
  "hasNext": true
}
```
`totalPages`, `hasPrevious`, `hasNext` are computed from `totalItems`/`page`/`pageSize`.

### Deterministic order (correctness requirement)
Pagination is only correct over a deterministic sort. Every paginated endpoint MUST apply an explicit default order before paging (never rely on storage order) and document it. Current defaults:
- `GET /api/solar-systems` — by `Name`.
- `GET /api/fleets` — by `AssembledAt`, then `Id`.
- `GET /api/planets/{planetId}/fleets` — by `AssembledAt`, then `Id`.

### Producers (`Voidforge.Api/Pagination/PaginationExtensions.cs`)
- `IQueryable<T>.ToPagedResponseAsync(parameters, selector)` — document queries; wraps Marten `ToPagedListAsync` (items + count in one round-trip).
- `IReadOnlyList<T>.ToPagedResponse(parameters, selector)` — already-materialized aggregate child collections (e.g. the ship roster/queue in #27).

### Definition of done
Any new collection endpoint MUST adopt this contract. Bounded inline collections (a planet's fixed building slots, the energy block) stay embedded on their parent resource; unbounded ones (ship roster, shipyard queue) get dedicated paginated endpoints.

### Migration path to keyset (not built yet)
For large/append-heavy collections, an endpoint may later switch to keyset/cursor paging. The envelope is designed so that swap is non-breaking: clients that follow `hasNext` (rather than computing pages from `totalItems`) keep working. Build keyset per-endpoint only when its access pattern demands it.
