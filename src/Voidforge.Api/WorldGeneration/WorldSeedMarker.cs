namespace Voidforge.Api.WorldGeneration;

/// <summary>Single-row marker committed atomically with the seeded world so a second
/// concurrent seeder collides on the primary key (23505) instead of double-seeding.</summary>
public sealed class WorldSeedMarker
{
    // Well-known constant id — there is only ever one of these.
    public static readonly Guid WellKnownId = new("5eed0000-0000-0000-0000-000000000001");

    public Guid Id { get; set; }
}
