using Xunit;

namespace Voidforge.Tests;

#pragma warning disable CA1711 // xUnit collection definition types conventionally end in 'Collection'
[CollectionDefinition(IntegrationCollection.Name)]
public sealed class IntegrationCollection : ICollectionFixture<AppFixture>
{
    public const string Name = "App";
}
#pragma warning restore CA1711
