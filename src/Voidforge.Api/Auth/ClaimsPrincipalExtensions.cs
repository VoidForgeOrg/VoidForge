using System.Security.Claims;

namespace Voidforge.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    // The single source of the player-id claim parse (D11). Every endpoint that needs the
    // caller's player id goes through here. Returns null when the principal carries no
    // parseable NameIdentifier claim; ownership call sites treat null as "not the owner".
    public static Guid? PlayerId(this ClaimsPrincipal principal)
        => Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
