using JasperFx;
using Microsoft.AspNetCore.Diagnostics;

namespace Voidforge.Api.Http;

// Maps Marten's optimistic-concurrency failure to 409 Conflict (#39). Same-planet-stream command
// endpoints use FetchForWriting, so a losing concurrent append throws a ConcurrencyException on
// commit. That commit is issued by Wolverine's transactional middleware *after* the endpoint method
// returns, so it cannot be caught inside the endpoint — this handler turns it into a retryable 409
// instead of a 500. Scheduled message handlers are unaffected: their conflicts are retried via the
// Wolverine OnException policy in Program.cs.
internal sealed class ConcurrencyConflictExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ConcurrencyException || httpContext.Response.HasStarted)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        await httpContext.Response.WriteAsJsonAsync(
            new { detail = "Concurrent modification of this resource; please retry." },
            cancellationToken);
        return true;
    }
}
