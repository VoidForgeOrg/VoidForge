using Voidforge.Api.Pagination;
using Xunit;

namespace Voidforge.Tests.Pagination;

public sealed class PaginationContractTests
{
    [Fact]
    public void CreateAppliesDefaults()
    {
        var p = PaginationParameters.Create();
        Assert.NotNull(p);
        Assert.Equal(1, p.Page);
        Assert.Equal(50, p.PageSize);
    }

    [Fact]
    public void CreateClampsPageSizeToMaximum()
    {
        var p = PaginationParameters.Create(1, 500);
        Assert.NotNull(p);
        Assert.Equal(200, p.PageSize);
    }

    [Fact]
    public void CreateAcceptsMaximumPageSize()
    {
        var p = PaginationParameters.Create(1, 200);
        Assert.NotNull(p);
        Assert.Equal(200, p.PageSize);
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(-1, 50)]
    [InlineData(1, 0)]
    [InlineData(1, -5)]
    public void CreateRejectsOutOfRangeParameters(int page, int pageSize)
    {
        Assert.Null(PaginationParameters.Create(page, pageSize));
    }

    [Fact]
    public void EnvelopeComputesMetadata()
    {
        var response = new PagedResponse<int>([1, 2, 3], Page: 1, PageSize: 50, TotalItems: 1234);
        Assert.Equal(25, response.TotalPages);   // ceil(1234 / 50)
        Assert.False(response.HasPrevious);
        Assert.True(response.HasNext);
    }

    [Fact]
    public void EnvelopeMiddlePageHasBothNeighbours()
    {
        var response = new PagedResponse<int>([1], Page: 3, PageSize: 10, TotalItems: 55);
        Assert.Equal(6, response.TotalPages);     // ceil(55 / 10)
        Assert.True(response.HasPrevious);
        Assert.True(response.HasNext);
    }

    [Fact]
    public void EnvelopeEmptyResultHasNoPages()
    {
        var response = new PagedResponse<int>([], Page: 1, PageSize: 50, TotalItems: 0);
        Assert.Equal(0, response.TotalPages);
        Assert.False(response.HasPrevious);
        Assert.False(response.HasNext);
    }

    [Fact]
    public void EnvelopeLastPageHasPreviousButNoNext()
    {
        var response = new PagedResponse<int>([1, 2], Page: 3, PageSize: 5, TotalItems: 12);
        Assert.Equal(3, response.TotalPages);     // ceil(12 / 5)
        Assert.True(response.HasPrevious);
        Assert.False(response.HasNext);
    }
}
