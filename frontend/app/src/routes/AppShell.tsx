import { Outlet, useRouterState } from '@tanstack/react-router';

import { useCurrentPlayer } from '../shared/api/hooks';
import { AppShellLayout } from './AppShellLayout';
import { getSectionTitle } from './navigation';

export function AppShell() {
  const pathname = useRouterState({
    select: (state) => state.location.pathname,
  });
  const currentPlayer = useCurrentPlayer();

  return (
    <AppShellLayout
      sectionTitle={getSectionTitle(pathname)}
      playerName={currentPlayer.data?.name ?? null}
    >
      <Outlet />
    </AppShellLayout>
  );
}
