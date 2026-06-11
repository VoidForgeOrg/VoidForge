import { render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';

import { AppProviders } from '../app/AppProviders';
import { useSessionStore } from '../features/auth/sessionStore';
import { PlanetPage } from './PlanetPage';

describe('PlanetPage', () => {
  beforeEach(() => {
    window.localStorage.clear();
    useSessionStore.setState({ apiKey: null });
  });

  it('renders the planet detail shell for a route planet ID', () => {
    render(
      <AppProviders>
        <PlanetPage planetId="018f4c8a-3f10-7cc5-b802-cd2f7ba2b8f4" />
      </AppProviders>,
    );

    expect(
      screen.getByRole('heading', { name: 'Planet Detail' }),
    ).toBeInTheDocument();
    expect(
      screen.getByText('018f4c8a-3f10-7cc5-b802-cd2f7ba2b8f4'),
    ).toBeInTheDocument();
  });
});
