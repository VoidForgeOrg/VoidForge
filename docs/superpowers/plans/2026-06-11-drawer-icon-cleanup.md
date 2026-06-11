# Drawer Icon Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace text-based drawer controls and boxed letter markers with demo-style MUI icons.

**Architecture:** Add `@mui/icons-material` and keep icon selection in the navigation metadata consumed by `AppShellLayout`. Keep route behavior unchanged and tests behavior-focused around accessible controls and navigation.

**Tech Stack:** React, TypeScript, MUI Core, MUI Icons, TanStack Router, Vitest, Testing Library, Bun.

---

## Files

- Modify: `frontend/app/package.json`
- Modify: `frontend/app/bun.lock`
- Modify: `frontend/app/src/routes/navigation.ts`
- Modify: `frontend/app/src/routes/AppShellLayout.tsx`
- Modify: `frontend/app/src/routes/AppShellLayout.test.tsx`

## Tasks

### Task 1: Dependency And Icon Metadata

- [ ] Run `bun add @mui/icons-material` from `frontend/app`.
- [ ] Update `navigation.ts` so each `NavigationItem` has an `Icon` component instead of `shortLabel`.
- [ ] Use stable route labels and paths from existing navigation metadata.

### Task 2: Demo-Style Drawer Controls

- [ ] Update `AppShellLayout.test.tsx` to keep asserting expand/collapse controls and navigation links by accessible name.
- [ ] Run `bun run test -- src/routes/AppShellLayout.test.tsx` and verify it still catches drawer behavior.
- [ ] Update `AppShellLayout.tsx` to import `useTheme`, `MenuIcon`, `ChevronLeftIcon`, and `ChevronRightIcon`.
- [ ] Replace text glyph controls with icon components.
- [ ] Replace boxed letter drawer markers with the navigation `Icon` components.
- [ ] Adjust `sx` to match the MUI mini-drawer demo: compact icon button spacing, `sx` arrays for open/closed list item styles, and icon-only collapsed drawer items.

### Task 3: Verification

- [ ] Run `bun run test` from `frontend/app`.
- [ ] Run `bun run build` from `frontend/app`.
- [ ] Run `bun run lint` from `frontend/app`.
- [ ] Run `bun run format:check` from `frontend/app`.
