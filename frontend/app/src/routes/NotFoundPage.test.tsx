import { screen } from '@testing-library/react';
import { RouterProvider } from '@tanstack/react-router';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { createAppRouter } from '../app/router';
import { routePath } from '../app/routePaths';
import { renderWithAppProviders, resetFrontendTestState } from '../test/render';

describe('NotFoundPage', () => {
  beforeEach(() => {
    resetFrontendTestState();
  });

  afterEach(() => {
    window.history.pushState({}, '', routePath.home);
  });

  it('offers a return action to the app', async () => {
    window.history.pushState({}, '', '/missing-route');
    const router = createAppRouter();

    renderWithAppProviders(<RouterProvider router={router} />);

    expect(
      await screen.findByRole('link', { name: 'Return to empire' }),
    ).toHaveAttribute('href', routePath.app.empire);
  });
});
