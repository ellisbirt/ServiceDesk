using System.Security.Claims;
using System.Text.Encodings.Web;
using AgentDesk.Api.Realtime;
using AgentDesk.Core.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AgentDesk.Api.Security;

/// <summary>
/// The <c>Auth</c> section. Three modes, chosen at startup:
/// <list type="bullet">
/// <item><b>dev</b>: Development and <see cref="UseDevAuth"/>. Trusts <c>X-Dev-*</c> headers so you can act as different users.</item>
/// <item><b>anonymous</b>: <see cref="Enforce"/> is false. Every caller is the fixed <see cref="Anonymous"/> identity.</item>
/// <item><b>jwt</b>: <see cref="Enforce"/> is true. Entra ID bearer tokens for the API; single-use tickets for the hub.</item>
/// </list>
/// </summary>
public sealed class AgentDeskAuthOptions
{
    public const string SectionName = "Auth";

    public bool UseDevAuth { get; set; }

    /// <summary>When false (the default today) no sign-in is required. Turn on once an Entra app registration exists.</summary>
    public bool Enforce { get; set; }

    /// <summary>Entra ID authority, e.g. https://login.microsoftonline.com/{tenant-id}/v2.0.</summary>
    public string? Authority { get; set; }

    /// <summary>The API's application ID URI or client id.</summary>
    public string? Audience { get; set; }

    /// <summary>Public SPA settings handed to the browser by <c>GET /api/v1/meta/config</c> for MSAL.</summary>
    public string? SpaClientId { get; set; }
    public string? ApiScope { get; set; }

    public AnonymousIdentityOptions Anonymous { get; set; } = new();
}

public sealed class AnonymousIdentityOptions
{
    public string TenantId { get; set; } = "default";
    public string UserId { get; set; } = "agent";
    public string DisplayName { get; set; } = "Service Desk Agent";
    public string? Email { get; set; }
    public string[] Roles { get; set; } = [AgentDeskRoles.Agent, AgentDeskRoles.KbAuthor, AgentDeskRoles.Admin];
}

public static class AuthModes
{
    public const string Dev = "dev";
    public const string Anonymous = "anonymous";
    public const string Jwt = "jwt";
}

/// <summary>
/// Local-development authentication: trusts <c>X-Dev-Tenant</c>, <c>X-Dev-User</c>, <c>X-Dev-Name</c> and <c>X-Dev-Roles</c>.
/// The hub also accepts them as query parameters (browsers cannot set WebSocket headers). Missing values fall back to the
/// anonymous identity. Registered only in Development with <c>Auth:UseDevAuth</c>.
/// </summary>
public sealed class DevAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IOptions<AgentDeskAuthOptions> auth)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DevAuth";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var fallback = auth.Value.Anonymous;
        var isHub = Request.Path.StartsWithSegments(TicketsHub.Path);
        string? Read(string header) =>
            Request.Headers[header].FirstOrDefault() ?? (isHub ? Request.Query[header.ToLowerInvariant()].FirstOrDefault() : null);

        var roles = Read("X-Dev-Roles")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? fallback.Roles;
        var user = Read("X-Dev-User") ?? fallback.UserId;
        var principal = AnonymousAuthHandler.BuildPrincipal(
            Read("X-Dev-Tenant") ?? fallback.TenantId, user, Read("X-Dev-Name") ?? (user == fallback.UserId ? fallback.DisplayName : user),
            fallback.Email, roles, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

/// <summary>Used while authentication is not enforced: every request is the configured anonymous agent.</summary>
public sealed class AnonymousAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IOptions<AgentDeskAuthOptions> auth)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Anonymous";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var a = auth.Value.Anonymous;
        var principal = BuildPrincipal(a.TenantId, a.UserId, a.DisplayName, a.Email, a.Roles, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    internal static ClaimsPrincipal BuildPrincipal(
        string tenant, string user, string name, string? email, IEnumerable<string> roles, string scheme)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenant),
            new(ClaimTypes.NameIdentifier, user),
            new("name", name)
        };
        if (!string.IsNullOrEmpty(email)) claims.Add(new Claim("email", email));
        claims.AddRange(roles.Select(r => new Claim(AgentDeskClaims.RoleClaim, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme, "name", AgentDeskClaims.RoleClaim));
    }
}
