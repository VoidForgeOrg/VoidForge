import { render, screen } from '@testing-library/react';
import { RouterProvider } from '@tanstack/react-router';
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

describe('NotFoundPage', () => {
  beforeAll(() => {
    vi.spyOn(window, 'scrollTo').mockImplementation(() => undefined);
  });

  afterAll(() => {
    vi.restoreAllMocks();
  });

  afterEach(() => {
    window.history.pushState({}, '', '/');
  });

  it('offers a return action to the app', async () => {
    window.history.pushState({}, '', '/missing-route');
    const router = createAppRouter();

    render(
      <AppProviders>
        <RouterProvider router={router} />
      </AppProviders>,
    );

    expect(
      await screen.findByRole('link', { name: 'Return to empire' }),
    ).toHaveAttribute('href', '/app');
  });
});
