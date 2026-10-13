using Helpdesk.Api.Chat;
using Helpdesk.Core;
using Helpdesk.Core.Interfaces;
using Helpdesk.Shared.Contracts;

namespace Helpdesk.Api.Endpoints;

public static class ChatEndpointRouteBuilderExtensions
{
    public const int MaxMessageLength = ChatRequestValidator.MaxMessageLength;
    public const int MaxHistoryMessages = ChatRequestValidator.MaxHistoryMessages;

    /// <summary>
    /// Synchronous fallback. The reply is returned in the HTTP response, so a rate-limit wait longer than the
    /// caller's HTTP timeout will fail; the SignalR hub (<see cref="ChatHub"/>) is the primary path.
    /// </summary>
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/chat/message", async (
            ChatMessageRequest request,
            ITenantProvider tenantProvider,
            ChatPipeline pipeline,
            TenantJobLimiter tenantLimiter,
            HttpContext http,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (ChatRequestValidator.Validate(request) is { } errors)
            {
                return Results.ValidationProblem(errors);
            }

            string tenantId = tenantProvider.GetCurrentTenantId();
            string userId = tenantProvider.GetCurrentUserId();

            // The same per-tenant cap as the hub's job queue, so this fallback is not a way around it. The lease is
            // held until the reply is written, and ct (the client's connection) cancels the work if they give up.
            using var lease = tenantLimiter.TryAcquire(tenantId);
            if (lease is null)
            {
                Telemetry.TenantJobRejections.Add(1);
                http.Response.Headers.RetryAfter = "5";
                return Results.Problem(
                    statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Your organisation has too many requests in progress. Please try again shortly.");
            }

            loggerFactory.CreateLogger("ChatEndpoint").LogInformation(
                "Processing incoming chat payload for Tenant: {TenantId}, User: {UserId}", tenantId, userId);

            // Classification must complete (and succeed) before any downstream work, including queueing.
            return Results.Ok(await pipeline.RunAsync(
                request, tenantId, userId, progress: null, ct, userAgent: http.Request.Headers.UserAgent.ToString()));
        })
        .WithName("PostChatMessage")
        .RequireAuthorization()
        .RequireRateLimiting("chat");

        return endpoints;
    }
}
