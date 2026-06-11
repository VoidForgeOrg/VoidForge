import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      'react-transition-group/TransitionGroupContext':
        'react-transition-group/cjs/TransitionGroupContext.js',
    },
  },
  server: {
    port: 5173,
  },
  test: {
    environment: 'jsdom',
    environmentOptions: {
      jsdom: {
        url: 'http://localhost:5173',
      },
    },
    include: ['src/**/*.test.{ts,tsx}'],
    server: {
      deps: {
        inline: [
          '@mui/material',
          '@mui/system',
          '@mui/utils',
          'react-transition-group',
        ],
      },
    },
    setupFiles: ['src/test/setup.ts'],
  },
});
