import { useMutation, useQuery } from '@tanstack/react-query';

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
  currentPlayer: ['current-player'] as const,
  solarSystems: ['solar-systems'] as const,
  planet: (planetId: string) => ['planet', planetId] as const,
};

function useApiClient() {
  const apiKey = useSessionStore((state) => state.apiKey);

  return {
    api: createApiClient({
      getApiKey: () => apiKey,
    }),
    hasApiKey: apiKey !== null && apiKey.length > 0,
  };
}

export function useCurrentPlayer() {
  const { api, hasApiKey } = useApiClient();

  return useQuery({
    queryKey: queryKeys.currentPlayer,
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
  const setApiKey = useSessionStore((state) => state.setApiKey);

  return useMutation({
    mutationFn: (request: RegisterPlayerRequest) =>
      api.post('/api/players/register', request, registerPlayerResponseSchema),
    onSuccess: (response) => {
      setApiKey(response.apiKey);
    },
  });
}
