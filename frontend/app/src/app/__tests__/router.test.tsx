import { screen } from '@testing-library/react';
import { RouterProvider } from '@tanstack/react-router';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import {
  renderWithAppProviders,
  resetFrontendTestState,
} from '../../test/render';
import { createAppRouter } from '../router';
import { routePath } from '../routePaths';

describe('router', () => {
  beforeEach(() => {
    resetFrontendTestState();
  });

  afterEach(() => {
    window.history.pushState({}, '', routePath.home);
  });

  it('creates a router instance', () => {
    expect(createAppRouter()).toBeDefined();
  });

  it('renders app placeholder routes built from route paths', async () => {
    window.history.pushState({}, '', routePath.app.universe);
    const router = createAppRouter();

    renderWithAppProviders(<RouterProvider router={router} />);

    expect(
      await screen.findByRole('heading', { name: 'Universe' }),
    ).toBeInTheDocument();
    expect(router.state.location.pathname).toBe(routePath.app.universe);
  });

  it('passes planet detail route params through the app router', async () => {
    const planetId = '018f4c8a-3f10-7cc5-b802-cd2f7ba2b8f4';
    window.history.pushState({}, '', routePath.app.planet(planetId));
    const router = createAppRouter();

    renderWithAppProviders(<RouterProvider router={router} />);

    expect(
      await screen.findByRole('heading', { name: 'Planet Detail' }),
    ).toBeInTheDocument();
    expect(screen.getByText(planetId)).toBeInTheDocument();
    expect(router.state.location.pathname).toBe(routePath.app.planet(planetId));
  });

  it('renders the real API debug page route', async () => {
    window.history.pushState({}, '', routePath.app.apiDebug);
    const router = createAppRouter();

    renderWithAppProviders(<RouterProvider router={router} />);

    expect(
      await screen.findByRole('heading', { name: 'API / Debug' }),
    ).toBeInTheDocument();
    expect(screen.getByText('API disconnected')).toBeInTheDocument();
    expect(
      screen.getByText('No API key is stored locally.'),
    ).toBeInTheDocument();
  });
});
