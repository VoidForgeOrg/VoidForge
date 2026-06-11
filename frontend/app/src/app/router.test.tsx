import { describe, expect, it } from 'vitest';

import { createAppRouter, routePaths } from './router';

describe('router', () => {
  it('defines the initial application routes', () => {
    expect(routePaths).toEqual([
      '/',
      '/login',
      '/app',
      '/app/universe',
      '/app/planets',
      '/app/planets/$planetId',
      '/app/buildings',
      '/app/shipyards',
      '/app/fleets',
      '/app/leaderboard',
      '/app/api-debug',
    ]);
  });

  it('creates a router instance', () => {
    expect(createAppRouter()).toBeDefined();
  });
});
