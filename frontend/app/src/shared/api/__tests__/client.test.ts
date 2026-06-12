import { describe, expect, it } from 'vitest';

import { ApiError, createApiClient } from '../client';
import { playerInfoSchema } from '../schemas';

describe('API client', () => {
  it('uses same-origin API paths when no base URL is configured', async () => {
    let capturedInput: RequestInfo | URL | undefined;
    const client = createApiClient({
      getApiKey: () => null,
      fetcher: (input) => {
        capturedInput = input;
        return Promise.resolve(
          new Response(
            JSON.stringify({
              id: '018f4c8a-3f10-7cc5-b802-cd2f7ba2b8f4',
              name: 'Kedar',
              registeredAt: '2026-06-10T12:00:00Z',
            }),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          ),
        );
      },
    });

    await client.get('/api/players/me', playerInfoSchema);

    expect(capturedInput).toBe('/api/players/me');
  });

  it('sends the API key header and parses successful responses', async () => {
    const capturedRequests: Request[] = [];
    const client = createApiClient({
      baseUrl: 'https://api.voidforge.test',
      getApiKey: () => 'vf_test_key',
      fetcher: (input, init) => {
        capturedRequests.push(new Request(input, init));
        return Promise.resolve(
          new Response(
            JSON.stringify({
              id: '018f4c8a-3f10-7cc5-b802-cd2f7ba2b8f4',
              name: 'Kedar',
              registeredAt: '2026-06-10T12:00:00Z',
            }),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          ),
        );
      },
    });

    const result = await client.get('/api/players/me', playerInfoSchema);

    expect(result.name).toBe('Kedar');
    expect(capturedRequests[0]?.url).toBe(
      'https://api.voidforge.test/api/players/me',
    );
    expect(capturedRequests[0]?.headers.get('X-API-Key')).toBe('vf_test_key');
  });

  it('throws an ApiError for failed responses', async () => {
    const client = createApiClient({
      baseUrl: 'https://api.voidforge.test',
      getApiKey: () => null,
      fetcher: () => Promise.resolve(new Response('No key', { status: 401 })),
    });

    await expect(
      client.get('/api/players/me', playerInfoSchema),
    ).rejects.toEqual(new ApiError(401, 'No key'));
  });
});
