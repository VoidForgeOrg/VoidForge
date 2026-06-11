import { type z } from 'zod';

function readDefaultBaseUrl(): string {
  const configuredBaseUrl: unknown = import.meta.env.VITE_API_BASE_URL;

  return typeof configuredBaseUrl === 'string' && configuredBaseUrl.length > 0
    ? configuredBaseUrl
    : 'http://localhost:5000';
}

const defaultBaseUrl = readDefaultBaseUrl();

type Fetcher = (
  input: RequestInfo | URL,
  init?: RequestInit,
) => Promise<Response>;

interface ApiClientOptions {
  baseUrl?: string;
  getApiKey?: () => string | null;
  fetcher?: Fetcher;
}

interface ApiClient {
  get: <TResponse>(
    path: string,
    schema: z.ZodType<TResponse>,
  ) => Promise<TResponse>;
  post: <TResponse>(
    path: string,
    body: unknown,
    schema: z.ZodType<TResponse>,
  ) => Promise<TResponse>;
}

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

export function createApiClient(options: ApiClientOptions = {}): ApiClient {
  const baseUrl = options.baseUrl ?? defaultBaseUrl;
  const fetcher = options.fetcher ?? fetch;
  const getApiKey = options.getApiKey ?? (() => null);

  async function request<TResponse>(
    method: 'GET' | 'POST',
    path: string,
    schema: z.ZodType<TResponse>,
    body?: unknown,
  ): Promise<TResponse> {
    const headers = new Headers({ Accept: 'application/json' });
    const apiKey = getApiKey();

    if (apiKey !== null && apiKey.length > 0) {
      headers.set('X-API-Key', apiKey);
    }

    if (body !== undefined) {
      headers.set('Content-Type', 'application/json');
    }

    const response = await fetcher(new URL(path, baseUrl), {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
    });

    if (!response.ok) {
      const message = (await response.text()) || response.statusText;
      throw new ApiError(response.status, message);
    }

    const data: unknown = await response.json();
    return schema.parse(data);
  }

  return {
    get: (path, schema) => request('GET', path, schema),
    post: (path, body, schema) => request('POST', path, schema, body),
  };
}
