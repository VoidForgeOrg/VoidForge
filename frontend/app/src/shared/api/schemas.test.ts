import { describe, expect, it } from 'vitest';

import {
  planetSchema,
  playerInfoSchema,
  registerPlayerResponseSchema,
  solarSystemSchema,
} from './schemas';

describe('API schemas', () => {
  it('parses a registration response', () => {
    const result = registerPlayerResponseSchema.parse({
      playerId: '018f4c8a-3f10-7cc5-b802-cd2f7ba2b8f4',
      apiKey: 'vf_0123456789abcdef',
      homeworldId: '018f4c8a-3f10-7cc5-b802-cd2f7ba2b8f5',
    });

    expect(result.apiKey).toBe('vf_0123456789abcdef');
  });

  it('parses the current player response', () => {
    const result = playerInfoSchema.parse({
      id: '018f4c8a-3f10-7cc5-b802-cd2f7ba2b8f4',
      name: 'Kedar',
      registeredAt: '2026-06-10T12:00:00Z',
    });

    expect(result.name).toBe('Kedar');
  });

  it('parses a solar system response', () => {
    const result = solarSystemSchema.parse({
      id: '018f4c8a-3f10-7cc5-b802-cd2f7ba2b8f4',
      name: 'Solace',
      x: 12.5,
      y: -30,
      z: 4,
      planetIds: ['018f4c8a-3f10-7cc5-b802-cd2f7ba2b8f5'],
    });

    expect(result.planetIds).toHaveLength(1);
  });

  it('parses a planet response with nullable ownership', () => {
    const result = planetSchema.parse({
      id: '018f4c8a-3f10-7cc5-b802-cd2f7ba2b8f4',
      name: 'Homeworld',
      solarSystemId: '018f4c8a-3f10-7cc5-b802-cd2f7ba2b8f5',
      ownerId: null,
      ironOrePool: 50000,
      buildingSlotCount: 6,
      ironOreStorageCapacity: 10000,
      ironIngotStorageCapacity: 5000,
      ironOreStored: 500,
      ironIngotStored: 100,
    });

    expect(result.ownerId).toBeNull();
  });
});
