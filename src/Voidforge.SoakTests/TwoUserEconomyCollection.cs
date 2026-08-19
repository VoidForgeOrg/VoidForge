using Xunit;

namespace Voidforge.SoakTests;

// xUnit collection for the two-user economy scenario (its own fixture => own DB + theme + host boot).
// Runs serially with every other soak collection in-process — see AssemblyInfo's DisableTestParallelization
// (the host uses a process-global economy table + process-global env config). Parallelism is one process
// per scenario (scripts/soak-matrix.sh).
#pragma warning disable CA1711 // xUnit collection definition types conventionally end in 'Collection'
[CollectionDefinition(Name)]
public sealed class TwoUserEconomyCollection : ICollectionFixture<TwoUserEconomyFixture>
{
    public const string Name = "Soak:TwoUserEconomy";
}
#pragma warning restore CA1711
