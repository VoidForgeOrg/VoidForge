import { describe, expect, it } from 'vitest';

import { APP_BASE_PATH, appPath, routePath } from '../routePaths';

describe('routePaths', () => {
  it('defines the authenticated app base once', () => {
    expect(APP_BASE_PATH).toBe('/app');
    expect(appPath('')).toBe('/app');
    expect(appPath('/universe')).toBe('/app/universe');
  });

  it('builds public app paths from the app base', () => {
    expect(routePath.home).toBe('/');
    expect(routePath.login).toBe('/login');
    expect(routePath.app.empire).toBe('/app');
    expect(routePath.app.planets).toBe('/app/planets');
    expect(routePath.app.planetTemplate).toBe('/app/planets/$planetId');
  });

  it('builds planet detail paths with the supplied planet id', () => {
    const planetId = '018f4c8a-3f10-7cc5-b802-cd2f7ba2b8f4';

    expect(routePath.app.planet(planetId)).toBe(`/app/planets/${planetId}`);
  });
});
