using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Voidforge.Api.Documents;

namespace Voidforge.Api.Auth;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IQuerySession querySession)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyAuthenticationDefaults.HeaderName, out var headerValues))
        {
            return AuthenticateResult.NoResult();
        }

        var rawKey = headerValues.ToString();
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return AuthenticateResult.NoResult();
        }

        var hashedKey = HashKey(rawKey);

        var apiKey = await querySession.Query<ApiKey>()
            .FirstOrDefaultAsync(k => k.HashedKey == hashedKey);

        if (apiKey is null)
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, apiKey.PlayerId.ToString()),
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }

    internal static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexStringLower(bytes);
    }
}
