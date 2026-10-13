using AgentDesk.Api.Security;
using AgentDesk.Core.Abstractions;
using AgentDesk.Core.Services;

namespace AgentDesk.Api.Endpoints;

public static class KbEndpoints
{
    public static RouteGroupBuilder MapKbEndpoints(this RouteGroupBuilder api)
    {
        var read = api.MapGroup("/kb/articles").WithTags("Knowledge base").RequireAuthorization(AgentDeskPolicies.Agent);
        var write = api.MapGroup("/kb/articles").WithTags("Knowledge base").RequireAuthorization(AgentDeskPolicies.KbAuthor);

        read.MapGet("/", async (HttpContext ctx, KbService svc, string? status, string? category, string? q, int? pageSize,
            string? continuationToken, CancellationToken ct) =>
        {
            var page = await svc.QueryAsync(Http.User(ctx), new KbQuery
            {
                Status = status,
                Category = category,
                Text = q,
                PageSize = Math.Clamp(pageSize ?? 25, 1, 100),
                ContinuationToken = continuationToken
            }, ct);
            return Results.Ok(new PageDto<KbArticleDto>(page.Items.Select(KbArticleDto.From).ToList(), page.ContinuationToken));
        }).WithName("ListKbArticles");

        read.MapGet("/{id}", async (string id, HttpContext ctx, KbService svc, CancellationToken ct) =>
        {
            var article = await svc.GetAsync(Http.User(ctx), id, ct);
            return Http.WithETag(ctx, article, KbArticleDto.From(article));
        }).WithName("GetKbArticle");

        read.MapGet("/{id}/versions", async (string id, HttpContext ctx, KbService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetVersionsAsync(Http.User(ctx), id, ct))).WithName("GetKbArticleVersions");

        write.MapPost("/", async (KbArticleInput body, HttpContext ctx, KbService svc, CancellationToken ct) =>
        {
            var article = await svc.CreateAsync(Http.User(ctx), body, ct);
            if (!string.IsNullOrEmpty(article.ETag)) ctx.Response.Headers.ETag = article.ETag;
            return Results.Created($"/api/v1/kb/articles/{article.Id}", KbArticleDto.From(article));
        }).WithName("CreateKbArticle");

        write.MapPut("/{id}", async (string id, KbArticleInput body, HttpContext ctx, KbService svc, CancellationToken ct) =>
        {
            var article = await svc.UpdateAsync(Http.User(ctx), id, body, Http.IfMatch(ctx), ct);
            return Http.WithETag(ctx, article, KbArticleDto.From(article));
        }).WithName("UpdateKbArticle");

        write.MapPost("/{id}/publish", async (string id, HttpContext ctx, KbService svc, CancellationToken ct) =>
        {
            var article = await svc.PublishAsync(Http.User(ctx), id, Http.IfMatch(ctx), ct);
            return Http.WithETag(ctx, article, KbArticleDto.From(article));
        }).WithName("PublishKbArticle");

        write.MapPost("/{id}/archive", async (string id, HttpContext ctx, KbService svc, CancellationToken ct) =>
        {
            var article = await svc.ArchiveAsync(Http.User(ctx), id, Http.IfMatch(ctx), ct);
            return Http.WithETag(ctx, article, KbArticleDto.From(article));
        }).WithName("ArchiveKbArticle");

        write.MapPost("/{id}/restore", async (string id, HttpContext ctx, KbService svc, CancellationToken ct) =>
        {
            var article = await svc.RestoreAsync(Http.User(ctx), id, Http.IfMatch(ctx), ct);
            return Http.WithETag(ctx, article, KbArticleDto.From(article));
        }).WithName("RestoreKbArticle");

        write.MapPost("/{id}/reembed", async (string id, HttpContext ctx, KbService svc, CancellationToken ct) =>
        {
            var article = await svc.ReembedAsync(await svc.GetAsync(Http.User(ctx), id, ct), ct);
            return Http.WithETag(ctx, article, KbArticleDto.From(article));
        }).WithName("ReembedKbArticle");

        return api;
    }
}
