using Marten.Pagination;

namespace Voidforge.Api.Pagination;

public static class PaginationExtensions
{
    // Document queries: wraps Marten's ToPagedListAsync (items + total count in one
    // round-trip). The query MUST already carry a deterministic OrderBy. The selector
    // projects each materialized document to its response DTO.
    public static async Task<PagedResponse<TResult>> ToPagedResponseAsync<TSource, TResult>(
        this IQueryable<TSource> query,
        PaginationParameters parameters,
        Func<TSource, TResult> selector,
        CancellationToken token = default)
    {
        var paged = await query.ToPagedListAsync(parameters.Page, parameters.PageSize, token);
        var items = paged.Select(selector).ToList();
        return new PagedResponse<TResult>(items, parameters.Page, parameters.PageSize, paged.TotalItemCount);
    }

    // Aggregate child collections (e.g. the ship roster/queue in #27) that are already
    // materialized in memory. Same envelope; the migration candidate for keyset if a
    // collection grows large (see api-conventions.md).
    public static PagedResponse<TResult> ToPagedResponse<TSource, TResult>(
        this IReadOnlyList<TSource> source,
        PaginationParameters parameters,
        Func<TSource, TResult> selector)
    {
        var items = source
            .Skip((parameters.Page - 1) * parameters.PageSize)
            .Take(parameters.PageSize)
            .Select(selector)
            .ToList();
        return new PagedResponse<TResult>(items, parameters.Page, parameters.PageSize, source.Count);
    }
}
