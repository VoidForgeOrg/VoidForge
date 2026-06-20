import Box from '@mui/material/Box';
import { Outlet } from '@tanstack/react-router';

export function RootLayout() {
  return (
    <Box sx={{ minHeight: '100vh', bgcolor: 'background.default' }}>
      <Outlet />
    </Box>
  );
}
