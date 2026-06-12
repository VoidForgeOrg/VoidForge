import { z } from 'zod';

const guidSchema = z.uuid();

export const registerPlayerRequestSchema = z.object({
  name: z.string().min(1),
});

export const registerPlayerResponseSchema = z.object({
  playerId: guidSchema,
  apiKey: z.string().min(1),
  homeworldId: guidSchema,
});

export const playerInfoSchema = z.object({
  id: guidSchema,
  name: z.string().min(1),
  registeredAt: z.string().min(1),
});

export const solarSystemSchema = z.object({
  id: guidSchema,
  name: z.string().min(1),
  x: z.number(),
  y: z.number(),
  z: z.number(),
  planetIds: z.array(guidSchema),
});

export const solarSystemsSchema = z.array(solarSystemSchema);

export const resourcePoolSchema = z.object({
  currentValue: z.number().nonnegative(),
  rate: z.number(),
  storageCapacity: z.number().nonnegative(),
});

export const planetSchema = z.object({
  id: guidSchema,
  name: z.string().min(1),
  solarSystemId: guidSchema,
  ownerId: guidSchema.nullable(),
  ironOrePool: z.number().int().nonnegative(),
  buildingSlotCount: z.number().int().nonnegative(),
  ironOre: resourcePoolSchema,
  ironIngot: resourcePoolSchema,
});

export type RegisterPlayerRequest = z.infer<typeof registerPlayerRequestSchema>;
export type RegisterPlayerResponse = z.infer<
  typeof registerPlayerResponseSchema
>;
export type PlayerInfo = z.infer<typeof playerInfoSchema>;
export type SolarSystem = z.infer<typeof solarSystemSchema>;
export type ResourcePool = z.infer<typeof resourcePoolSchema>;
export type Planet = z.infer<typeof planetSchema>;
