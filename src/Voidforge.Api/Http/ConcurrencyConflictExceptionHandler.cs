using JasperFx;
using Microsoft.AspNetCore.Diagnostics;

namespace Voidforge.Api.Http;

// Maps Marten's optimistic-concurrency failure to 409 Conflict (#39). Same-planet-stream command
// endpoints use FetchForWriting, so a losing concurrent append throws a ConcurrencyException on
// commit. That commit is issued by Wolverine's transactional middleware *after* the endpoint method
// returns, so it cannot be caught inside the endpoint — this handler turns it into a retryable 409
// instead of a 500. Scheduled message handlers are unaffected: their conflicts are retried via the
// Wolverine OnException policy in Program.cs.
//
// Emits the 409 as a ProblemDetails through the registered IProblemDetailsService (D12/#74) so it
// carries the exact same shape — Instance, traceId, framework-defaulted title/type — as every
// endpoint's TypedResults.Problem responses.
internal sealed class ConcurrencyConflictExceptionHandler(IProblemDetailsService problemDetailsService)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ConcurrencyException || httpContext.Response.HasStarted)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails =
            {
                Status = StatusCodes.Status409Conflict,
                Detail = "Concurrent modification of this resource; please retry.",
            },
        });
    }
}
