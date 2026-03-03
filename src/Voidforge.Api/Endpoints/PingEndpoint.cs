using Wolverine.Http;

namespace Voidforge.Api.Endpoints;

public static class PingEndpoint
{
    [WolverineGet("/api/ping")]
    public static string Get() => "pong";
}
