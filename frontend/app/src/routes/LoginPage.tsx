import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Card from '@mui/material/Card';
import CardContent from '@mui/material/CardContent';
import Container from '@mui/material/Container';
import MuiLink from '@mui/material/Link';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { Link as RouterLink } from '@tanstack/react-router';
import { type SyntheticEvent, useState } from 'react';

import { useSessionStore } from '../features/auth/sessionStore';
import { useRegisterPlayer } from '../shared/api/hooks';

export function LoginPage() {
  const apiKey = useSessionStore((state) => state.apiKey);
  const setApiKey = useSessionStore((state) => state.setApiKey);
  const [apiKeyInput, setApiKeyInput] = useState(apiKey ?? '');
  const [playerName, setPlayerName] = useState('');
  const registerPlayer = useRegisterPlayer();

  function saveApiKey(event: SyntheticEvent<HTMLFormElement>) {
    event.preventDefault();

    const trimmedApiKey = apiKeyInput.trim();
    if (trimmedApiKey.length > 0) {
      setApiKey(trimmedApiKey);
    }
  }

  function register(event: SyntheticEvent<HTMLFormElement>) {
    event.preventDefault();

    const trimmedPlayerName = playerName.trim();
    if (trimmedPlayerName.length > 0) {
      registerPlayer.mutate({ name: trimmedPlayerName });
    }
  }

  return (
    <Container maxWidth="sm" sx={{ py: 8 }}>
      <Stack spacing={3}>
        <Box>
          <Typography component="h1" variant="h3" gutterBottom>
            Voidforge
          </Typography>
          <Typography color="text.secondary">
            Enter an existing API key or register a new player against the
            current backend.
          </Typography>
        </Box>

        {apiKey !== null ? (
          <Box
            role="status"
            sx={{
              border: 1,
              borderColor: 'success.main',
              borderRadius: 1,
              p: 2,
            }}
          >
            <Typography variant="body2">
              API key is stored locally for this browser.{' '}
              <MuiLink component={RouterLink} to="/app">
                Open app
              </MuiLink>
            </Typography>
          </Box>
        ) : null}

        <Card>
          <CardContent>
            <Box component="form" onSubmit={saveApiKey}>
              <Stack spacing={2}>
                <Typography component="h2" variant="h6">
                  Existing player
                </Typography>
                <TextField
                  id="api-key"
                  label="API key"
                  value={apiKeyInput}
                  onChange={(event) => {
                    setApiKeyInput(event.target.value);
                  }}
                  placeholder="vf_..."
                  fullWidth
                />
                <Button type="submit" variant="contained" size="large">
                  Save API key
                </Button>
              </Stack>
            </Box>
          </CardContent>
        </Card>

        <Card>
          <CardContent>
            <Box component="form" onSubmit={register}>
              <Stack spacing={2}>
                <Typography component="h2" variant="h6">
                  New player
                </Typography>
                <TextField
                  id="player-name"
                  label="Player name"
                  value={playerName}
                  onChange={(event) => {
                    setPlayerName(event.target.value);
                  }}
                  fullWidth
                />
                <Button
                  type="submit"
                  disabled={registerPlayer.isPending}
                  variant="outlined"
                  size="large"
                >
                  Register player
                </Button>
                {registerPlayer.data !== undefined ? (
                  <Box
                    role="status"
                    sx={{
                      border: 1,
                      borderColor: 'success.main',
                      borderRadius: 1,
                      p: 2,
                    }}
                  >
                    Registered. Homeworld ID: {registerPlayer.data.homeworldId}
                  </Box>
                ) : null}
                {registerPlayer.error !== null ? (
                  <Box
                    role="alert"
                    sx={{
                      border: 1,
                      borderColor: 'error.main',
                      borderRadius: 1,
                      p: 2,
                    }}
                  >
                    {registerPlayer.error.message}
                  </Box>
                ) : null}
              </Stack>
            </Box>
          </CardContent>
        </Card>
      </Stack>
    </Container>
  );
}
