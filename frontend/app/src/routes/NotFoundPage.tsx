import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { Link as RouterLink } from '@tanstack/react-router';

export function NotFoundPage() {
  return (
    <Container maxWidth="sm" sx={{ py: 8 }}>
      <Stack spacing={2}>
        <Typography component="h1" variant="h4">
          Route not found
        </Typography>
        <Typography color="text.secondary">
          This frontend shell only includes the initial Epoch 1 routes.
        </Typography>
        <Button
          component={RouterLink}
          to="/app"
          variant="contained"
          sx={{ alignSelf: 'flex-start' }}
        >
          Return to empire
        </Button>
      </Stack>
    </Container>
  );
}
