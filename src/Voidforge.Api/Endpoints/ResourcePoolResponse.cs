namespace Voidforge.Api.Endpoints;

public sealed record ResourcePoolResponse(
    decimal CurrentValue,
    decimal Rate,
    decimal StorageCapacity);
