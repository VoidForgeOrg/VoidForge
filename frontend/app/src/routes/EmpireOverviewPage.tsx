import Box from '@mui/material/Box';
import Card from '@mui/material/Card';
import CardContent from '@mui/material/CardContent';
import CircularProgress from '@mui/material/CircularProgress';
import Grid from '@mui/material/Grid';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';

import { useSessionStore } from '../features/auth/sessionStore';
import { useCurrentPlayer, useSolarSystems } from '../shared/api/hooks';

function formatError(error: unknown) {
  return error instanceof Error ? error.message : 'Unknown API error';
}

export function EmpireOverviewPage() {
  const apiKey = useSessionStore((state) => state.apiKey);
  const currentPlayer = useCurrentPlayer();
  const solarSystems = useSolarSystems();

  return (
    <Stack spacing={3}>
      <Stack spacing={1}>
        <Typography component="h1" variant="h4">
          Empire Overview
        </Typography>
        <Typography color="text.secondary">
          First shell for the Epoch 1 economy and expansion frontend.
        </Typography>
      </Stack>

      {apiKey === null ? (
        <Box
          role="status"
          sx={{ border: 1, borderColor: 'info.main', borderRadius: 1, p: 2 }}
        >
          Enter an API key to load live empire data.
        </Box>
      ) : null}

      <Grid container spacing={2}>
        <Grid size={{ xs: 12, md: 4 }}>
          <Card sx={{ height: '100%' }}>
            <CardContent>
              <Typography variant="overline" color="text.secondary">
                Current player
              </Typography>
              {currentPlayer.isLoading ? <CircularProgress size={24} /> : null}
              {currentPlayer.error !== null ? (
                <Box
                  role="alert"
                  sx={{
                    border: 1,
                    borderColor: 'error.main',
                    borderRadius: 1,
                    p: 2,
                  }}
                >
                  {formatError(currentPlayer.error)}
                </Box>
              ) : null}
              <Typography variant="h5">
                {currentPlayer.data?.name ?? 'Not loaded'}
              </Typography>
            </CardContent>
          </Card>
        </Grid>
        <Grid size={{ xs: 12, md: 4 }}>
          <Card sx={{ height: '100%' }}>
            <CardContent>
              <Typography variant="overline" color="text.secondary">
                Current backend endpoints
              </Typography>
              {solarSystems.isLoading ? <CircularProgress size={24} /> : null}
              {solarSystems.error !== null ? (
                <Box
                  role="alert"
                  sx={{
                    border: 1,
                    borderColor: 'error.main',
                    borderRadius: 1,
                    p: 2,
                  }}
                >
                  {formatError(solarSystems.error)}
                </Box>
              ) : null}
              <Typography variant="h5">
                {solarSystems.data?.length ?? 0} solar systems
              </Typography>
            </CardContent>
          </Card>
        </Grid>
        <Grid size={{ xs: 12, md: 4 }}>
          <Card sx={{ height: '100%' }}>
            <CardContent>
              <Typography variant="overline" color="text.secondary">
                Epoch 1 placeholders
              </Typography>
              <Typography variant="body1">
                Buildings, ships, fleets, energy, scoring, and alerts are
                documented but not implemented in the backend yet.
              </Typography>
            </CardContent>
          </Card>
        </Grid>
      </Grid>
    </Stack>
  );
}
