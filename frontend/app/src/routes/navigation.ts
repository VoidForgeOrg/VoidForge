import ApiIcon from '@mui/icons-material/Api';
import DashboardIcon from '@mui/icons-material/Dashboard';
import FactoryIcon from '@mui/icons-material/Factory';
import LeaderboardIcon from '@mui/icons-material/Leaderboard';
import PrecisionManufacturingIcon from '@mui/icons-material/PrecisionManufacturing';
import PublicIcon from '@mui/icons-material/Public';
import RocketLaunchIcon from '@mui/icons-material/RocketLaunch';
import TravelExploreIcon from '@mui/icons-material/TravelExplore';
import { type SvgIconProps } from '@mui/material/SvgIcon';
import { type ComponentType } from 'react';

import { routePath } from '../app/routePaths';

export interface NavigationItem {
  label: string;
  path: string;
  Icon: ComponentType<SvgIconProps>;
}

export const navigationItems: NavigationItem[] = [
  { label: 'Empire', path: routePath.app.empire, Icon: DashboardIcon },
  { label: 'Universe', path: routePath.app.universe, Icon: TravelExploreIcon },
  { label: 'Planets', path: routePath.app.planets, Icon: PublicIcon },
  { label: 'Buildings', path: routePath.app.buildings, Icon: FactoryIcon },
  {
    label: 'Shipyards',
    path: routePath.app.shipyards,
    Icon: PrecisionManufacturingIcon,
  },
  { label: 'Fleets', path: routePath.app.fleets, Icon: RocketLaunchIcon },
  {
    label: 'Leaderboard',
    path: routePath.app.leaderboard,
    Icon: LeaderboardIcon,
  },
  { label: 'API / Debug', path: routePath.app.apiDebug, Icon: ApiIcon },
];

export function getSectionTitle(pathname: string): string {
  if (
    pathname.startsWith(`${routePath.app.planets}/`) &&
    pathname !== routePath.app.planets
  ) {
    return 'Planet Detail';
  }

  return (
    navigationItems.find((item) => item.path === pathname)?.label ?? 'Voidforge'
  );
}
