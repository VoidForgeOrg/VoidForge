import Card from '@mui/material/Card';
import CardContent from '@mui/material/CardContent';
import CircularProgress from '@mui/material/CircularProgress';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import Box from '@mui/material/Box';

import { useSessionStore } from '../features/auth/sessionStore';
import { usePlanet } from '../shared/api/hooks';

interface PlanetPageProps {
  planetId: string;
}

function formatError(error: unknown) {
  return error instanceof Error ? error.message : 'Unknown API error';
}

export function PlanetPage({ planetId }: PlanetPageProps) {
  const apiKey = useSessionStore((state) => state.apiKey);
  const planet = usePlanet(planetId);

  return (
    <Stack spacing={3}>
      <Stack spacing={1}>
        <Typography component="h1" variant="h4">
          Planet Detail
        </Typography>
        <Typography color="text.secondary">{planetId}</Typography>
      </Stack>

      {apiKey === null ? (
        <Box
          role="status"
          sx={{ border: 1, borderColor: 'info.main', borderRadius: 1, p: 2 }}
        >
          Enter an API key to load planet data.
        </Box>
      ) : null}
      {planet.isLoading ? <CircularProgress /> : null}
      {planet.error !== null ? (
        <Box
          role="alert"
          sx={{ border: 1, borderColor: 'error.main', borderRadius: 1, p: 2 }}
        >
          {formatError(planet.error)}
        </Box>
      ) : null}

      <Card>
        <CardContent>
          <Typography variant="overline" color="text.secondary">
            Current backend fields
          </Typography>
          <Typography variant="h5">
            {planet.data?.name ?? 'Not loaded'}
          </Typography>
          {planet.data !== undefined ? (
            <Stack spacing={1} sx={{ mt: 2 }}>
              <Typography>Iron Ore: {planet.data.ironOreStored}</Typography>
              <Typography>
                Iron Ingots: {planet.data.ironIngotStored}
              </Typography>
              <Typography>
                Building slots: {planet.data.buildingSlotCount}
              </Typography>
            </Stack>
          ) : null}
        </CardContent>
      </Card>
    </Stack>
  );
}
