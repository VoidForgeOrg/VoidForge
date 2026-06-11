import { describe, expect, it } from 'vitest';

import { createAppRouter, routePaths } from './router';

describe('router', () => {
  it('defines the initial application routes', () => {
    expect(routePaths).toEqual([
      '/',
      '/login',
      '/app',
      '/app/planets/$planetId',
    ]);
  });

  it('creates a router instance', () => {
    expect(createAppRouter()).toBeDefined();
  });
});
