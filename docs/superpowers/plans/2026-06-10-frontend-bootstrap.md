# Frontend Bootstrap Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bootstrap the first Voidforge frontend app using Bun, Vite, React, MUI, TanStack Query, Zustand, Zod, TanStack Router, strict TypeScript, ESLint, Prettier, and Vitest.

**Architecture:** Create a greenfield React SPA in `frontend/app/` while keeping `frontend/epoch-1-scope.md` as planning documentation. The app has focused boundaries for providers, routing, API schemas/client/hooks, local session state, and page placeholders that reflect the currently implemented backend.

**Tech Stack:** Bun, Vite, React, TypeScript, MUI Core, Emotion, TanStack Query, TanStack Router, Zustand, Zod, ESLint, Prettier, Vitest.

---

## File Structure

- Create `frontend/app/package.json`: Bun scripts and dependencies.
- Create `frontend/app/index.html`: Vite HTML entry.
- Create `frontend/app/tsconfig.json`: strict TypeScript settings.
- Create `frontend/app/vite.config.ts`: Vite React config and Vitest config.
- Create `frontend/app/eslint.config.js`: ESLint flat config for TypeScript and React.
- Create `frontend/app/prettier.config.js`: Prettier formatting config.
- Create `frontend/app/src/test/setup.ts`: Vitest setup for browser-oriented tests.
- Create `frontend/app/src/main.tsx`: React entrypoint.
- Create `frontend/app/src/app/AppProviders.tsx`: MUI, Query, and global providers.
- Create `frontend/app/src/app/router.tsx`: TanStack Router route tree.
- Create `frontend/app/src/app/theme.ts`: MUI theme.
- Create `frontend/app/src/features/auth/sessionStore.ts`: Zustand API-key state.
- Create `frontend/app/src/shared/api/client.ts`: fetch wrapper using `X-API-Key`.
- Create `frontend/app/src/shared/api/schemas.ts`: Zod schemas for current backend DTOs.
- Create `frontend/app/src/shared/api/hooks.ts`: TanStack Query hooks and registration mutation.
- Create `frontend/app/src/routes/RootLayout.tsx`: root document shell.
- Create `frontend/app/src/routes/LoginPage.tsx`: API-key and registration page.
- Create `frontend/app/src/routes/AppShell.tsx`: authenticated app shell.
- Create `frontend/app/src/routes/EmpireOverviewPage.tsx`: initial dashboard placeholder using live endpoints.
- Create `frontend/app/src/routes/PlanetPage.tsx`: planet detail placeholder using live endpoint.
- Create `frontend/app/src/routes/NotFoundPage.tsx`: route fallback.
- Create `frontend/app/src/shared/api/schemas.test.ts`: schema validation tests.
- Modify `technical-design/project-structure.md`: document frontend location and commands.

## Task 1: Scaffold Package And Tooling

- [ ] Create `frontend/app/package.json` with scripts:

```json
{
  "name": "@voidforge/frontend",
  "private": true,
  "version": "0.0.0",
  "type": "module",
  "scripts": {
    "dev": "vite",
    "build": "tsc --noEmit && vite build",
    "preview": "vite preview",
    "test": "vitest run",
    "test:watch": "vitest"
  }
}
```

- [ ] Create Vite, strict TypeScript, ESLint, Prettier, Vitest setup, and HTML entry files.
- [ ] Run `bun install` in `frontend/app` to create `bun.lock`.

## Task 2: Add API Schemas Test First

- [ ] Create `frontend/app/src/shared/api/schemas.test.ts` before implementation.
- [ ] Run `bun run test` from `frontend/app` and confirm the schema exports are missing.
- [ ] Add Zod schemas in `frontend/app/src/shared/api/schemas.ts`.
- [ ] Run `bun run test` again and confirm the schema tests pass.

## Task 3: Add App Providers And Routing

- [ ] Create MUI theme, query client provider, and React entrypoint.
- [ ] Create TanStack Router route tree with `/`, `/login`, `/app`, and `/app/planets/$planetId`.
- [ ] Run `bun run build` and fix type errors.

## Task 4: Add State, API Client, And Query Hooks

- [ ] Create Zustand API-key session store.
- [ ] Create fetch client that sends `X-API-Key` when present.
- [ ] Create TanStack Query hooks for current player, solar systems, planet detail, and registration.
- [ ] Run `bun run build` and `bun run test`.

## Task 5: Add MUI Pages And Documentation

- [ ] Create login, app shell, empire overview, planet, and not-found pages.
- [ ] Update `technical-design/project-structure.md` with the frontend app layout and commands.
- [ ] Run final verification: `bun run build`, `bun run lint`, `bun run format:check`, `bun run test`, and, if environment allows, `dotnet test src/Voidforge.slnx` from repo root.
