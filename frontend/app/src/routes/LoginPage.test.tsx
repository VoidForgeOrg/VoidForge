import { render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';

import { AppProviders } from '../app/AppProviders';
import { useSessionStore } from '../features/auth/sessionStore';
import { LoginPage } from './LoginPage';

describe('LoginPage', () => {
  beforeEach(() => {
    window.localStorage.clear();
    useSessionStore.setState({ apiKey: null });
  });

  it('renders API key and registration controls', () => {
    render(
      <AppProviders>
        <LoginPage />
      </AppProviders>,
    );

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
