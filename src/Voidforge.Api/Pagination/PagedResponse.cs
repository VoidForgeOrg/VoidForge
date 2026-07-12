namespace Voidforge.Api.Pagination;

// Generic pagination envelope. TotalPages/HasPrevious/HasNext are computed from the
// primitive fields so every producer path yields identical metadata. Designed so a
// future keyset swap stays non-breaking: clients that follow HasNext keep working.
public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long TotalItems)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalItems / (double)PageSize);
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;
}
