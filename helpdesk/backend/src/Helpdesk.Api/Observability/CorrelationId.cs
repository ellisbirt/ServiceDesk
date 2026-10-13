using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Helpdesk.Api.Observability;

/// <summary>
/// Gives every HTTP request a correlation id: the caller's <c>X-Correlation-ID</c> if it is well formed,
/// otherwise a new one. It is echoed on the response, attached to every log line (as a logging scope, so any
/// provider sees it), tagged on the current trace span and exposed to error responses.
/// Chat jobs started from the hub use their request id as the correlation id (see <c>ChatJobProcessor</c>).
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-ID";
    private const string ItemKey = "CorrelationId";

    // Caller-supplied values end up in logs and headers, so accept only a short, plain token.
    [GeneratedRegex("^[A-Za-z0-9._-]{8,64}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Valid();

    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers[HeaderName].FirstOrDefault();
        var id = supplied is not null && Valid().IsMatch(supplied) ? supplied : Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = id;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = id;
            return Task.CompletedTask;
        });
        Activity.Current?.SetTag("helpdesk.correlation_id", id);

        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = id,
            ["TraceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier
        }))
        {
            await next(context);
        }
    }

    public static string? Get(HttpContext context) => context.Items[ItemKey] as string;
}
