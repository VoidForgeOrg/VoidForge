import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it } from 'vitest';

import { useSessionStore } from '../features/auth/sessionStore';
import { renderWithAppProviders, resetFrontendTestState } from '../test/render';
import { ApiDebugPage } from './ApiDebugPage';

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
});
