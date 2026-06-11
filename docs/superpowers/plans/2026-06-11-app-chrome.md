# App Chrome Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a fixed MUI AppBar and permanent mini variant Drawer shell around the authenticated frontend routes.

**Architecture:** Create route-independent navigation metadata and a reusable `AppShellLayout` component. Keep `AppShell` responsible for route-aware data such as current path, API-key status, player name, and clearing the API key.

**Tech Stack:** React, TypeScript, MUI Core, TanStack Router, TanStack Query, Zustand, Vitest, Testing Library.

---

## Files

- Create: `frontend/app/src/routes/navigation.ts`
- Create: `frontend/app/src/routes/AppShellLayout.tsx`
- Create: `frontend/app/src/routes/AppShellLayout.test.tsx`
- Create: `frontend/app/src/routes/PlaceholderPage.tsx`
- Modify: `frontend/app/src/routes/AppShell.tsx`
- Modify: `frontend/app/src/app/router.tsx`
- Modify: `frontend/app/src/app/router.test.tsx`

## Tasks

- [ ] Write `AppShellLayout.test.tsx` that expects the product label, section title, API state, drawer navigation labels, and expand/collapse button behavior.
- [ ] Update `router.test.tsx` to expect the new placeholder route paths.
- [ ] Run `bun run test` from `frontend/app` and verify the tests fail because the new layout and route paths are not implemented.
- [ ] Implement `navigation.ts` with drawer items and `getSectionTitle(pathname: string)`.
- [ ] Implement `AppShellLayout.tsx` with MUI `AppBar`, permanent mini variant `Drawer`, and content offset.
- [ ] Update `AppShell.tsx` to compose `AppShellLayout` with `Outlet` and current session/player state.
- [ ] Implement `PlaceholderPage.tsx` and add placeholder routes in `router.tsx`.
- [ ] Run `bun run test`, `bun run build`, `bun run lint`, and `bun run format:check` from `frontend/app`.
