using System.Security.Claims;
using System.Text.Encodings.Web;
using Helpdesk.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Helpdesk.Api.Security;

/// <summary>
/// Local-development authentication only: trusts X-Dev-Tenant / X-Dev-User headers.
/// Registered exclusively when the environment is Development and Auth:UseDevAuth is true.
/// </summary>
public sealed class DevAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DevAuth";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Browsers cannot set headers on a WebSocket, so the hub also accepts the same values as query parameters.
        var isHub = Request.Path.StartsWithSegments(Chat.ChatHub.Path);
        var tenant = Request.Headers["X-Dev-Tenant"].FirstOrDefault()
            ?? (isHub ? Request.Query["x-dev-tenant"].FirstOrDefault() : null) ?? "dev-tenant";
        var user = Request.Headers["X-Dev-User"].FirstOrDefault()
            ?? (isHub ? Request.Query["x-dev-user"].FirstOrDefault() : null) ?? "dev-user";

        // X-Dev-Roles: comma-separated roles; defaults to the status-report operator role so /status works locally.
        var roles = (Request.Headers["X-Dev-Roles"].FirstOrDefault() ?? Program.DefaultStatusRole)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var identity = new ClaimsIdentity(
        [
            new Claim(JwtTenantProvider.TenantClaim, tenant),
            new Claim(ClaimTypes.NameIdentifier, user),
            new Claim(ClaimTypes.Name, user),
            ..roles.Select(r => new Claim(ClaimTypes.Role, r))
        ], SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

