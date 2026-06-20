import Card from '@mui/material/Card';
import CardContent from '@mui/material/CardContent';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';

interface PlaceholderPageProps {
  title: string;
  description: string;
}

export function PlaceholderPage({ description, title }: PlaceholderPageProps) {
  return (
    <Stack spacing={3}>
      <Stack spacing={1}>
        <Typography component="h1" variant="h4">
          {title}
        </Typography>
        <Typography color="text.secondary">{description}</Typography>
      </Stack>
      <Card>
        <CardContent>
          <Typography variant="body1">
            This screen is part of the Epoch 1 navigation skeleton. Detailed
            screen design will be filled in after the app chrome is approved.
          </Typography>
        </CardContent>
      </Card>
    </Stack>
  );
}
