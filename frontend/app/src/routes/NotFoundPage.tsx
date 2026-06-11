import Box from '@mui/material/Box';
import Container from '@mui/material/Container';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { Link } from '@tanstack/react-router';

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
        <Link to="/app">
          <Box
            component="span"
            sx={{
              bgcolor: 'primary.main',
              borderRadius: 1,
              color: 'primary.contrastText',
              display: 'inline-block',
              fontWeight: 700,
              px: 2,
              py: 1,
            }}
          >
            Return to empire
          </Box>
        </Link>
      </Stack>
    </Container>
  );
}
