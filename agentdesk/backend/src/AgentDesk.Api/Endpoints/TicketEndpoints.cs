using AgentDesk.Api.Security;
using AgentDesk.Core.Abstractions;
using AgentDesk.Core.Domain;
using AgentDesk.Core.Services;

namespace AgentDesk.Api.Endpoints;

public static class TicketEndpoints
{
    public static RouteGroupBuilder MapTicketEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/tickets").WithTags("Tickets").RequireAuthorization(AgentDeskPolicies.Agent);

        g.MapGet("/", async (HttpContext ctx, TicketService svc,
            string? status, string? priority, string? assignee, bool? unassigned, string? group, string? category,
            string? requester, string? q, bool? breached, string? sort, int? pageSize, string? continuationToken,
            CancellationToken ct) =>
        {
            var user = Http.User(ctx);
            var query = new TicketQuery
            {
                Statuses = status?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                Priority = priority,
                AssigneeId = assignee == "me" ? user.UserId : assignee,
                Unassigned = unassigned ?? false,
                AssignmentGroup = group,
                Category = category,
                RequesterId = requester,
                Text = q,
                Breached = breached,
                Sort = string.IsNullOrWhiteSpace(sort) ? "updatedAt" : sort,
                PageSize = Math.Clamp(pageSize ?? 25, 1, 100),
                ContinuationToken = continuationToken
            };
            var page = await svc.QueryAsync(user, query, ct);
            return Results.Ok(new PageDto<Ticket>(page.Items, page.ContinuationToken));
        }).WithName("ListTickets");

        g.MapGet("/{id}", async (string id, HttpContext ctx, TicketService svc, CancellationToken ct) =>
            Http.WithETag(ctx, await svc.GetAsync(Http.User(ctx), id, ct))).WithName("GetTicket");

        g.MapPost("/", async (CreateTicketCommand body, HttpContext ctx, TicketService svc, CancellationToken ct) =>
        {
            // The console creates tickets on behalf of requesters; intake-only fields are not accepted from clients.
            var ticket = await svc.CreateAsync(Http.User(ctx), body with { Source = null }, ct);
            if (!string.IsNullOrEmpty(ticket.ETag)) ctx.Response.Headers.ETag = ticket.ETag;
            return Results.Created($"/api/v1/tickets/{ticket.Id}", ticket);
        }).WithName("CreateTicket");

        g.MapPatch("/{id}", async (string id, UpdateTicketCommand body, HttpContext ctx, TicketService svc, CancellationToken ct) =>
            Http.WithETag(ctx, await svc.UpdateAsync(Http.User(ctx), id, body, Http.IfMatch(ctx), ct))).WithName("UpdateTicket");

        g.MapPost("/{id}/assign", async (string id, AssignTicketCommand body, HttpContext ctx, TicketService svc, CancellationToken ct) =>
            Http.WithETag(ctx, await svc.AssignAsync(Http.User(ctx), id, body, Http.IfMatch(ctx), ct))).WithName("AssignTicket");

        g.MapPost("/{id}/transition", async (string id, TransitionTicketCommand body, HttpContext ctx, TicketService svc, CancellationToken ct) =>
            Http.WithETag(ctx, await svc.TransitionAsync(Http.User(ctx), id, body, Http.IfMatch(ctx), ct))).WithName("TransitionTicket");

        g.MapGet("/{id}/timeline", async (string id, HttpContext ctx, TicketService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetTimelineAsync(Http.User(ctx), id, ct))).WithName("GetTicketTimeline");

        g.MapPost("/{id}/comments", async (string id, AddCommentCommand body, HttpContext ctx, TicketService svc, CancellationToken ct) =>
        {
            var result = await svc.AddCommentAsync(Http.User(ctx), id, body, ct);
            if (!string.IsNullOrEmpty(result.Ticket.ETag)) ctx.Response.Headers.ETag = result.Ticket.ETag;
            return Results.Created($"/api/v1/tickets/{id}/timeline", new CommentResponse(
                result.Ticket, result.Comment, result.PublishedArticles.Select(KbArticleDto.From).ToList()));
        }).WithName("AddTicketComment");

        // Attachments: request an upload URL, PUT the file straight to blob storage, then complete.
        g.MapGet("/{id}/attachments", async (string id, HttpContext ctx, AttachmentService svc, CancellationToken ct) =>
            Results.Ok((await svc.ListAsync(Http.User(ctx), id, ct)).Select(AttachmentDto.From))).WithName("ListAttachments");

        g.MapPost("/{id}/attachments", async (string id, CreateAttachmentCommand body, HttpContext ctx, AttachmentService svc, CancellationToken ct) =>
        {
            var upload = await svc.CreateUploadAsync(Http.User(ctx), id, body, ct);
            return Results.Ok(new UploadResponse(AttachmentDto.From(upload.Attachment), upload.UploadUri.ToString(),
                upload.ExpiresAt, new Dictionary<string, string> { ["x-ms-blob-type"] = "BlockBlob", ["Content-Type"] = upload.Attachment.ContentType }));
        }).WithName("CreateAttachmentUpload");

        g.MapPost("/{id}/attachments/{attachmentId}/complete", async (string id, string attachmentId, HttpContext ctx, AttachmentService svc, CancellationToken ct) =>
            Results.Ok(AttachmentDto.From(await svc.CompleteAsync(Http.User(ctx), id, attachmentId, ct)))).WithName("CompleteAttachmentUpload");

        g.MapGet("/{id}/attachments/{attachmentId}/download", async (string id, string attachmentId, HttpContext ctx, AttachmentService svc, CancellationToken ct) =>
        {
            var download = await svc.GetDownloadAsync(Http.User(ctx), id, attachmentId, ct);
            return Results.Ok(new DownloadResponse(download.DownloadUri.ToString(), download.ExpiresAt));
        }).WithName("GetAttachmentDownload");

        return api;
    }

    public sealed record CommentResponse(Ticket Ticket, TimelineEntry Comment, IReadOnlyList<KbArticleDto> PublishedArticles);

    public sealed record UploadResponse(AttachmentDto Attachment, string UploadUrl, DateTimeOffset ExpiresAt, IReadOnlyDictionary<string, string> Headers);

    public sealed record DownloadResponse(string DownloadUrl, DateTimeOffset ExpiresAt);
}
