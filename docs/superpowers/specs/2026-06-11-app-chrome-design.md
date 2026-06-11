# App Chrome Design

## Goal

Add the first reusable application chrome for the Voidforge frontend so the current placeholder screens sit inside a stable desktop management layout.

## Scope

This slice implements only the structural bones of the app:

- Fixed MUI `AppBar` across the top.
- Permanent MUI mini variant `Drawer` on the left.
- Collapsed drawer by default with short icon markers.
- Expand/collapse drawer control in the top bar.
- Main content offset under the app bar and beside the drawer.
- Drawer navigation entries for Epoch 1 surfaces, backed by lightweight placeholder routes where needed.

This slice does not design the deep content of Empire, Planet, Building, Fleet, or Universe screens.

## Navigation

Drawer entries:

- Empire: `/app`
- Universe: `/app/universe`
- Planets: `/app/planets`
- Buildings: `/app/buildings`
- Shipyards: `/app/shipyards`
- Fleets: `/app/fleets`
- Leaderboard: `/app/leaderboard`
- API / Debug: `/app/api-debug`

Planet detail remains available at `/app/planets/$planetId`.

## App Bar

The app bar shows:

- Voidforge product label.
- Current section title based on the route.
- API connection state derived from whether an API key is stored.
- Current player name when the player query has loaded.
- A refresh placeholder button.
- Clear API key action.

## Implementation Notes

- Keep layout state local to the shell component; do not add global Zustand state for drawer expansion.
- Keep navigation metadata in one module so drawer labels and route title lookup cannot drift.
- Use existing MUI Core components only.
- Keep placeholder routes intentionally simple until the deeper screen designs are approved.
