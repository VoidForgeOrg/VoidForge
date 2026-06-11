import { render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';

import { AppProviders } from '../app/AppProviders';
import { useSessionStore } from '../features/auth/sessionStore';
import { EmpireOverviewPage } from './EmpireOverviewPage';

describe('EmpireOverviewPage', () => {
  beforeEach(() => {
    window.localStorage.clear();
    useSessionStore.setState({ apiKey: null });
  });

  it('renders the epoch 1 dashboard shell', () => {
    render(
      <AppProviders>
        <EmpireOverviewPage />
      </AppProviders>,
    );

    expect(
      screen.getByRole('heading', { name: 'Empire Overview' }),
    ).toBeInTheDocument();
    expect(screen.getByText('Current backend endpoints')).toBeInTheDocument();
  });
});
