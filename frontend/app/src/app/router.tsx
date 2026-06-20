import {
  createRootRoute,
  createRoute,
  createRouter,
} from '@tanstack/react-router';

import { ApiDebugPage } from '../routes/ApiDebugPage';
import { AppShell } from '../routes/AppShell';
import { EmpireOverviewPage } from '../routes/EmpireOverviewPage';
import { LoginPage } from '../routes/LoginPage';
import { NotFoundPage } from '../routes/NotFoundPage';
import { PlanetPage } from '../routes/PlanetPage';
import { PlaceholderPage } from '../routes/PlaceholderPage';
import { RootLayout } from '../routes/RootLayout';
import { APP_BASE_PATH, routePath } from './routePaths';

function HomeRouteComponent() {
  return <div>Voidforge frontend bootstrap</div>;
}

function PlanetRouteComponent() {
  const { planetId } = planetRoute.useParams();

  return <PlanetPage planetId={planetId} />;
}

const placeholderRouteDefinitions = [
  {
    path: '/universe',
    title: 'Universe',
    description:
      'Browse solar systems, planet ownership, and colonization candidates.',
  },
  {
    path: '/planets',
    title: 'Planets',
    description:
      'Manage owned planets and inspect visible planets across the MVP universe.',
  },
  {
    path: '/buildings',
    title: 'Buildings',
    description:
      'Plan drills, refineries, generators, shipyards, construction, halts, and demolition.',
  },
  {
    path: '/shipyards',
    title: 'Shipyards',
    description:
      'Track colony ship and cargo vessel queues across owned shipyards.',
  },
  {
    path: '/fleets',
    title: 'Fleets',
    description:
      'Track stationed fleets, in-transit fleets, missions, cargo, and ETAs.',
  },
  {
    path: '/leaderboard',
    title: 'Leaderboard',
    description:
      'Show score, rank, and asset-value breakdowns once scoring endpoints exist.',
  },
] as const;

const rootRoute = createRootRoute({
  component: RootLayout,
  notFoundComponent: NotFoundPage,
});

const homeRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: routePath.home,
  component: HomeRouteComponent,
});

const loginRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: routePath.login,
  component: LoginPage,
});

const appRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: APP_BASE_PATH,
  component: AppShell,
});

const empireOverviewRoute = createRoute({
  getParentRoute: () => appRoute,
  path: '/',
  component: EmpireOverviewPage,
});

const planetRoute = createRoute({
  getParentRoute: () => appRoute,
  path: '/planets/$planetId',
  component: PlanetRouteComponent,
});

const apiDebugRoute = createRoute({
  getParentRoute: () => appRoute,
  path: '/api-debug',
  component: ApiDebugPage,
});

const placeholderRoutes = placeholderRouteDefinitions.map((definition) =>
  createRoute({
    getParentRoute: () => appRoute,
    path: definition.path,
    component: () => (
      <PlaceholderPage
        title={definition.title}
        description={definition.description}
      />
    ),
  }),
);

const routeTree = rootRoute.addChildren([
  homeRoute,
  loginRoute,
  appRoute.addChildren([
    empireOverviewRoute,
    planetRoute,
    apiDebugRoute,
    ...placeholderRoutes,
  ]),
]);

export function createAppRouter() {
  return createRouter({ routeTree });
}

declare module '@tanstack/react-router' {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }
}
