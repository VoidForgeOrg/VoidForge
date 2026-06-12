import Button from '@mui/material/Button';
import Card from '@mui/material/Card';
import CardContent from '@mui/material/CardContent';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useQueryClient } from '@tanstack/react-query';

import { useSessionStore } from '../features/auth/sessionStore';
import { queryKeys } from '../shared/api/hooks';

export function ApiDebugPage() {
  const apiKey = useSessionStore((state) => state.apiKey);
  const clearApiKey = useSessionStore((state) => state.clearApiKey);
  const queryClient = useQueryClient();
  const apiConnected = apiKey !== null;

  function clearSession() {
    queryClient.removeQueries({ queryKey: queryKeys.currentPlayerRoot });
    clearApiKey();
  }

  return (
    <Stack spacing={3}>
      <Stack spacing={1}>
        <Typography component="h1" variant="h4">
          API / Debug
        </Typography>
        <Typography color="text.secondary">
          Inspect the local API session state for this browser.
        </Typography>
      </Stack>

      <Card>
        <CardContent>
          <Stack spacing={2}>
            <Stack spacing={0.5}>
              <Typography
                color={apiConnected ? 'success.main' : 'warning.main'}
                variant="h6"
              >
                {apiConnected ? 'API connected' : 'API disconnected'}
              </Typography>
              <Typography color="text.secondary">
                {apiConnected
                  ? 'An API key is stored locally.'
                  : 'No API key is stored locally.'}
              </Typography>
            </Stack>

            {apiConnected ? (
              <Button
                type="button"
                variant="outlined"
                color="warning"
                onClick={clearSession}
                sx={{ alignSelf: 'flex-start' }}
              >
                Clear API key
              </Button>
            ) : null}
          </Stack>
        </CardContent>
      </Card>
    </Stack>
  );
}
