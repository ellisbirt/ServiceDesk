using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Helpdesk.Llm;

/// <summary>
/// Standard resilience (rate limiter, total timeout, retry, circuit breaker, attempt timeout) for outbound HttpClients.
/// The handler owns timeouts, so HttpClient.Timeout is disabled on the clients that use it.
/// </summary>
public static class HttpResilience
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

/// <summary>
/// POSTs to a model API, honouring the <c>Retry-After</c> header of HTTP 429 responses. The wait can far exceed
/// a normal HTTP timeout, which is why interactive callers get told about it through <see cref="ILlmProgress"/>.
/// The resilience handler is configured not to retry 429, so this loop owns it.
/// </summary>
internal static class RateLimitedSender
{
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http, Func<HttpRequestMessage> createRequest, bool stream, string providerName,
        LlmProviderOptions options, ILlmProgress? progress, CancellationToken ct)
    {
        var maxWait = TimeSpan.FromSeconds(options.RateLimitMaxWaitSeconds);
        var tags = LlmTelemetry.Tags(providerName, options.Model);

        for (var attempt = 1; ; attempt++)
        {
            using var request = createRequest();
            var response = await http.SendAsync(
                request, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct);

            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                try
                {
                    await EnsureSuccessAsync(response, providerName, ct);
                    return response;
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }

            var retryAfter = ReadRetryAfter(response);
            response.Dispose();
            LlmTelemetry.RateLimited.Add(1, tags);

            var wait = retryAfter ?? TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 30));
            if (attempt > options.RateLimitMaxRetries || wait > maxWait)
            {
                throw new UpstreamRateLimitedException(retryAfter,
                    $"{DisplayName(providerName)} rate limit: retry after {(retryAfter ?? wait).TotalSeconds:0}s, giving up after {attempt} attempt(s).");
            }

            // Jitter only on a real wait so many queued jobs do not all resume in the same instant.
            if (wait > TimeSpan.Zero) wait += TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
            LlmTelemetry.RateLimitWait.Record(wait.TotalSeconds, tags);
            if (progress is not null) await progress.OnRateLimitedAsync(wait, attempt, ct);
            await Task.Delay(wait, ct);
        }
    }

    public static string DisplayName(string provider) => provider switch
    {
        LlmProviders.Anthropic => "Anthropic",
        LlmProviders.OpenAI => "OpenAI",
        LlmProviders.AzureOpenAI => "Azure OpenAI",
        LlmProviders.Grok => "Grok",
        LlmProviders.Ollama => "Ollama",
        LlmProviders.LMStudio => "LM Studio",
        _ => provider
    };

    /// <summary>Like EnsureSuccessStatusCode, but includes a short, non-secret slice of the error body.</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string providerName, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string detail;
        try
        {
            detail = await response.Content.ReadAsStringAsync(ct);
            if (detail.Length > 300) detail = detail[..300] + "…";
        }
        catch
        {
            detail = "";
        }
        throw new HttpRequestException(
            $"{DisplayName(providerName)} returned {(int)response.StatusCode} ({response.ReasonPhrase}). {detail}".TrimEnd(),
            null, response.StatusCode);
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header?.Date is { } date)
        {
            var until = date - DateTimeOffset.UtcNow;
            return until < TimeSpan.Zero ? TimeSpan.Zero : until;
        }
        return null;
    }
}
