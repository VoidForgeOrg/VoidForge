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
import { RootLayout } from '../routes/RootLayout';

export const routePaths = [
  '/',
  '/login',
  '/app',
  '/app/planets/$planetId',
] as const;

function HomeRouteComponent() {
  return <div>Voidforge frontend bootstrap</div>;
}

function PlanetRouteComponent() {
  const { planetId } = planetRoute.useParams();

  return <PlanetPage planetId={planetId} />;
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

const planetRoute = createRoute({
  getParentRoute: () => appRoute,
  path: '/planets/$planetId',
  component: PlanetRouteComponent,
});

const routeTree = rootRoute.addChildren([
  homeRoute,
  loginRoute,
  appRoute.addChildren([empireOverviewRoute, planetRoute]),
]);

export function createAppRouter() {
  return createRouter({ routeTree });
}

declare module '@tanstack/react-router' {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }
}
