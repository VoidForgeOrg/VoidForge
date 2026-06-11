# Drawer Icon Cleanup Design

## Goal

Make the app drawer match the MUI mini-drawer demo more closely by using real MUI icons and compact icon-button controls instead of text glyphs and boxed letter markers.

## Scope

- Add `@mui/icons-material` to the frontend app.
- Replace the top drawer toggle text glyph with `MenuIcon`.
- Replace the drawer close text glyph with `ChevronLeftIcon` or `ChevronRightIcon` based on theme direction.
- Give each drawer navigation item a semantic MUI icon.
- Keep public routes and route-builder behavior unchanged.
- Keep tests behavior-focused; do not assert SVG internals.

## Non-Goals

- Do not redesign the full app chrome layout.
- Do not add responsive temporary/mobile drawer behavior in this slice.
- Do not introduce custom SVG assets.

## Testing

- Existing drawer tests continue to assert expand/collapse controls and navigation links.
- Add/keep accessible labels for icon-only controls.
- Run full frontend test/build/lint/format verification.
