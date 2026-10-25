using Helpdesk.Core.Interfaces;
using System.Globalization;
using System.Net.Http.Headers;

namespace Helpdesk.Api.Chat;

/// <summary>Maps exceptions to a status, a user-safe message and a stable code. Used by HTTP and the hub alike.</summary>
public static class ApiErrors
{
    public sealed record Mapped(int Status, string Title, string Code);

    public static Mapped Map(Exception exception, bool clientAborted = false) => exception switch
    {
        UnauthorizedAccessException =>
            new(StatusCodes.Status403Forbidden, "Tenant or identity context missing.", "forbidden"),
        AttachmentException attachment => new(attachment.Status, attachment.Message, attachment.Code),
        UnsupportedLaneException =>
            new(StatusCodes.Status500InternalServerError, "An unsupported processing lane was returned.", "unsupported_lane"),
        GuardrailClassificationException =>
            new(StatusCodes.Status503ServiceUnavailable, "Message could not be safely classified. Please try again.", "classification_failed"),
        TicketInterviewException =>
            new(StatusCodes.Status502BadGateway, "Your request could not be prepared for the service desk. Please try again.", "interview_failed"),
        UpstreamRateLimitedException =>
            new(StatusCodes.Status429TooManyRequests, "The AI service is busy right now. Please try again in a few minutes.", "rate_limited"),
        // Must precede the IOException case: BadHttpRequestException derives from IOException.
        BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
            new(StatusCodes.Status413PayloadTooLarge, "Request body too large.", "request_too_large"),
        BadHttpRequestException bad =>
            new(bad.StatusCode, "The request could not be read.", "bad_request"),
        _ when IsQueueFailure(exception) =>
            new(StatusCodes.Status503ServiceUnavailable, "The ticket queue is unavailable. Please try again shortly.", "queue_unavailable"),
        HttpRequestException or TaskCanceledException or TimeoutException or IOException
            or Polly.Timeout.TimeoutRejectedException or Polly.CircuitBreaker.BrokenCircuitException when !clientAborted =>
            new(StatusCodes.Status502BadGateway, "An upstream AI or search service failed. Please try again.", "upstream_failed"),
        _ => new(StatusCodes.Status500InternalServerError, "An unexpected error occurred.", "internal_error")
    };

    public static double? RetryAfterSeconds(Exception exception)
    {
        if (exception is UpstreamRateLimitedException { RetryAfter: { } delay })
            return Math.Max(0, delay.TotalSeconds);

        var storage = exception as Azure.RequestFailedException
            ?? (exception as AggregateException)?.InnerException as Azure.RequestFailedException;
        var response = storage?.GetRawResponse();
        if (response is null) return null;

        if (response.Headers.TryGetValue("x-ms-retry-after-ms", out var milliseconds)
            && double.TryParse(milliseconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
            && double.IsFinite(ms) && ms >= 0)
            return ms / 1000;

        if (response.Headers.TryGetValue("Retry-After", out var value)
            && RetryConditionHeaderValue.TryParse(value, out var retry))
            return retry.Delta?.TotalSeconds
                ?? (retry.Date is { } date ? Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds) : null);

        return null;
    }

    // Storage SDK retries surface as AggregateException wrapping RequestFailedException.
    private static bool IsQueueFailure(Exception ex) =>
        ex is Azure.RequestFailedException or Azure.Messaging.ServiceBus.ServiceBusException
        || ex is AggregateException { InnerException: Azure.RequestFailedException };
}
