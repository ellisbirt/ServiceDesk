using Helpdesk.Api.Chat;
using Helpdesk.Api.Observability;
using Helpdesk.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Helpdesk.Api;

public sealed class ExceptionStatusCodeHandler(
    RequestDelegate next,
    ILogger<ExceptionStatusCodeHandler> logger,
    IWebHostEnvironment environment)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            if (context.Response.HasStarted)
            {
                logger.LogError(exception, "Request failed after the response started.");
                throw;
            }

            var (status, title, code) = ApiErrors.Map(
                exception, clientAborted: context.RequestAborted.IsCancellationRequested);

            if (status >= 500)
            {
                logger.LogError(exception, "Request failed with {Status}", status);
            }
            else
            {
                logger.LogWarning(exception, "Request rejected with {Status}", status);
            }

            context.Response.Clear();
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/problem+json";
            if (exception is UpstreamRateLimitedException { RetryAfter: { } retryAfter })
            {
                context.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString("0");
            }

            var problem = new ProblemDetails
            {
                Type = $"urn:helpdesk:problem:{code}",
                Status = status,
                Title = title,
                Instance = context.Request.Path
            };
            problem.Extensions["code"] = code;
            problem.Extensions["correlationId"] = CorrelationIdMiddleware.Get(context);
            if (IsDevelopmentEnvironment())
            {
                problem.Detail = exception.ToString();
            }

            await JsonSerializer.SerializeAsync(
                context.Response.Body,
                problem,
                new JsonSerializerOptions(JsonSerializerDefaults.Web),
                cancellationToken: context.RequestAborted);
        }
    }

    private bool IsDevelopmentEnvironment() =>
        environment.IsDevelopment() || environment.IsEnvironment("LocalDevelopment");
}
