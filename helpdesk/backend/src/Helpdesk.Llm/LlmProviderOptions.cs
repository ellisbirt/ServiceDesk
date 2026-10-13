namespace Helpdesk.Llm;

/// <summary>Provider ids accepted in <see cref="LlmProviderOptions.Provider"/>.</summary>
public static class LlmProviders
{
    public const string Anthropic = "anthropic";
    public const string OpenAI = "openai";
    public const string AzureOpenAI = "azure_openai";
    public const string Grok = "grok";
    public const string Ollama = "ollama";
    public const string LMStudio = "lm_studio";
    /// <summary>No network: deterministic canned chat and hashed bag-of-words embeddings. For dev and tests.</summary>
    public const string Offline = "offline";

    public static readonly IReadOnlyList<string> All = [Anthropic, OpenAI, AzureOpenAI, Grok, Ollama, LMStudio, Offline];

    /// <summary>Accepts the ids above plus the aliases <c>microsoft</c>, <c>azure</c>, <c>lmstudio</c>, <c>fake</c> and <c>claude</c>.</summary>
    public static string Normalize(string? provider) => (provider ?? "").Trim().ToLowerInvariant() switch
    {
        "claude" => Anthropic,
        "microsoft" or "azure" or "azureopenai" => AzureOpenAI,
        "lmstudio" or "lm-studio" => LMStudio,
        "xai" => Grok,
        "fake" or "none" => Offline,
        var other => other
    };
}

/// <summary>
/// Settings for one model role, bound from <c>Llm:Roles:&lt;role&gt;</c>. Every provider shares this shape so a role
/// can be repointed at another provider by configuration alone.
/// </summary>
public sealed class LlmProviderOptions
{
    public string Provider { get; set; } = LlmProviders.Offline;
    /// <summary>Model id, or the deployment name for Azure OpenAI.</summary>
    public string Model { get; set; } = "";
    /// <summary>Base URL. Defaults per provider (for example <c>http://localhost:11434/v1</c> for Ollama).</summary>
    public string? Endpoint { get; set; }
    /// <summary>
    /// Secret. Prefer environment variables or a secret store; falls back to <c>ANTHROPIC_API_KEY</c>,
    /// <c>OPENAI_API_KEY</c>, <c>XAI_API_KEY</c> or <c>AZURE_OPENAI_API_KEY</c> by provider.
    /// </summary>
    public string? ApiKey { get; set; }
    /// <summary>Azure OpenAI <c>api-version</c>.</summary>
    public string? ApiVersion { get; set; }
    public string AnthropicVersion { get; set; } = "2023-06-01";

    public int MaxTokens { get; set; } = 1024;
    /// <summary>Overrides the model-name heuristic for whether sampling temperature may be sent.</summary>
    public bool? SupportsTemperature { get; set; }
    /// <summary>Embedding vector length. Required for embedding models we do not recognise.</summary>
    public int? Dimensions { get; set; }

    public int RateLimitMaxWaitSeconds { get; set; } = 120;
    public int RateLimitMaxRetries { get; set; } = 4;
    public int StreamIdleTimeoutSeconds { get; set; } = 30;

    public decimal? InputCostPerMillionUsd { get; set; }
    public decimal? CacheWriteCostPerMillionUsd { get; set; }
    public decimal? CacheReadCostPerMillionUsd { get; set; }
    public decimal? OutputCostPerMillionUsd { get; set; }

    public LlmProviderOptions Clone() => (LlmProviderOptions)MemberwiseClone();

    /// <summary>A copy that is safe to log or return from a diagnostics endpoint.</summary>
    public LlmProviderOptions Redacted()
    {
        var copy = Clone();
        copy.ApiKey = string.IsNullOrEmpty(ApiKey) ? null : "***";
        return copy;
    }
}

/// <summary>Root of the <c>Llm</c> configuration section.</summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>Role name (case-insensitive) to provider settings, e.g. <c>chat</c>, <c>assist</c>, <c>embedding</c>.</summary>
    public Dictionary<string, LlmProviderOptions> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
