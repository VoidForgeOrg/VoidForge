import { describe, expect, it } from 'vitest';

import viteConfig from '../../../vite.config';

describe('Vite config', () => {
  it('proxies same-origin API calls to the local backend in dev', () => {
    expect(viteConfig).toMatchObject({
      server: {
        proxy: {
          '/api': {
            changeOrigin: true,
            target: 'http://localhost:5000',
          },
        },
      },
    });
  });
});
