using AgentDesk.Api.Security;
using AgentDesk.Core.Domain;
using AgentDesk.Core.Rules;
using AgentDesk.Infrastructure.Ai;
using AgentDesk.Infrastructure.Blobs;
using Microsoft.Extensions.Options;

namespace AgentDesk.Api.Endpoints;

public static class AssistEndpoints
{
    public const string RateLimitPolicy = "assist";

    public static RouteGroupBuilder MapAssistEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/assist", (TicketAssistService svc) => Results.Ok(svc.Describe()))
            .WithTags("AI assist").RequireAuthorization(AgentDeskPolicies.Agent).WithName("DescribeAssist");

        // Each call sends the (redacted) ticket to the configured model; nothing is saved.
        var g = api.MapGroup("/tickets/{id}/assist").WithTags("AI assist")
            .RequireAuthorization(AgentDeskPolicies.Agent).RequireRateLimiting(RateLimitPolicy);

        g.MapPost("/suggest-solutions", async (string id, SuggestSolutionsRequest? body, HttpContext ctx, TicketAssistService svc, CancellationToken ct) =>
            Results.Ok(await svc.SuggestSolutionsAsync(Http.User(ctx), id, body ?? new(), ct))).WithName("AssistSuggestSolutions");

        g.MapPost("/draft-reply", async (string id, DraftReplyRequest? body, HttpContext ctx, TicketAssistService svc, CancellationToken ct) =>
            Results.Ok(await svc.DraftReplyAsync(Http.User(ctx), id, body ?? new(), ct))).WithName("AssistDraftReply");

        g.MapPost("/draft-kb", async (string id, DraftKbRequest? body, HttpContext ctx, TicketAssistService svc, CancellationToken ct) =>
            Results.Ok(await svc.DraftKbAsync(Http.User(ctx), id, body ?? new(), ct))).WithName("AssistDraftKb");

        g.MapPost("/reply-with-kb", async (string id, ReplyWithKbRequest? body, HttpContext ctx, TicketAssistService svc, CancellationToken ct) =>
            Results.Ok(await svc.ReplyWithKbAsync(Http.User(ctx), id, body ?? new(), ct))).WithName("AssistReplyWithKb");

        g.MapPost("/improve", async (string id, ImproveTextRequest body, HttpContext ctx, TicketAssistService svc, CancellationToken ct) =>
            Results.Ok(await svc.ImproveAsync(Http.User(ctx), id, body, ct))).WithName("AssistImprove");

        return api;
    }
}

public static class MetaEndpoints
{
    public static RouteGroupBuilder MapMetaEndpoints(this RouteGroupBuilder api, string authMode)
    {
        api.MapGet("/meta/vocabulary", () => Results.Ok(new
        {
            ticketStatuses = TicketStatuses.All,
            activeStatuses = TicketStatuses.Active,
            transitions = TicketStatuses.All.ToDictionary(s => s, TicketStateMachine.AllowedFrom),
            pendingReasons = PendingReasons.All,
            ticketTypes = TicketTypes.All,
            channels = Channels.All,
            priorities = Priorities.All,
            categories = TicketCategories.All,
            resolutionCodes = ResolutionCodes.All,
            visibilities = Visibilities.All,
            kbStatuses = KbStatuses.All,
            impactLevels = new[] { 1, 2, 3 },
            urgencyLevels = new[] { 1, 2, 3 }
        })).WithTags("Meta").RequireAuthorization().WithName("GetVocabulary");

        // Anonymous: the console calls this before signing in to learn how to authenticate.
        api.MapGet("/meta/config", (IOptions<AgentDeskAuthOptions> auth) => Results.Ok(new
        {
            auth = new
            {
                mode = authMode,
                hubTicketRequired = authMode == AuthModes.Jwt,
                authority = authMode == AuthModes.Jwt ? auth.Value.Authority : null,
                clientId = authMode == AuthModes.Jwt ? auth.Value.SpaClientId : null,
                apiScope = authMode == AuthModes.Jwt ? auth.Value.ApiScope : null
            }
        })).WithTags("Meta").AllowAnonymous().WithName("GetClientConfig");

        api.MapGet("/me", (HttpContext ctx) =>
        {
            var u = Http.User(ctx);
            return Results.Ok(new { u.TenantId, u.UserId, u.DisplayName, u.Email, roles = u.Roles.Order() });
        }).WithTags("Meta").RequireAuthorization().WithName("GetMe");

        return api;
    }
}

/// <summary>
/// Stand-in for Blob Storage when <c>Blobs:Provider</c> is InMemory. Anonymous like a SAS URL: access is granted by the
/// signed, expiring query string that only the API can issue.
/// </summary>
public static class DevBlobEndpoints
{
    public static void MapDevBlobEndpoints(this WebApplication app)
    {
        if (app.Services.GetService<InMemoryBlobStore>() is null) return;
        var route = InMemoryBlobStore.RoutePrefix + "{**path}";

        app.MapPut(route, async (string path, HttpContext ctx, InMemoryBlobStore store, IOptions<Core.Services.AttachmentOptions> limits, CancellationToken ct) =>
        {
            var q = ctx.Request.Query;
            if (!store.Verify(path, "w", q["se"], null, q["sig"])) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (ctx.Request.ContentLength > limits.Value.MaxBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            using var buffer = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(buffer, ct);
            if (buffer.Length > limits.Value.MaxBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            store.Put(path, buffer.ToArray(), ctx.Request.ContentType ?? "application/octet-stream");
            return Results.Created();
        }).AllowAnonymous().ExcludeFromDescription();

        app.MapGet(route, (string path, HttpContext ctx, InMemoryBlobStore store) =>
        {
            var q = ctx.Request.Query;
            string? fileName = q["fn"];
            if (!store.Verify(path, "r", q["se"], fileName, q["sig"])) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (store.Get(path) is not { } blob) return Results.NotFound();
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            // Always a download, never rendered inline, so an uploaded HTML/SVG file cannot run script on this origin.
            return Results.File(blob.Content, "application/octet-stream", fileName ?? "attachment");
        }).AllowAnonymous().ExcludeFromDescription();
    }
}
