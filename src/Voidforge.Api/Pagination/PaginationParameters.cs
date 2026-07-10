namespace Voidforge.Api.Pagination;

// Validated pagination query parameters. Created via the factory so the contract
// (defaults, clamp, rejection) lives in one place and is unit-testable without HTTP.
public sealed record PaginationParameters
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public int Page { get; }
    public int PageSize { get; }

    private PaginationParameters(int page, int pageSize)
    {
        Page = page;
        PageSize = pageSize;
    }

    // Returns null when page < 1 or pageSize < 1 (the caller maps null to 400).
    // pageSize above MaxPageSize is clamped rather than rejected.
    public static PaginationParameters? Create(int page = DefaultPage, int pageSize = DefaultPageSize)
    {
        if (page < 1 || pageSize < 1)
        {
            return null;
        }

        return new PaginationParameters(page, Math.Min(pageSize, MaxPageSize));
    }
}
