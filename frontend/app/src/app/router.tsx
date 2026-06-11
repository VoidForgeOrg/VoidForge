import {
  createRootRoute,
  createRoute,
  createRouter,
} from '@tanstack/react-router';

import { AppShell } from '../routes/AppShell';
import { EmpireOverviewPage } from '../routes/EmpireOverviewPage';
import { LoginPage } from '../routes/LoginPage';
import { NotFoundPage } from '../routes/NotFoundPage';
import { PlanetPage } from '../routes/PlanetPage';
import { PlaceholderPage } from '../routes/PlaceholderPage';
import { RootLayout } from '../routes/RootLayout';

export const routePaths = [
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
] as const;

function HomeRouteComponent() {
  return <div>Voidforge frontend bootstrap</div>;
}

function PlanetRouteComponent() {
  const { planetId } = planetRoute.useParams();

  return <PlanetPage planetId={planetId} />;
}

function UniverseRouteComponent() {
  return (
    <PlaceholderPage
      title="Universe"
      description="Browse solar systems, planet ownership, and colonization candidates."
    />
  );
}

function PlanetsRouteComponent() {
  return (
    <PlaceholderPage
      title="Planets"
      description="Manage owned planets and inspect visible planets across the MVP universe."
    />
  );
}

function BuildingsRouteComponent() {
  return (
    <PlaceholderPage
      title="Buildings"
      description="Plan drills, refineries, generators, shipyards, construction, halts, and demolition."
    />
  );
}

function ShipyardsRouteComponent() {
  return (
    <PlaceholderPage
      title="Shipyards"
      description="Track colony ship and cargo vessel queues across owned shipyards."
    />
  );
}

function FleetsRouteComponent() {
  return (
    <PlaceholderPage
      title="Fleets"
      description="Track stationed fleets, in-transit fleets, missions, cargo, and ETAs."
    />
  );
}

function LeaderboardRouteComponent() {
  return (
    <PlaceholderPage
      title="Leaderboard"
      description="Show score, rank, and asset-value breakdowns once scoring endpoints exist."
    />
  );
}

function ApiDebugRouteComponent() {
  return (
    <PlaceholderPage
      title="API / Debug"
      description="Expose API-key status, IDs, API errors, and links to backend documentation."
    />
  );
}

const rootRoute = createRootRoute({
  component: RootLayout,
  notFoundComponent: NotFoundPage,
});

const homeRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/',
  component: HomeRouteComponent,
});

const loginRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/login',
  component: LoginPage,
});

const appRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/app',
  component: AppShell,
});

const empireOverviewRoute = createRoute({
  getParentRoute: () => appRoute,
  path: '/',
  component: EmpireOverviewPage,
});

const universeRoute = createRoute({
  getParentRoute: () => appRoute,
  path: '/universe',
  component: UniverseRouteComponent,
});

const planetsRoute = createRoute({
  getParentRoute: () => appRoute,
  path: '/planets',
  component: PlanetsRouteComponent,
});

const planetRoute = createRoute({
  getParentRoute: () => appRoute,
  path: '/planets/$planetId',
  component: PlanetRouteComponent,
});

const buildingsRoute = createRoute({
  getParentRoute: () => appRoute,
  path: '/buildings',
  component: BuildingsRouteComponent,
});

const shipyardsRoute = createRoute({
  getParentRoute: () => appRoute,
  path: '/shipyards',
  component: ShipyardsRouteComponent,
});

const fleetsRoute = createRoute({
  getParentRoute: () => appRoute,
  path: '/fleets',
  component: FleetsRouteComponent,
});

const leaderboardRoute = createRoute({
  getParentRoute: () => appRoute,
  path: '/leaderboard',
  component: LeaderboardRouteComponent,
});

const apiDebugRoute = createRoute({
  getParentRoute: () => appRoute,
  path: '/api-debug',
  component: ApiDebugRouteComponent,
});

const routeTree = rootRoute.addChildren([
  homeRoute,
  loginRoute,
  appRoute.addChildren([
    empireOverviewRoute,
    universeRoute,
    planetsRoute,
    planetRoute,
    buildingsRoute,
    shipyardsRoute,
    fleetsRoute,
    leaderboardRoute,
    apiDebugRoute,
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
