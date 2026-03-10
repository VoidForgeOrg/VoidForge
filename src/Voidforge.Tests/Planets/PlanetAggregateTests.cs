using Voidforge.Api.Domain;
using Voidforge.Api.Domain.Events;
using Xunit;

namespace Voidforge.Tests.Planets;

public sealed class PlanetAggregateTests
{
    [Fact]
    public void ApplyPlanetCreatedSetsAllProperties()
    {
        var planet = new Planet();
        var solarSystemId = Guid.NewGuid();

        planet.Apply(new PlanetCreated(
            Name: "Test Planet",
            SolarSystemId: solarSystemId,
            IronOrePool: 50000,
            BuildingSlotCount: 6,
            IronOreStorageCapacity: 10000,
            IronIngotStorageCapacity: 5000));

        Assert.Equal("Test Planet", planet.Name);
        Assert.Equal(solarSystemId, planet.SolarSystemId);
        Assert.Equal(50000, planet.IronOrePool);
        Assert.Equal(6, planet.BuildingSlotCount);
        Assert.Equal(10000, planet.IronOreStorageCapacity);
        Assert.Equal(5000, planet.IronIngotStorageCapacity);
    }

    [Fact]
    public void NewPlanetHasNullOwner()
    {
        var planet = new Planet();

        Assert.Null(planet.OwnerId);
    }

    [Fact]
    public void NewPlanetHasDefaultValues()
    {
        var planet = new Planet();

        Assert.Equal(Guid.Empty, planet.Id);
        Assert.Equal(string.Empty, planet.Name);
        Assert.Equal(Guid.Empty, planet.SolarSystemId);
        Assert.Null(planet.OwnerId);
        Assert.Equal(0, planet.IronOrePool);
        Assert.Equal(0, planet.BuildingSlotCount);
        Assert.Equal(0, planet.IronOreStorageCapacity);
        Assert.Equal(0, planet.IronIngotStorageCapacity);
        Assert.Equal(0, planet.IronOreStored);
        Assert.Equal(0, planet.IronIngotStored);
    }

    [Fact]
    public void ApplyPlanetColonizedSetsOwnerAndResources()
    {
        var planet = new Planet();
        var ownerId = Guid.NewGuid();

        planet.Apply(new PlanetColonized(
            OwnerId: ownerId,
            IronOreStored: 500,
            IronIngotStored: 100));

        Assert.Equal(ownerId, planet.OwnerId);
        Assert.Equal(500, planet.IronOreStored);
        Assert.Equal(100, planet.IronIngotStored);
    }
}
