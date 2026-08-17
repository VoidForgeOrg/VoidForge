using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Xunit;

namespace Voidforge.Tests.Travel;

[Trait("Category", "Unit")]
public sealed class PlanetCoordinateTests
{
    [Fact]
    public void PlanetCreatedSetsCoordinates()
    {
        var planet = new Planet();
        planet.Apply(new PlanetCreated("P", Guid.NewGuid(), 1000, 5, 1000, 1000, 12.5m, -3m, 990m));

        Assert.Equal(new Coordinates(12.5m, -3m, 990m), planet.GetCoordinates());
    }
}
