using Xunit;

namespace Voidforge.SoakTests;

// xUnit collection for the input-starvation scenario (its own fixture => own DB + theme + host boot).
// Runs serially with every other soak collection in-process — see AssemblyInfo's DisableTestParallelization.
// Parallelism is one process per scenario (scripts/soak-matrix.sh).
#pragma warning disable CA1711 // xUnit collection definition types conventionally end in 'Collection'
[CollectionDefinition(Name)]
public sealed class InputStarvationCollection : ICollectionFixture<InputStarvationFixture>
{
    public const string Name = "Soak:InputStarvation";
}
#pragma warning restore CA1711
