using System.Security.Claims;
using AgentDesk.Core.Abstractions;

namespace AgentDesk.Api.Security;

/// <summary>
/// Maps a <see cref="ClaimsPrincipal"/> (from Entra ID, dev headers or the anonymous handler) to a <see cref="CurrentUser"/>.
/// </summary>
public static class AgentDeskClaims
{
    /// <summary>Claim types checked in order for the tenant id. "tenant_id" is what the dev and anonymous handlers issue.</summary>
    public static readonly string[] TenantClaims = ["tenant_id", "tid", "http://schemas.microsoft.com/identity/claims/tenantid"];

    /// <summary>Claim types checked in order for the stable user id.</summary>
    public static readonly string[] UserClaims = ["oid", "http://schemas.microsoft.com/identity/claims/objectidentifier", "sub", ClaimTypes.NameIdentifier];

    public const string RoleClaim = "roles";

    public static string? TenantId(ClaimsPrincipal user) => First(user, TenantClaims);

    public static string? UserId(ClaimsPrincipal user) => First(user, UserClaims);

    /// <summary>Tenant and user ids are used in composite keys, so both must be present and must not contain ':'.</summary>
    public static bool HasValidIdentity(ClaimsPrincipal user) =>
        TenantId(user) is { Length: > 0 and <= 128 } t && !t.Contains(':')
        && UserId(user) is { Length: > 0 and <= 128 } u && !u.Contains(':');

    public static CurrentUser ToCurrentUser(ClaimsPrincipal user)
    {
        var tenant = TenantId(user) ?? throw new InvalidOperationException("The caller has no tenant claim.");
        var id = UserId(user) ?? throw new InvalidOperationException("The caller has no user id claim.");
        var name = user.FindFirst("name")?.Value ?? user.FindFirst(ClaimTypes.Name)?.Value ?? id;
        var email = user.FindFirst("preferred_username")?.Value ?? user.FindFirst("email")?.Value ?? user.FindFirst(ClaimTypes.Email)?.Value;
        var roles = user.Claims
            .Where(c => c.Type is RoleClaim or ClaimTypes.Role)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.Ordinal);
        return new CurrentUser(tenant, id, name, email, roles);
    }

    private static string? First(ClaimsPrincipal user, string[] types)
    {
        foreach (var type in types)
        {
            if (user.FindFirst(type)?.Value is { Length: > 0 } value) return value;
        }
        return null;
    }
}

/// <summary>Reads the caller from the current HTTP request. Only valid inside a request.</summary>
public sealed class HttpCurrentUserAccessor(IHttpContextAccessor http) : ICurrentUserAccessor
{
    public CurrentUser Current =>
        http.HttpContext?.User is { Identity.IsAuthenticated: true } user
            ? AgentDeskClaims.ToCurrentUser(user)
            : throw new InvalidOperationException("No authenticated user is available outside an HTTP request.");
}

/// <summary>Authorization policy names. Each requires the matching app role (Admin satisfies all of them).</summary>
public static class AgentDeskPolicies
{
    public const string Agent = "agent";
    public const string KbAuthor = "kb-author";
    public const string Admin = "admin";
}
