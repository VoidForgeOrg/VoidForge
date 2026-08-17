using Xunit;

namespace Voidforge.Tests;

[Trait("Category", "Unit")]
public sealed class SampleUnitTest
{
    [Fact]
    public void TrueIsTrue() => Assert.True(true);
}
