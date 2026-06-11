import { screen } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';

import { renderWithAppProviders, resetFrontendTestState } from '../test/render';
import { PlanetPage } from './PlanetPage';

describe('PlanetPage', () => {
  beforeEach(() => {
    resetFrontendTestState();
  });

  it('renders the planet detail shell for a route planet ID', () => {
    renderWithAppProviders(
      <PlanetPage planetId="018f4c8a-3f10-7cc5-b802-cd2f7ba2b8f4" />,
    );

    expect(
      screen.getByRole('heading', { name: 'Planet Detail' }),
    ).toBeInTheDocument();
    expect(
      screen.getByText('018f4c8a-3f10-7cc5-b802-cd2f7ba2b8f4'),
    ).toBeInTheDocument();
  });
});
