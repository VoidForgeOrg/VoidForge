# API Debug Controls Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move API connection status and API-key clearing from global chrome into the API / Debug route.

**Architecture:** Keep `AppShell` responsible for route section title and player name only. Add `ApiDebugPage` as the route owner for local API-key status and clearing behavior.

**Tech Stack:** React, TypeScript, MUI Core, TanStack Router, Zustand, Vitest, Testing Library.

---

## Files

- Create: `frontend/app/src/routes/ApiDebugPage.tsx`
- Create: `frontend/app/src/routes/ApiDebugPage.test.tsx`
- Modify: `frontend/app/src/routes/AppShell.tsx`
- Modify: `frontend/app/src/routes/AppShellLayout.tsx`
- Modify: `frontend/app/src/routes/AppShellLayout.test.tsx`
- Modify: `frontend/app/src/app/router.tsx`
- Modify: `frontend/app/src/app/router.test.tsx`

## Tasks

### Task 1: API Debug Page Behavior

- [ ] Write `ApiDebugPage.test.tsx` for disconnected state and clearing a stored API key.
- [ ] Run `bun run test -- src/routes/ApiDebugPage.test.tsx` and verify it fails because the page does not exist.
- [ ] Implement `ApiDebugPage.tsx` using MUI cards, status text, and a `Clear API key` button when a key is stored.
- [ ] Run `bun run test -- src/routes/ApiDebugPage.test.tsx` and verify it passes.

### Task 2: Route And Shell Ownership

- [ ] Update app shell tests to assert API status and clear action are absent from the app bar.
- [ ] Update router tests to assert `/app/api-debug` renders the real API Debug page.
- [ ] Run targeted tests and verify failures against current chrome/placeholder behavior.
- [ ] Remove `apiConnected` and `onClearApiKey` props from `AppShellLayout` and `AppShell`.
- [ ] Wire `/app/api-debug` to `ApiDebugPage` instead of the placeholder list.
- [ ] Run targeted tests and verify they pass.

### Task 3: Verification

- [ ] Run `bun run test` from `frontend/app`.
- [ ] Run `bun run build` from `frontend/app`.
- [ ] Run `bun run lint` from `frontend/app`.
- [ ] Run `bun run format:check` from `frontend/app`.
