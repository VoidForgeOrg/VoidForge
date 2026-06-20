export const APP_BASE_PATH = '/app' as const;

export function appPath<const TSuffix extends '' | `/${string}`>(
  suffix: TSuffix,
): `${typeof APP_BASE_PATH}${TSuffix}` {
  return `${APP_BASE_PATH}${suffix}`;
}

export const routePath = {
  home: '/',
  login: '/login',
  app: {
    empire: appPath(''),
    universe: appPath('/universe'),
    planets: appPath('/planets'),
    planet: (planetId: string) => appPath(`/planets/${planetId}`),
    buildings: appPath('/buildings'),
    shipyards: appPath('/shipyards'),
    fleets: appPath('/fleets'),
    leaderboard: appPath('/leaderboard'),
    apiDebug: appPath('/api-debug'),
  },
} as const;
