using System.Text.Json;
using AgentDesk.Api.Observability;
using AgentDesk.Core.Abstractions;
using Helpdesk.Llm;
using Microsoft.AspNetCore.Mvc;

namespace AgentDesk.Api;

/// <summary>
/// Turns domain exceptions into RFC 9457 problem responses (<c>application/problem+json</c>). Anything unexpected becomes a
/// 500 with no internal detail outside Development. Every problem carries <c>code</c> and <c>correlationId</c>.
/// </summary>
public sealed class ExceptionStatusCodeHandler(RequestDelegate next, ILogger<ExceptionStatusCodeHandler> logger, IHostEnvironment env)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client went away; nothing to report.
        }
        catch (Exception ex)
        {
            if (context.Response.HasStarted) throw;
            await WriteProblemAsync(context, ex);
        }
    }

    private async Task WriteProblemAsync(HttpContext context, Exception ex)
    {
        int status;
        string code;
        string title;
        string? detail = null;
        IReadOnlyDictionary<string, string[]>? errors = null;

        switch (ex)
        {
            case ValidationException v:
                (status, code, title, detail, errors) = (v.StatusCode, v.Code, v.Title, v.Message, v.Errors);
                break;
            case AgentDeskException a:
                (status, code, title, detail) = (a.StatusCode, a.Code, a.Title, a.Message);
                break;
            case UpstreamRateLimitedException r:
                (status, code, title, detail) = (429, "upstream_rate_limited", "AI provider is busy", "The AI provider is rate limiting requests. Try again shortly.");
                if (r.RetryAfter is { } wait)
                {
                    context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString();
                }
                break;
            case BadHttpRequestException b:
                (status, code, title, detail) = (b.StatusCode, "bad_request", "Bad request", b.Message);
                break;
            case JsonException:
                (status, code, title, detail) = (400, "bad_request", "Bad request", "The request body is not valid JSON for this endpoint.");
                break;
            default:
                (status, code, title) = (500, "internal_error", "Internal server error");
                break;
        }

        if (status >= 500) logger.LogError(ex, "Request failed with {Status} {Code}", status, code);
        else logger.LogWarning("Request failed with {Status} {Code}: {Message}", status, code, ex.Message);

        var problem = new ProblemDetails
        {
            Type = $"urn:agentdesk:problem:{code}",
            Status = status,
            Title = title,
            Detail = env.IsDevelopment() && status >= 500 ? ex.ToString() : detail,
            Instance = context.Request.Path
        };
        problem.Extensions["code"] = code;
        problem.Extensions["correlationId"] = CorrelationIdMiddleware.Get(context);
        if (errors is not null) problem.Extensions["errors"] = errors;

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(problem, Json), context.RequestAborted);
    }
}
