# Route Builder Cleanup Design

## Goal

Keep `/app` as the public authenticated route prefix, but define it once and route all frontend navigation, tests, and app-shell links through a small path helper.

## Scope

- Add a focused route path module for public paths and app-shell paths.
- Keep TanStack child routes relative under the `/app` shell route.
- Replace hardcoded `/app` strings in navigation, shell tests, login, not-found, and router tests.
- Collapse placeholder route definitions into route metadata instead of one component per placeholder screen.
- Add a shared test render helper for repeated `AppProviders` wrapping and session reset.
- Remove the no-op `Refresh` button from app chrome.
- Render the app-bar section label as text instead of a second page-level `h1`.

## Non-Goals

- Do not change public URLs.
- Do not add router basepath behavior.
- Do not redesign the drawer responsiveness in this slice.
- Do not add new dependencies.

## Testing

- Route-path helper tests own the exact `/app` contract.
- Router tests should verify real router behavior with built paths instead of mirroring a hardcoded route list.
- Layout and page tests should use path helpers where they need URLs.
- Full frontend verification remains `bun run test`, `bun run build`, `bun run lint`, and `bun run format:check`.
