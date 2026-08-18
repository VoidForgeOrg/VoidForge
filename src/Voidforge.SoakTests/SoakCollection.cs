using Xunit;

namespace Voidforge.SoakTests;

#pragma warning disable CA1711 // xUnit collection definition types conventionally end in 'Collection'
[CollectionDefinition(SoakCollection.Name)]
public sealed class SoakCollection : ICollectionFixture<SoakHostFixture>
{
    public const string Name = "Soak";
}
#pragma warning restore CA1711
