import { render } from '@testing-library/react';
import { type ReactNode } from 'react';

import { AppProviders } from '../app/AppProviders';
import { useSessionStore } from '../features/auth/sessionStore';

export function resetFrontendTestState() {
  window.localStorage.clear();
  useSessionStore.setState({ apiKey: null });
}

export function renderWithAppProviders(ui: ReactNode) {
  return render(<AppProviders>{ui}</AppProviders>);
}
