using System.Security.Claims;
using Helpdesk.Core.Interfaces;
using Microsoft.AspNetCore.Http;

namespace Helpdesk.Infrastructure.Security;

public class JwtTenantProvider(IHttpContextAccessor httpContextAccessor) : ITenantProvider
{
    public const string TenantClaim = "tenant_id";

    /// <summary>
    /// Tenant and user ids are joined with this character to build SignalR group names, rate-limit keys and
    /// similar composite keys. Refusing it in the ids keeps those keys unambiguous: without the rule, tenant
    /// <c>a:b</c> with user <c>c</c> and tenant <c>a</c> with user <c>b:c</c> would share a key.
    /// </summary>
    public const char KeySeparator = ':';

    /// <summary>True when the principal has a tenant id and a user id that are both usable (present, no <see cref="KeySeparator"/>).</summary>
    public static bool HasValidIdentity(ClaimsPrincipal? user)
    {
        try
        {
            _ = TenantId(user);
            _ = UserId(user);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public string GetCurrentTenantId() => TenantId(httpContextAccessor.HttpContext?.User);

    public string GetCurrentUserId() => UserId(httpContextAccessor.HttpContext?.User);

    /// <summary>Reads the tenant from any principal, e.g. a SignalR connection's <c>Context.User</c>.</summary>
    public static string TenantId(ClaimsPrincipal? user)
    {
        // Custom claim injected into the JWT via the TokenIssuanceStart Azure Function extension.
        var tenantClaim = user?.FindFirst(TenantClaim)?.Value;

        if (string.IsNullOrWhiteSpace(tenantClaim))
        {
            throw new UnauthorizedAccessException(
                "Data Boundary Violation: Execution contexts require a tenant identification token claim.");
        }
        if (tenantClaim.Contains(KeySeparator))
        {
            throw new UnauthorizedAccessException($"Tenant ids must not contain '{KeySeparator}'.");
        }
        return tenantClaim;
    }

    public static string UserId(ClaimsPrincipal? user)
    {
        // Inbound claim mapping is off by default on the JWT handler, so the subject arrives as "sub".
        var userClaim = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user?.FindFirst("sub")?.Value
            ?? user?.FindFirst("oid")?.Value;

        if (string.IsNullOrWhiteSpace(userClaim))
        {
            throw new UnauthorizedAccessException(
                "Execution contexts require an identity context verification signature.");
        }
        if (userClaim.Contains(KeySeparator))
        {
            throw new UnauthorizedAccessException($"User ids must not contain '{KeySeparator}'.");
        }
        return userClaim;
    }
}
