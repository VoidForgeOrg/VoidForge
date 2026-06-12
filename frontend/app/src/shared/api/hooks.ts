import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { useSessionStore } from '../../features/auth/sessionStore';
import { createApiClient } from './client';
import {
  type RegisterPlayerRequest,
  planetSchema,
  playerInfoSchema,
  registerPlayerResponseSchema,
  solarSystemsSchema,
} from './schemas';

export const queryKeys = {
  currentPlayerRoot: ['current-player'] as const,
  currentPlayer: (apiKey: string | null) =>
    [...queryKeys.currentPlayerRoot, apiKey ?? 'anonymous'] as const,
  solarSystems: ['solar-systems'] as const,
  planet: (planetId: string) => ['planet', planetId] as const,
};

function useApiClient() {
  const apiKey = useSessionStore((state) => state.apiKey);

  return {
    api: createApiClient({
      getApiKey: () => apiKey,
    }),
    apiKey,
    hasApiKey: apiKey !== null && apiKey.length > 0,
  };
}

export function useCurrentPlayer() {
  const { api, apiKey, hasApiKey } = useApiClient();

  return useQuery({
    queryKey: queryKeys.currentPlayer(apiKey),
    queryFn: () => api.get('/api/players/me', playerInfoSchema),
    enabled: hasApiKey,
  });
}

export function useSolarSystems() {
  const { api, hasApiKey } = useApiClient();

  return useQuery({
    queryKey: queryKeys.solarSystems,
    queryFn: () => api.get('/api/solar-systems', solarSystemsSchema),
    enabled: hasApiKey,
  });
}

export function usePlanet(planetId: string) {
  const { api, hasApiKey } = useApiClient();

  return useQuery({
    queryKey: queryKeys.planet(planetId),
    queryFn: () => api.get(`/api/planets/${planetId}`, planetSchema),
    enabled: hasApiKey && planetId.length > 0,
  });
}

export function useRegisterPlayer() {
  const { api } = useApiClient();
  const queryClient = useQueryClient();
  const setApiKey = useSessionStore((state) => state.setApiKey);

  return useMutation({
    mutationFn: (request: RegisterPlayerRequest) =>
      api.post('/api/players/register', request, registerPlayerResponseSchema),
    onSuccess: (response) => {
      queryClient.removeQueries({ queryKey: queryKeys.currentPlayerRoot });
      setApiKey(response.apiKey);
    },
  });
}
