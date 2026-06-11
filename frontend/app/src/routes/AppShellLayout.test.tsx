import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import {
  Outlet,
  RouterProvider,
  createRootRoute,
  createRoute,
  createRouter,
} from '@tanstack/react-router';
import { type ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { createAppRouter } from '../app/router';
import { routePath } from '../app/routePaths';
import { renderWithAppProviders, resetFrontendTestState } from '../test/render';
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

  return renderWithAppProviders(<RouterProvider router={router} />);
}

describe('AppShellLayout', () => {
  beforeEach(() => {
    resetFrontendTestState();
  });

  afterEach(() => {
    window.history.pushState({}, '', routePath.home);
  });

  it('renders the top bar, mini drawer navigation, and content', async () => {
    renderWithTestRouter(
      <AppShellLayout sectionTitle="Empire" playerName="Kedar">
        <div>Empire content</div>
      </AppShellLayout>,
    );

    expect(await screen.findByText('Voidforge')).toBeInTheDocument();
    const appBar = screen.getByRole('banner');
    expect(within(appBar).getByText('Empire')).toBeInTheDocument();
    expect(
      screen.queryByRole('heading', { name: 'Empire' }),
    ).not.toBeInTheDocument();
    expect(screen.queryByText('API connected')).not.toBeInTheDocument();
    expect(screen.queryByText('API disconnected')).not.toBeInTheDocument();
    expect(screen.getByText('Kedar')).toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Clear API key' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Refresh' }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole('navigation', { name: 'Primary navigation' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Expand navigation' }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Collapse navigation' }),
    ).not.toBeInTheDocument();
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
      <AppShellLayout sectionTitle="Universe" playerName={null}>
        <div>Universe content</div>
      </AppShellLayout>,
    );

    await user.click(
      await screen.findByRole('button', { name: 'Expand navigation' }),
    );

    expect(
      screen.getByRole('button', { name: 'Collapse navigation' }),
    ).toBeInTheDocument();
    expect(screen.queryByText('API disconnected')).not.toBeInTheDocument();
  });

  it('navigates drawer links through the app router', async () => {
    const user = userEvent.setup();
    window.history.pushState({}, '', routePath.app.empire);
    const router = createAppRouter();

    renderWithAppProviders(<RouterProvider router={router} />);

    await user.click(await screen.findByRole('link', { name: 'Universe' }));

    await waitFor(() => {
      expect(router.state.location.pathname).toBe(routePath.app.universe);
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
    expect(getSectionTitle(routePath.app.empire)).toBe('Empire');
    expect(getSectionTitle(routePath.app.planets)).toBe('Planets');
    expect(
      getSectionTitle(
        routePath.app.planet('018f4c8a-3f10-7cc5-b802-cd2f7ba2b8f4'),
      ),
    ).toBe('Planet Detail');
    expect(getSectionTitle(routePath.app.fleets)).toBe('Fleets');
    expect(getSectionTitle('/unexpected')).toBe('Voidforge');
  });
});
