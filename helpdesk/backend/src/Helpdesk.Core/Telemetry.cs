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


    // Model call metrics (tokens, cost, 429s) live in Helpdesk.Llm.LlmTelemetry under the "Helpdesk.Llm" meter.

    /// <summary>Tag: <c>reason</c>. Requests whose history had messages the server could not trust.</summary>
    public static readonly Counter<long> HistoryVerificationFailures =
        Meter.CreateCounter<long>("helpdesk.history.verification_failures", description: "Requests with conversation history that failed signature verification.");

    public static readonly Counter<long> HistoryMessagesDropped =
        Meter.CreateCounter<long>("helpdesk.history.messages_dropped", description: "History messages dropped because they were unsigned, altered or out of order.");

    /// <summary>Tag: <c>outcome</c> = hit | miss | error | disabled. Tag: <c>mode</c> = hybrid | lexical (no semantic leg ran).</summary>
    public static readonly Counter<long> KbSearches =
        Meter.CreateCounter<long>("helpdesk.kb.searches", description: "Knowledge-base retrievals, by outcome.");

    public static readonly Histogram<double> KbSearchDuration =
        Meter.CreateHistogram<double>("helpdesk.kb.search.duration", unit: "s", description: "Wall time of a knowledge-base retrieval.");

    public static readonly Counter<long> InferenceRoutes =
        Meter.CreateCounter<long>("helpdesk.inference.routes", description: "Answer-lane questions routed to a model tier, by lane, tier and reason.");

    public static readonly Counter<long> PrefilterBlocks =
        Meter.CreateCounter<long>("helpdesk.guardrail.prefilter.blocks", description: "Requests blocked by deterministic guardrail rules.");
}
