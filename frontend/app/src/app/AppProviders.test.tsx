import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import { AppProviders } from './AppProviders';

describe('AppProviders', () => {
  it('renders children inside the application provider stack', () => {
    render(
      <AppProviders>
        <div>Voidforge child</div>
      </AppProviders>,
    );

    expect(screen.getByText('Voidforge child')).toBeInTheDocument();
  });
});
