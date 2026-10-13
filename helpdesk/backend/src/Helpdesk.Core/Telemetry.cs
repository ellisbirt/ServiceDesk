using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Helpdesk.Core;

/// <summary>
/// Application-defined traces and metrics. Metric tags are deliberately low-cardinality (no tenant or user
/// ids); tenant and correlation ids go on spans and in logs instead.
/// </summary>
public static class Telemetry
{
    public const string Name = "Helpdesk";

    public static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);

    /// <summary>Tag: <c>lane</c>.</summary>
    public static readonly Counter<long> Classifications =
        Meter.CreateCounter<long>("helpdesk.guardrail.classifications", description: "Messages classified, by action lane.");

    /// <summary>Tag: <c>outcome</c> = completed | failed | timeout | cancelled (service stopping) | abandoned (client left or cancelled).</summary>
    public static readonly Counter<long> ChatJobs =
        Meter.CreateCounter<long>("helpdesk.chat.jobs", description: "Chat jobs finished, by outcome.");

    /// <summary>Requests turned away because the tenant already had its limit in flight.</summary>
    public static readonly Counter<long> TenantJobRejections =
        Meter.CreateCounter<long>("helpdesk.chat.tenant_rejections", description: "Requests rejected because the tenant is at its in-flight limit.");

    public static readonly Histogram<double> ChatJobDuration =
        Meter.CreateHistogram<double>("helpdesk.chat.job.duration", unit: "s", description: "Wall time of a chat job, including rate-limit waits.");

    public static readonly Counter<long> Redactions =
        Meter.CreateCounter<long>("helpdesk.redactions", description: "Sensitive values removed before leaving the service.");

    public static readonly Counter<long> UpstreamRateLimited =
        Meter.CreateCounter<long>("helpdesk.anthropic.rate_limited", description: "HTTP 429 responses from the model API.");

    public static readonly Histogram<double> UpstreamRateLimitWait =
        Meter.CreateHistogram<double>("helpdesk.anthropic.rate_limit.wait", unit: "s", description: "Seconds waited out after a 429.");

    public static readonly Counter<long> AnthropicInputTokens =
        Meter.CreateCounter<long>("helpdesk.anthropic.input_tokens", description: "Uncached Anthropic input tokens.");

    public static readonly Counter<long> AnthropicOutputTokens =
        Meter.CreateCounter<long>("helpdesk.anthropic.output_tokens", description: "Anthropic output tokens.");

    public static readonly Counter<long> AnthropicCacheWriteTokens =
        Meter.CreateCounter<long>("helpdesk.anthropic.cache_write_tokens", description: "Anthropic cache-write input tokens.");

    public static readonly Counter<long> AnthropicCacheReadTokens =
        Meter.CreateCounter<long>("helpdesk.anthropic.cache_read_tokens", description: "Anthropic cache-read input tokens.");

    public static readonly Histogram<double> AnthropicEstimatedCost =
        Meter.CreateHistogram<double>("helpdesk.anthropic.estimated_cost", unit: "USD", description: "Estimated Anthropic cost per API operation.");

    /// <summary>Tag: <c>reason</c>. Requests whose history had messages the server could not trust.</summary>
    public static readonly Counter<long> HistoryVerificationFailures =
        Meter.CreateCounter<long>("helpdesk.history.verification_failures", description: "Requests with conversation history that failed signature verification.");

    public static readonly Counter<long> HistoryMessagesDropped =
        Meter.CreateCounter<long>("helpdesk.history.messages_dropped", description: "History messages dropped because they were unsigned, altered or out of order.");

    public static readonly Counter<long> PrefilterBlocks =
        Meter.CreateCounter<long>("helpdesk.guardrail.prefilter.blocks", description: "Requests blocked by deterministic guardrail rules.");
}
