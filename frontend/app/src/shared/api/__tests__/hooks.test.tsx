import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { type ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { useSessionStore } from '../../../features/auth/sessionStore';
import { resetFrontendTestState } from '../../../test/render';
import { queryKeys, useCurrentPlayer } from '../hooks';

const oldPlayer = {
  id: '018f4c8a-3f10-7cc5-b802-cd2f7ba2b8f4',
  name: 'Old Commander',
  registeredAt: '2026-06-10T12:00:00Z',
};

function CurrentPlayerName() {
  const currentPlayer = useCurrentPlayer();

  return <div>{currentPlayer.data?.name ?? 'No player'}</div>;
}

function renderWithQueryClient(ui: ReactNode, queryClient: QueryClient) {
  return render(
    <QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>,
  );
}

describe('API query hooks', () => {
  beforeEach(() => {
    resetFrontendTestState();
    vi.stubGlobal('fetch', () =>
      Promise.resolve(
        new Response(
          JSON.stringify({
            id: '018f4c8a-3f10-7cc5-b802-cd2f7ba2b8f5',
            name: 'New Commander',
            registeredAt: '2026-06-11T12:00:00Z',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        ),
      ),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('does not expose cached current-player data after the API key is cleared', () => {
    const queryClient = new QueryClient();
    queryClient.setQueryData(queryKeys.currentPlayer('vf_old_key'), oldPlayer);
    // apiKey is null after reset, so the query is disabled and must not surface stale data.

    renderWithQueryClient(<CurrentPlayerName />, queryClient);

    expect(screen.getByText('No player')).toBeInTheDocument();
    expect(screen.queryByText('Old Commander')).not.toBeInTheDocument();
  });

  it('does not reuse cached current-player data after switching API keys', () => {
    const queryClient = new QueryClient();
    queryClient.setQueryData(queryKeys.currentPlayer('vf_old_key'), oldPlayer);
    useSessionStore.getState().setApiKey('vf_new_key');

    renderWithQueryClient(<CurrentPlayerName />, queryClient);

    // The new key reads queryKeys.currentPlayer('vf_new_key'), never the old key's cache.
    expect(screen.queryByText('Old Commander')).not.toBeInTheDocument();
  });
});
