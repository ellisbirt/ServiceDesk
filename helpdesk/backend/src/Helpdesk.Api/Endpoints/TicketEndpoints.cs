using System.Globalization;
using Helpdesk.Api.Chat;
using Helpdesk.Api.Security;
using Helpdesk.Infrastructure.Security;

namespace Helpdesk.Api.Endpoints;

public static class TicketEndpointRouteBuilderExtensions
{
    public const string Path = "/api/v1/chat/ticket";
    public const string RateLimitPolicy = "ticket";

    /// <summary>
    /// Exchanges the caller's access token (sent in the Authorization header, like any other API call) for a
    /// short-lived, single-use ticket to put in the hub URL. See <see cref="HubTicketOptions"/>.
    /// </summary>
    public static IEndpointRouteBuilder MapTicketEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, (HttpContext context, IHubTicketStore store, TimeProvider time) =>
        {
            // The fallback policy has already required a usable tenant and user id.
            var tenantId = JwtTenantProvider.TenantId(context.User);
            var userId = JwtTenantProvider.UserId(context.User);

            var expiresUtc = long.TryParse(context.User.FindFirst("exp")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var exp)
                ? DateTimeOffset.FromUnixTimeSeconds(exp)
                : time.GetUtcNow().AddMinutes(5); // no exp claim: be conservative, the client simply reconnects

            // A ticket in a URL must never be cached or kept.
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";

            if (store.Issue(new HubTicketClaims(tenantId, userId, expiresUtc)) is not { } issued)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Too many connection tickets are outstanding. Please try again shortly.");
            }
            return Results.Ok(new { ticket = issued.Value, expiresInSeconds = (int)Math.Ceiling(issued.Lifetime.TotalSeconds) });
        })
        .WithName("PostHubTicket")
        .RequireAuthorization()
        .RequireRateLimiting(RateLimitPolicy);

        return endpoints;
    }
}
