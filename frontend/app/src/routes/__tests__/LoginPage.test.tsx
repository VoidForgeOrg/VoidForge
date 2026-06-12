import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  Outlet,
  RouterProvider,
  createRootRoute,
  createRoute,
  createRouter,
} from '@tanstack/react-router';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { useSessionStore } from '../../features/auth/sessionStore';
import { resetFrontendTestState } from '../../test/render';
import { LoginPage } from '../LoginPage';

function renderLoginPage(queryClient = new QueryClient()) {
  const rootRoute = createRootRoute({
    component: () => (
      <QueryClientProvider client={queryClient}>
        <Outlet />
      </QueryClientProvider>
    ),
  });
  const loginRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/',
    component: LoginPage,
  });
  const appRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/app',
    component: () => null,
  });
  const router = createRouter({
    routeTree: rootRoute.addChildren([loginRoute, appRoute]),
  });

  return render(<RouterProvider router={router} />);
}

describe('LoginPage', () => {
  beforeEach(() => {
    resetFrontendTestState();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('renders API key and registration controls', async () => {
    renderLoginPage();

    expect(await screen.findByLabelText('API key')).toBeInTheDocument();
    expect(screen.getByLabelText('Player name')).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Save API key' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Register player' }),
    ).toBeInTheDocument();
  });

  it('does not prefill or reveal a stored API key', async () => {
    useSessionStore.getState().setApiKey('vf_secret_key');

    renderLoginPage();

    const apiKeyInput = await screen.findByLabelText('API key');
    expect(apiKeyInput).toHaveValue('');
    expect(apiKeyInput).toHaveAttribute('type', 'password');
    expect(screen.queryByDisplayValue('vf_secret_key')).not.toBeInTheDocument();
  });

  it('clears cached current-player data when saving an API key', async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient();
    const currentPlayerKey = ['current-player', 'vf_old_key'] as const;
    queryClient.setQueryData(currentPlayerKey, {
      id: '018f4c8a-3f10-7cc5-b802-cd2f7ba2b8f4',
      name: 'Kedar',
      registeredAt: '2026-06-10T12:00:00Z',
    });

    renderLoginPage(queryClient);

    await user.type(await screen.findByLabelText('API key'), 'vf_new_key');
    await user.click(screen.getByRole('button', { name: 'Save API key' }));

    expect(useSessionStore.getState().apiKey).toBe('vf_new_key');
    expect(queryClient.getQueryData(currentPlayerKey)).toBeUndefined();
  });

  it('shows the generated API key once after registering a player', async () => {
    const user = userEvent.setup();
    vi.stubGlobal('fetch', () =>
      Promise.resolve(
        new Response(
          JSON.stringify({
            playerId: '018f4c8a-3f10-7cc5-b802-cd2f7ba2b8f4',
            apiKey: 'vf_generated_key',
            homeworldId: '018f4c8a-3f10-7cc5-b802-cd2f7ba2b8f5',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        ),
      ),
    );

    renderLoginPage();

    await user.type(await screen.findByLabelText('Player name'), 'Kedar');
    await user.click(screen.getByRole('button', { name: 'Register player' }));

    expect(
      await screen.findByText('API key: vf_generated_key'),
    ).toBeInTheDocument();
    expect(useSessionStore.getState().apiKey).toBe('vf_generated_key');
  });
});
