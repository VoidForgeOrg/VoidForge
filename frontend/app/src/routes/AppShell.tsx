import AppBar from '@mui/material/AppBar';
import Box from '@mui/material/Box';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import Toolbar from '@mui/material/Toolbar';
import Typography from '@mui/material/Typography';
import { Link, Outlet } from '@tanstack/react-router';

import { useSessionStore } from '../features/auth/sessionStore';

export function AppShell() {
  const clearApiKey = useSessionStore((state) => state.clearApiKey);

  return (
    <Box>
      <AppBar position="static" color="transparent" elevation={0}>
        <Toolbar>
          <Stack
            direction="row"
            spacing={2}
            sx={{ alignItems: 'center', flexGrow: 1 }}
          >
            <Typography variant="h6">Voidforge</Typography>
            <Link to="/app">Empire</Link>
            <Link to="/login">Login</Link>
          </Stack>
          <Box
            component="button"
            type="button"
            onClick={clearApiKey}
            sx={{
              bgcolor: 'transparent',
              border: 0,
              color: 'text.primary',
              cursor: 'pointer',
              font: 'inherit',
            }}
          >
            Clear API key
          </Box>
        </Toolbar>
      </AppBar>
      <Container maxWidth="lg" sx={{ py: 4 }}>
        <Outlet />
      </Container>
    </Box>
  );
}
