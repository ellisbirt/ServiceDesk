namespace Helpdesk.Infrastructure.Ai;

public class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    /// <summary>When empty, the offline keyword router and stub inference are used (local development only).</summary>
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.anthropic.com/";
    public string AnthropicVersion { get; set; } = "2023-06-01";
    /// <summary>The Standard tier's model.</summary>
    public string Model { get; set; } = "claude-sonnet-5-5";
    /// <summary>The Fast tier's model. Empty uses the vendor default (Claude Haiku).</summary>
    public string FastModel { get; set; } = "";
    /// <summary>The Deep tier's model. Empty uses the vendor default (Claude Opus).</summary>
    public string DeepModel { get; set; } = "";
    /// <summary>Ceiling for the classifier reply. The small model sometimes writes a long reasoning note, which at 256 was cut off (about 1 in 6); replies normally use 80 to 330 tokens.</summary>
    public int RouterMaxTokens { get; set; } = 768;
    public int InferenceMaxTokens { get; set; } = 1024;
    public double CoachTemperature { get; set; } = 0.3;
    public double InferenceTemperature { get; set; } = 0.3;

    /// <summary>Server-side web search tool version used when Search:Provider is "Claude".</summary>
    public string WebSearchToolType { get; set; } = "web_search_20260318";
    public int WebSearchMaxUses { get; set; } = 3;

    /// <summary>Longest single wait honoured for an HTTP 429 Retry-After header; longer waits fail the request.</summary>
    public int RateLimitMaxWaitSeconds { get; set; } = 120;
    /// <summary>How many times a rate-limited call is retried before giving up.</summary>
    public int RateLimitMaxRetries { get; set; } = 4;
    /// <summary>A stream that delivers nothing (not even a ping) for this long is abandoned.</summary>
    public int StreamIdleTimeoutSeconds { get; set; } = 30;
    public decimal? InputCostPerMillionUsd { get; set; }
    public decimal? CacheWriteCostPerMillionUsd { get; set; }
    public decimal? CacheReadCostPerMillionUsd { get; set; }
    public decimal? OutputCostPerMillionUsd { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
