import { z } from 'zod';

import { schemas } from './schemas.gen';

// Stable, app-facing aliases over the OpenAPI-generated schemas (schemas.gen.ts).
// The generated names mirror the backend response DTOs; these aliases keep call
// sites decoupled from the generator's naming and compose collection shapes.
export const planetSchema = schemas.PlanetResponse;
export const playerInfoSchema = schemas.PlayerInfoResponse;
export const registerPlayerResponseSchema = schemas.RegisterPlayerResponse;
export const solarSystemSchema = schemas.SolarSystemResponse;
export const solarSystemsSchema = z.array(solarSystemSchema);

export type RegisterPlayerRequest = z.infer<
  typeof schemas.RegisterPlayerRequest
>;
