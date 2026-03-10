namespace Voidforge.Api.Documents;

public sealed class SolarSystem
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public decimal X { get; set; }
    public decimal Y { get; set; }
    public decimal Z { get; set; }
    public IList<Guid> PlanetIds { get; set; } = [];
}
