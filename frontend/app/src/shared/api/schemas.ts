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

export const planetSchema = z.object({
  id: guidSchema,
  name: z.string().min(1),
  solarSystemId: guidSchema,
  ownerId: guidSchema.nullable(),
  ironOrePool: z.number().int().nonnegative(),
  buildingSlotCount: z.number().int().nonnegative(),
  ironOreStorageCapacity: z.number().int().nonnegative(),
  ironIngotStorageCapacity: z.number().int().nonnegative(),
  ironOreStored: z.number().int().nonnegative(),
  ironIngotStored: z.number().int().nonnegative(),
});

export type RegisterPlayerRequest = z.infer<typeof registerPlayerRequestSchema>;
export type RegisterPlayerResponse = z.infer<
  typeof registerPlayerResponseSchema
>;
export type PlayerInfo = z.infer<typeof playerInfoSchema>;
export type SolarSystem = z.infer<typeof solarSystemSchema>;
export type Planet = z.infer<typeof planetSchema>;
