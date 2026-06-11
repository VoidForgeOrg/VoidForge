# API Debug Controls Design

## Goal

Move API connection state and API-key clearing out of the global app bar and into the `/app/api-debug` screen.

## Scope

- Remove `API connected` / `API disconnected` from the app bar.
- Remove `Clear API key` from the app bar.
- Keep player identity in the app bar when the current-player query has loaded.
- Replace the API / Debug placeholder with a real page that shows API key connection status and exposes the clear action.
- Keep the API status based on local API-key presence for this slice.

## Non-Goals

- Do not show the raw API key.
- Do not add backend health checks.
- Do not add request logs or debug consoles yet.

## Testing

- App shell tests assert the API controls are absent from the top bar.
- API Debug page tests assert connected/disconnected states and clearing the key.
- Router tests assert `/app/api-debug` renders the real API Debug page.
