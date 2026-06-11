import { screen } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';

import {
  renderWithAppProviders,
  resetFrontendTestState,
} from '../../test/render';
import { LoginPage } from '../LoginPage';

describe('LoginPage', () => {
  beforeEach(() => {
    resetFrontendTestState();
  });

  it('renders API key and registration controls', () => {
    renderWithAppProviders(<LoginPage />);

    expect(screen.getByLabelText('API key')).toBeInTheDocument();
    expect(screen.getByLabelText('Player name')).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Save API key' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Register player' }),
    ).toBeInTheDocument();
  });
});
