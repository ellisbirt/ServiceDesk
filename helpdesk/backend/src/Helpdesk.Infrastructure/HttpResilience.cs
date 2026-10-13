using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Helpdesk.Infrastructure;

/// <summary>
/// Standard resilience (rate limiter, total timeout, retry, circuit breaker, attempt timeout) for outbound HttpClients.
/// The handler owns timeouts, so HttpClient.Timeout is disabled on the clients that use it.
/// </summary>
internal static class HttpResilience
{
    /// <param name="callerHandlesRateLimits">
    /// When true, HTTP 429 is neither retried nor counted by the circuit breaker, so the caller can honour
    /// Retry-After itself (possibly for longer than any request timeout).
    /// </param>
    public static IHttpClientBuilder AddHelpdeskResilience(
        this IHttpClientBuilder builder, TimeSpan attemptTimeout, TimeSpan totalTimeout, int maxRetries = 2,
        bool callerHandlesRateLimits = false)
    {
        builder.AddStandardResilienceHandler(o =>
        {
            o.AttemptTimeout.Timeout = attemptTimeout;
            o.TotalRequestTimeout.Timeout = totalTimeout;
            o.Retry.MaxRetryAttempts = maxRetries;
            o.Retry.UseJitter = true;
            // Circuit breaker sampling window must be at least double the attempt timeout.
            o.CircuitBreaker.SamplingDuration = attemptTimeout * 2;

            if (callerHandlesRateLimits)
            {
                o.Retry.ShouldHandle = args => ValueTask.FromResult(
                    HttpClientResiliencePredicates.IsTransient(args.Outcome) && !IsRateLimited(args.Outcome));
                o.CircuitBreaker.ShouldHandle = args => ValueTask.FromResult(
                    HttpClientResiliencePredicates.IsTransient(args.Outcome) && !IsRateLimited(args.Outcome));
            }
        });
        return builder;
    }

    private static bool IsRateLimited(Polly.Outcome<HttpResponseMessage> outcome) =>
        outcome.Result?.StatusCode == HttpStatusCode.TooManyRequests;
}
