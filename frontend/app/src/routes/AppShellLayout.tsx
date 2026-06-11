import MuiAppBar, {
  type AppBarProps as MuiAppBarProps,
} from '@mui/material/AppBar';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Divider from '@mui/material/Divider';
import MuiDrawer from '@mui/material/Drawer';
import IconButton from '@mui/material/IconButton';
import List from '@mui/material/List';
import ListItem from '@mui/material/ListItem';
import ListItemButton from '@mui/material/ListItemButton';
import ListItemIcon from '@mui/material/ListItemIcon';
import ListItemText from '@mui/material/ListItemText';
import Stack from '@mui/material/Stack';
import { styled, type CSSObject, type Theme } from '@mui/material/styles';
import Toolbar from '@mui/material/Toolbar';
import Typography from '@mui/material/Typography';
import { Link as RouterLink } from '@tanstack/react-router';
import { type PropsWithChildren, useState } from 'react';

import { navigationItems } from './navigation';

const drawerWidth = 240;

const openedMixin = (theme: Theme): CSSObject => ({
  overflowX: 'hidden',
  transition: theme.transitions.create('width', {
    duration: theme.transitions.duration.enteringScreen,
    easing: theme.transitions.easing.sharp,
  }),
  width: drawerWidth,
});

const closedMixin = (theme: Theme): CSSObject => ({
  overflowX: 'hidden',
  transition: theme.transitions.create('width', {
    duration: theme.transitions.duration.leavingScreen,
    easing: theme.transitions.easing.sharp,
  }),
  width: `calc(${theme.spacing(7)} + 1px)`,
  [theme.breakpoints.up('sm')]: {
    width: `calc(${theme.spacing(8)} + 1px)`,
  },
});

interface StyledAppBarProps extends MuiAppBarProps {
  open?: boolean;
}

const AppBar = styled(MuiAppBar, {
  shouldForwardProp: (prop) => prop !== 'open',
})<StyledAppBarProps>(({ theme, open }) => ({
  borderBottom: `1px solid ${theme.palette.divider}`,
  transition: theme.transitions.create(['margin', 'width'], {
    duration: theme.transitions.duration.leavingScreen,
    easing: theme.transitions.easing.sharp,
  }),
  zIndex: theme.zIndex.drawer + 1,
  ...(open && {
    marginLeft: drawerWidth,
    transition: theme.transitions.create(['margin', 'width'], {
      duration: theme.transitions.duration.enteringScreen,
      easing: theme.transitions.easing.sharp,
    }),
    width: `calc(100% - ${String(drawerWidth)}px)`,
  }),
}));

const Drawer = styled(MuiDrawer, {
  shouldForwardProp: (prop) => prop !== 'open',
})(({ theme, open }) => ({
  boxSizing: 'border-box',
  flexShrink: 0,
  whiteSpace: 'nowrap',
  width: drawerWidth,
  ...(open && {
    ...openedMixin(theme),
  }),
  ...(!open && {
    ...closedMixin(theme),
  }),
  '& .MuiDrawer-paper': {
    ...(open ? openedMixin(theme) : closedMixin(theme)),
    borderRight: `1px solid ${theme.palette.divider}`,
    boxSizing: 'border-box',
  },
}));

const DrawerHeader = styled('div')(({ theme }) => ({
  alignItems: 'center',
  display: 'flex',
  justifyContent: 'flex-end',
  padding: theme.spacing(0, 1),
  ...theme.mixins.toolbar,
}));

interface AppShellLayoutProps extends PropsWithChildren {
  apiConnected: boolean;
  sectionTitle: string;
  playerName: string | null;
  onClearApiKey: () => void;
}

export function AppShellLayout({
  apiConnected,
  children,
  onClearApiKey,
  playerName,
  sectionTitle,
}: AppShellLayoutProps) {
  const [drawerOpen, setDrawerOpen] = useState(false);

  function openDrawer() {
    setDrawerOpen(true);
  }

  function closeDrawer() {
    setDrawerOpen(false);
  }

  return (
    <Box sx={{ display: 'flex', minHeight: '100vh' }}>
      <AppBar
        position="fixed"
        color="transparent"
        elevation={0}
        open={drawerOpen}
      >
        <Toolbar>
          <IconButton
            type="button"
            color="inherit"
            aria-label="Expand navigation"
            edge="start"
            onClick={openDrawer}
            sx={{ mr: 2, ...(drawerOpen && { display: 'none' }) }}
          >
            {'>>'}
          </IconButton>
          <Stack
            direction="row"
            spacing={2}
            sx={{ alignItems: 'center', flexGrow: 1 }}
          >
            <Typography variant="h6">Voidforge</Typography>
            <Typography color="text.secondary">/</Typography>
            <Typography component="h1" variant="h6">
              {sectionTitle}
            </Typography>
          </Stack>
          <Stack direction="row" spacing={2} sx={{ alignItems: 'center' }}>
            <Typography
              color={apiConnected ? 'success.main' : 'warning.main'}
              variant="body2"
            >
              {apiConnected ? 'API connected' : 'API disconnected'}
            </Typography>
            {playerName !== null ? (
              <Typography variant="body2">{playerName}</Typography>
            ) : null}
            <Button
              type="button"
              color="inherit"
              size="small"
              sx={{ textTransform: 'none' }}
            >
              Refresh
            </Button>
            <Button
              type="button"
              color="inherit"
              size="small"
              onClick={onClearApiKey}
              sx={{ textTransform: 'none' }}
            >
              Clear API key
            </Button>
          </Stack>
        </Toolbar>
      </AppBar>

      <Drawer variant="permanent" open={drawerOpen}>
        <DrawerHeader>
          <Typography
            variant="subtitle2"
            sx={{ flexGrow: 1, opacity: drawerOpen ? 1 : 0 }}
          >
            Navigation
          </Typography>
          {drawerOpen ? (
            <IconButton
              type="button"
              aria-label="Collapse navigation"
              onClick={closeDrawer}
            >
              {'<<'}
            </IconButton>
          ) : null}
        </DrawerHeader>
        <Divider />
        <Box component="nav" aria-label="Primary navigation" sx={{ py: 1 }}>
          <List disablePadding>
            {navigationItems.map((item) => (
              <ListItem
                key={item.path}
                disablePadding
                sx={{ display: 'block' }}
              >
                <ListItemButton
                  component={RouterLink}
                  to={item.path}
                  aria-label={item.label}
                  sx={{
                    color: 'text.primary',
                    justifyContent: drawerOpen ? 'initial' : 'center',
                    minHeight: 48,
                    px: 2.5,
                    textDecoration: 'none',
                  }}
                >
                  <ListItemIcon
                    aria-hidden="true"
                    sx={{
                      color: 'inherit',
                      justifyContent: 'center',
                      minWidth: 0,
                      mr: drawerOpen ? 3 : 'auto',
                    }}
                  >
                    <Box
                      sx={{
                        alignItems: 'center',
                        border: 1,
                        borderColor: 'divider',
                        borderRadius: 1,
                        display: 'inline-flex',
                        height: 32,
                        justifyContent: 'center',
                        width: 32,
                      }}
                    >
                      {item.shortLabel}
                    </Box>
                  </ListItemIcon>
                  <ListItemText
                    primary={item.label}
                    sx={{
                      opacity: drawerOpen ? 1 : 0,
                      transition: (theme) =>
                        theme.transitions.create('opacity'),
                      whiteSpace: 'nowrap',
                    }}
                  />
                </ListItemButton>
              </ListItem>
            ))}
          </List>
        </Box>
      </Drawer>

      <Box component="main" sx={{ flexGrow: 1, minWidth: 0, px: 3, py: 4 }}>
        <Toolbar />
        {children}
      </Box>
    </Box>
  );
}
