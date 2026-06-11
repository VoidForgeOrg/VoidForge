export interface NavigationItem {
  label: string;
  shortLabel: string;
  path: string;
}

export const navigationItems: NavigationItem[] = [
  { label: 'Empire', shortLabel: 'E', path: '/app' },
  { label: 'Universe', shortLabel: 'U', path: '/app/universe' },
  { label: 'Planets', shortLabel: 'P', path: '/app/planets' },
  { label: 'Buildings', shortLabel: 'B', path: '/app/buildings' },
  { label: 'Shipyards', shortLabel: 'S', path: '/app/shipyards' },
  { label: 'Fleets', shortLabel: 'F', path: '/app/fleets' },
  { label: 'Leaderboard', shortLabel: 'L', path: '/app/leaderboard' },
  { label: 'API / Debug', shortLabel: 'A', path: '/app/api-debug' },
];

export function getSectionTitle(pathname: string): string {
  if (pathname.startsWith('/app/planets/') && pathname !== '/app/planets') {
    return 'Planet Detail';
  }

  return (
    navigationItems.find((item) => item.path === pathname)?.label ?? 'Voidforge'
  );
}
