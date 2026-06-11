import { screen } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';

import {
  renderWithAppProviders,
  resetFrontendTestState,
} from '../../test/render';
import { EmpireOverviewPage } from '../EmpireOverviewPage';

describe('EmpireOverviewPage', () => {
  beforeEach(() => {
    resetFrontendTestState();
  });

  it('renders the epoch 1 dashboard shell', () => {
    renderWithAppProviders(<EmpireOverviewPage />);

    expect(
      screen.getByRole('heading', { name: 'Empire Overview' }),
    ).toBeInTheDocument();
    expect(screen.getByText('Current backend endpoints')).toBeInTheDocument();
  });
});
