using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Helpdesk.Llm;

/// <summary>
/// Traces and metrics for model calls. Register <see cref="Name"/> as both an activity source and a meter.
/// Metric tags are low-cardinality: <c>gen_ai.system</c> (provider id) and <c>gen_ai.request.model</c>.
/// Tenant and correlation ids found on parent activities are copied onto the request span only.
/// </summary>
public static class LlmTelemetry
{
    public const string Name = "Helpdesk.Llm";

    public static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> InputTokens =
        Meter.CreateCounter<long>("helpdesk.llm.input_tokens", description: "Uncached model input tokens.");

    public static readonly Counter<long> OutputTokens =
        Meter.CreateCounter<long>("helpdesk.llm.output_tokens", description: "Model output tokens.");

    public static readonly Counter<long> CacheWriteTokens =
        Meter.CreateCounter<long>("helpdesk.llm.cache_write_tokens", description: "Prompt-cache write input tokens.");

    public static readonly Counter<long> CacheReadTokens =
        Meter.CreateCounter<long>("helpdesk.llm.cache_read_tokens", description: "Prompt-cache read input tokens.");

    public static readonly Histogram<double> EstimatedCost =
        Meter.CreateHistogram<double>("helpdesk.llm.estimated_cost", unit: "USD", description: "Estimated model cost per operation.");

    public static readonly Counter<long> RateLimited =
        Meter.CreateCounter<long>("helpdesk.llm.rate_limited", description: "HTTP 429 responses from the model API.");

    public static readonly Histogram<double> RateLimitWait =
        Meter.CreateHistogram<double>("helpdesk.llm.rate_limit.wait", unit: "s", description: "Seconds waited out after a 429.");

    public static readonly Counter<long> EmbeddedInputs =
        Meter.CreateCounter<long>("helpdesk.llm.embedding.inputs", description: "Texts sent for embedding.");

    internal static TagList Tags(string provider, string model) => new()
    {
        { "gen_ai.system", provider },
        { "gen_ai.request.model", model }
    };

    /// <summary>Starts the request span, copying tenant and correlation ids from the nearest ancestor that has them.</summary>
    internal static Activity? StartRequest(string provider, string model, string operation = "chat")
    {
        var parent = Activity.Current;
        var tenantId = FindTag(parent, "helpdesk.tenant_id");
        var correlationId = FindTag(parent, "helpdesk.correlation_id");
        var activity = Source.StartActivity($"{provider}.request");
        if (activity is null) return null;
        activity.SetTag("gen_ai.system", provider);
        activity.SetTag("gen_ai.operation.name", operation);
        activity.SetTag("gen_ai.request.model", model);
        if (tenantId is not null) activity.SetTag("helpdesk.tenant_id", tenantId);
        if (correlationId is not null) activity.SetTag("helpdesk.correlation_id", correlationId);
        return activity;
    }

    internal static string? FindTag(Activity? activity, string key)
    {
        for (var current = activity; current is not null; current = current.Parent)
        {
            if (current.GetTagItem(key) is { } value) return value.ToString();
        }
        return null;
    }
}

internal readonly record struct TokenUsage(int Input, int Output, int CacheWrite, int CacheRead)
{
    public static TokenUsage Empty => new(0, 0, 0, 0);

    public TokenUsage Add(TokenUsage value) =>
        new(Input + value.Input, Output + value.Output, CacheWrite + value.CacheWrite, CacheRead + value.CacheRead);

    public decimal? EstimateCost(LlmProviderOptions options)
    {
        if (options.InputCostPerMillionUsd is not { } input
            || options.CacheWriteCostPerMillionUsd is not { } cacheWrite
            || options.CacheReadCostPerMillionUsd is not { } cacheRead
            || options.OutputCostPerMillionUsd is not { } output)
        {
            return null;
        }

        return (Input * input + CacheWrite * cacheWrite + CacheRead * cacheRead + Output * output) / 1_000_000m;
    }

    /// <summary>Records metrics, span tags and a structured log line for one finished operation.</summary>
    public void Record(string provider, LlmProviderOptions options, Activity? activity, Microsoft.Extensions.Logging.ILogger logger)
    {
        var tags = LlmTelemetry.Tags(provider, options.Model);
        LlmTelemetry.InputTokens.Add(Input, tags);
        LlmTelemetry.OutputTokens.Add(Output, tags);
        LlmTelemetry.CacheWriteTokens.Add(CacheWrite, tags);
        LlmTelemetry.CacheReadTokens.Add(CacheRead, tags);

        var cost = EstimateCost(options);
        if (cost is { } estimatedCost)
        {
            LlmTelemetry.EstimatedCost.Record((double)estimatedCost, tags);
            activity?.SetTag("gen_ai.usage.cost_usd", estimatedCost);
        }
        activity?.SetTag("gen_ai.usage.input_tokens", Input + CacheWrite + CacheRead);
        activity?.SetTag("gen_ai.usage.output_tokens", Output);

        var tenantId = activity?.GetTagItem("helpdesk.tenant_id")?.ToString()
            ?? LlmTelemetry.FindTag(System.Diagnostics.Activity.Current, "helpdesk.tenant_id");
        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger,
            "LLM usage for Tenant: {TenantId}, Provider: {Provider}, Model: {Model}, InputTokens: {InputTokens}, CacheWriteTokens: {CacheWriteTokens}, CacheReadTokens: {CacheReadTokens}, OutputTokens: {OutputTokens}, EstimatedCostUsd: {EstimatedCostUsd}",
            tenantId ?? "unknown", provider, options.Model, Input, CacheWrite, CacheRead, Output, cost);
    }
}
