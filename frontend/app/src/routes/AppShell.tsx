import { Outlet, useRouterState } from '@tanstack/react-router';

import { useSessionStore } from '../features/auth/sessionStore';
import { useCurrentPlayer } from '../shared/api/hooks';
import { AppShellLayout } from './AppShellLayout';
import { getSectionTitle } from './navigation';

export function AppShell() {
  const pathname = useRouterState({
    select: (state) => state.location.pathname,
  });
  const apiKey = useSessionStore((state) => state.apiKey);
  const clearApiKey = useSessionStore((state) => state.clearApiKey);
  const currentPlayer = useCurrentPlayer();

  return (
    <AppShellLayout
      apiConnected={apiKey !== null}
      sectionTitle={getSectionTitle(pathname)}
      playerName={currentPlayer.data?.name ?? null}
      onClearApiKey={clearApiKey}
    >
      <Outlet />
    </AppShellLayout>
  );
}
