using AgentDesk.Api.Security;
using AgentDesk.Core.Abstractions;
using AgentDesk.Core.Domain;
using Microsoft.Net.Http.Headers;

namespace AgentDesk.Api.Endpoints;

internal static class Http
{
    public static CurrentUser User(HttpContext ctx) => AgentDeskClaims.ToCurrentUser(ctx.User);

    /// <summary>Reads <c>If-Match</c>. Optional today; when present a stale ETag yields 412.</summary>
    public static string? IfMatch(HttpContext ctx)
    {
        var value = ctx.Request.Headers.IfMatch.FirstOrDefault();
        return string.IsNullOrWhiteSpace(value) || value == "*" ? null : value.Trim();
    }

    /// <summary>Returns <paramref name="body"/> with the document's ETag in the response header.</summary>
    public static IResult WithETag<T>(HttpContext ctx, TenantDocument document, T body)
    {
        if (!string.IsNullOrEmpty(document.ETag)) ctx.Response.Headers[HeaderNames.ETag] = document.ETag;
        return Results.Ok(body);
    }

    public static IResult WithETag(HttpContext ctx, TenantDocument document) => WithETag(ctx, document, document);
}
