import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import {
  Outlet,
  RouterProvider,
  createRootRoute,
  createRoute,
  createRouter,
} from '@tanstack/react-router';
import { type ReactNode } from 'react';
import {
  afterAll,
  afterEach,
  beforeAll,
  describe,
  expect,
  it,
  vi,
} from 'vitest';

import { AppProviders } from '../app/AppProviders';
import { createAppRouter } from '../app/router';
import { AppShellLayout } from './AppShellLayout';
import { getSectionTitle, navigationItems } from './navigation';

function renderWithTestRouter(ui: ReactNode) {
  const rootRoute = createRootRoute({ component: Outlet });
  const indexRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/',
    component: () => ui,
  });
  const router = createRouter({
    routeTree: rootRoute.addChildren([indexRoute]),
  });

  return render(
    <AppProviders>
      <RouterProvider router={router} />
    </AppProviders>,
  );
}

describe('AppShellLayout', () => {
  beforeAll(() => {
    vi.spyOn(window, 'scrollTo').mockImplementation(() => undefined);
  });

  afterAll(() => {
    vi.restoreAllMocks();
  });

  afterEach(() => {
    window.history.pushState({}, '', '/');
  });

  it('renders the top bar, mini drawer navigation, and content', async () => {
    renderWithTestRouter(
      <AppShellLayout
        apiConnected
        sectionTitle="Empire"
        playerName="Kedar"
        onClearApiKey={() => undefined}
      >
        <div>Empire content</div>
      </AppShellLayout>,
    );

    expect(await screen.findByText('Voidforge')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Empire' })).toBeInTheDocument();
    expect(screen.getByText('API connected')).toBeInTheDocument();
    expect(screen.getByText('Kedar')).toBeInTheDocument();
    expect(
      screen.getByRole('navigation', { name: 'Primary navigation' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Expand navigation' }),
    ).toBeInTheDocument();
    expect(screen.getByText('Empire content')).toBeInTheDocument();

    for (const item of navigationItems) {
      expect(
        screen.getByRole('link', { name: item.label }),
      ).toBeInTheDocument();
    }
  });

  it('toggles the mini drawer expansion state', async () => {
    const user = userEvent.setup();

    renderWithTestRouter(
      <AppShellLayout
        apiConnected={false}
        sectionTitle="Universe"
        playerName={null}
        onClearApiKey={() => undefined}
      >
        <div>Universe content</div>
      </AppShellLayout>,
    );

    await user.click(
      await screen.findByRole('button', { name: 'Expand navigation' }),
    );

    expect(
      screen.getByRole('button', { name: 'Collapse navigation' }),
    ).toBeInTheDocument();
    expect(screen.getByText('API disconnected')).toBeInTheDocument();
  });

  it('navigates drawer links through the app router', async () => {
    const user = userEvent.setup();
    window.history.pushState({}, '', '/app');
    const router = createAppRouter();

    render(
      <AppProviders>
        <RouterProvider router={router} />
      </AppProviders>,
    );

    await user.click(await screen.findByRole('link', { name: 'Universe' }));

    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/app/universe');
    });
    expect(
      await screen.findByText(
        'Browse solar systems, planet ownership, and colonization candidates.',
      ),
    ).toBeInTheDocument();
  });
});

describe('getSectionTitle', () => {
  it('maps route paths to section titles', () => {
    expect(getSectionTitle('/app')).toBe('Empire');
    expect(getSectionTitle('/app/planets')).toBe('Planets');
    expect(
      getSectionTitle('/app/planets/018f4c8a-3f10-7cc5-b802-cd2f7ba2b8f4'),
    ).toBe('Planet Detail');
    expect(getSectionTitle('/app/fleets')).toBe('Fleets');
  });
});
