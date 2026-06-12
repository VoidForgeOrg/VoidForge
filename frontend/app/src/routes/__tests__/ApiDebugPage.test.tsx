import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { type ReactNode } from 'react';
import { beforeEach, describe, expect, it } from 'vitest';

import { useSessionStore } from '../../features/auth/sessionStore';
import {
  renderWithAppProviders,
  resetFrontendTestState,
} from '../../test/render';
import { ApiDebugPage } from '../ApiDebugPage';

function renderWithQueryClient(ui: ReactNode, queryClient: QueryClient) {
  return render(
    <QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>,
  );
}

describe('ApiDebugPage', () => {
  beforeEach(() => {
    resetFrontendTestState();
  });

  it('shows disconnected state when no API key is stored', () => {
    renderWithAppProviders(<ApiDebugPage />);

    expect(
      screen.getByRole('heading', { name: 'API / Debug' }),
    ).toBeInTheDocument();
    expect(screen.getByText('API disconnected')).toBeInTheDocument();
    expect(
      screen.getByText('No API key is stored locally.'),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Clear API key' }),
    ).not.toBeInTheDocument();
  });

  it('clears a stored API key from the debug screen', async () => {
    const user = userEvent.setup();
    useSessionStore.getState().setApiKey('vf_test_key');

    renderWithAppProviders(<ApiDebugPage />);

    expect(screen.getByText('API connected')).toBeInTheDocument();
    expect(
      screen.getByText('An API key is stored locally.'),
    ).toBeInTheDocument();
    expect(screen.queryByText('vf_test_key')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Clear API key' }));

    expect(useSessionStore.getState().apiKey).toBeNull();
    expect(screen.getByText('API disconnected')).toBeInTheDocument();
  });

  it('clears cached current-player data when clearing the API key', async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient();
    const currentPlayerKey = ['current-player', 'vf_test_key'] as const;
    queryClient.setQueryData(currentPlayerKey, {
      id: '018f4c8a-3f10-7cc5-b802-cd2f7ba2b8f4',
      name: 'Kedar',
      registeredAt: '2026-06-10T12:00:00Z',
    });
    useSessionStore.getState().setApiKey('vf_test_key');

    renderWithQueryClient(<ApiDebugPage />, queryClient);

    await user.click(screen.getByRole('button', { name: 'Clear API key' }));

    expect(queryClient.getQueryData(currentPlayerKey)).toBeUndefined();
  });
});
