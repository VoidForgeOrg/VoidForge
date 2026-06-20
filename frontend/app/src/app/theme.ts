import { createTheme } from '@mui/material/styles';

export const theme = createTheme({
  palette: {
    mode: 'dark',
    background: {
      default: '#070b14',
      paper: '#101827',
    },
    primary: {
      main: '#7dd3fc',
    },
    secondary: {
      main: '#c084fc',
    },
  },
  typography: {
    fontFamily:
      'ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif',
  },
});
