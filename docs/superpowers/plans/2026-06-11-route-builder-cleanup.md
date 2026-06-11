# Route Builder Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Centralize authenticated frontend paths behind a route builder and remove low-risk app chrome/test duplication.

**Architecture:** Add `src/app/routePaths.ts` as the only module that knows the `/app` prefix. Keep route metadata separate from TanStack route creation, and keep tests behavior-focused by using route builder outputs for navigation inputs while route-path tests assert exact public strings.

**Tech Stack:** React, TypeScript, MUI Core, TanStack Router, TanStack Query, Zustand, Vitest, Testing Library.

---

## Files

- Create: `frontend/app/src/app/routePaths.ts`
- Create: `frontend/app/src/app/routePaths.test.ts`
- Create: `frontend/app/src/test/render.tsx`
- Modify: `frontend/app/src/app/router.tsx`
- Modify: `frontend/app/src/app/router.test.tsx`
- Modify: `frontend/app/src/routes/navigation.ts`
- Modify: `frontend/app/src/routes/AppShellLayout.tsx`
- Modify: `frontend/app/src/routes/AppShellLayout.test.tsx`
- Modify: `frontend/app/src/routes/LoginPage.tsx`
- Modify: `frontend/app/src/routes/NotFoundPage.tsx`
- Modify: route tests that repeat provider setup.

## Tasks

### Task 1: Route Path Helper

- [ ] Write `routePaths.test.ts` expecting `APP_BASE_PATH` to be `/app`, app paths to be built from suffixes, and planet detail paths to include the planet ID.
- [ ] Run `bun run test -- src/app/routePaths.test.ts` and verify it fails because the module does not exist.
- [ ] Implement `routePaths.ts` with `APP_BASE_PATH`, `appPath(suffix)`, and `routePath` constants/functions.
- [ ] Run `bun run test -- src/app/routePaths.test.ts` and verify it passes.

### Task 2: Navigation And Router Consumption

- [ ] Update router/layout/not-found tests to use `routePath` outputs and add router integration coverage for planet detail params.
- [ ] Run targeted tests and verify failures before production imports are updated.
- [ ] Update `router.tsx`, `navigation.ts`, `LoginPage.tsx`, and `NotFoundPage.tsx` to consume route helpers.
- [ ] Collapse placeholder route components into metadata-driven route construction in `router.tsx`.
- [ ] Run targeted tests and verify they pass.

### Task 3: Low-Risk Bloat Removal

- [ ] Add `src/test/render.tsx` with `resetFrontendTestState()` and `renderWithAppProviders()`.
- [ ] Refactor route tests that repeat provider/session setup to use the helper.
- [ ] Remove the no-op `Refresh` button from `AppShellLayout.tsx`.
- [ ] Change the app-bar section title from `component="h1"` to non-heading text.
- [ ] Run app route tests and verify they pass.

### Task 4: Full Verification

- [ ] Run `bun run test` from `frontend/app`.
- [ ] Run `bun run build` from `frontend/app`.
- [ ] Run `bun run lint` from `frontend/app`.
- [ ] Run `bun run format:check` from `frontend/app`.
- [ ] Search for remaining hardcoded `/app` strings in `frontend/app/src` and keep only route-path contract assertions or unavoidable route definitions.
