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
/// The model a vendor uses for the Fast or Deep tier of a role. Costs apply to that model only: left empty they are not
/// estimated, so a cheaper model is never priced at the standard model's rates.
/// </summary>
public sealed class LlmTierOptions
{
    /// <summary>Model id (or Azure deployment name). Empty means the vendor's default for the tier, if it has one.</summary>
    public string? Model { get; set; }
    public decimal? InputCostPerMillionUsd { get; set; }
    public decimal? CacheWriteCostPerMillionUsd { get; set; }
    public decimal? CacheReadCostPerMillionUsd { get; set; }
    public decimal? OutputCostPerMillionUsd { get; set; }
}

/// <summary>
/// The models a vendor offers for the Fast and Deep tiers when the role does not name them. Only vendors with a known
/// model family are listed; the others (OpenAI, Azure OpenAI, Grok and local servers) use the role's own model for every
/// tier until the tier models are configured.
/// </summary>
public static class LlmTierDefaults
{
    public static string? Model(string provider, ModelTier tier) => (provider, tier) switch
    {
        (LlmProviders.Anthropic, ModelTier.Fast) => "claude-haiku-5-5",
        (LlmProviders.Anthropic, ModelTier.Deep) => "claude-opus-5-5",
        _ => null
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
    /// <summary>
    /// Whether the model can read images. Unset uses the vendor's usual answer: yes for Anthropic, OpenAI, Azure OpenAI
    /// and Grok, no for local servers (Ollama, LM Studio), where it depends on the model that is loaded.
    /// </summary>
    public bool? SupportsVision { get; set; }
    /// <summary>Overrides the model-name heuristic for whether sampling temperature may be sent.</summary>
    public bool? SupportsTemperature { get; set; }
    /// <summary>Embedding vector length. Required for embedding models we do not recognise.</summary>
    public int? Dimensions { get; set; }

    public int RateLimitMaxWaitSeconds { get; set; } = 120;
    public int RateLimitMaxRetries { get; set; } = 4;
    /// <summary>A stream that delivers nothing for this long is abandoned. 0 means never (slow local models can be silent for minutes).</summary>
    public int StreamIdleTimeoutSeconds { get; set; } = 30;

    public decimal? InputCostPerMillionUsd { get; set; }
    public decimal? CacheWriteCostPerMillionUsd { get; set; }
    public decimal? CacheReadCostPerMillionUsd { get; set; }
    public decimal? OutputCostPerMillionUsd { get; set; }

    /// <summary>The Fast tier's model. Leave unset to use the vendor's default (or <see cref="Model"/> if it has none).</summary>
    public LlmTierOptions? FastTier { get; set; }
    /// <summary>The Deep tier's model. Leave unset to use the vendor's default (or <see cref="Model"/> if it has none).</summary>
    public LlmTierOptions? DeepTier { get; set; }

    /// <summary>The model that serves <paramref name="tier"/>: the configured one, else the vendor's default, else <see cref="Model"/>.</summary>
    public string TierModel(ModelTier tier) => ForTier(tier).Model;

    /// <summary>
    /// The settings for one tier. The Standard tier is this role itself, and a tier that resolves to the same model
    /// returns this instance, so a vendor with a single model serves every tier unchanged.
    /// </summary>
    public LlmProviderOptions ForTier(ModelTier tier)
    {
        if (tier == ModelTier.Standard) return this;

        var configured = tier == ModelTier.Fast ? FastTier : DeepTier;
        var model = !string.IsNullOrWhiteSpace(configured?.Model)
            ? configured.Model
            : LlmTierDefaults.Model(LlmProviders.Normalize(Provider), tier) ?? Model;
        if (string.Equals(model, Model, StringComparison.OrdinalIgnoreCase)) return this;

        var copy = Clone();
        copy.Model = model;
        copy.InputCostPerMillionUsd = configured?.InputCostPerMillionUsd;
        copy.CacheWriteCostPerMillionUsd = configured?.CacheWriteCostPerMillionUsd;
        copy.CacheReadCostPerMillionUsd = configured?.CacheReadCostPerMillionUsd;
        copy.OutputCostPerMillionUsd = configured?.OutputCostPerMillionUsd;
        copy.FastTier = null;
        copy.DeepTier = null;
        return copy;
    }

    public LlmProviderOptions Clone() => (LlmProviderOptions)MemberwiseClone();

    /// <summary>A copy that is safe to log or return from a diagnostics endpoint.</summary>
    public LlmProviderOptions Redacted()
    {
        var copy = Clone();
        copy.ApiKey = string.IsNullOrEmpty(ApiKey) ? null : "***";
        return copy;
    }
}

/// <summary>
/// How long a call to a model may take (<c>Llm:Http</c>). The defaults suit hosted vendors. A local model on modest hardware
/// (Ollama, LM Studio) can take minutes before the first token, so each limit can be raised, or set to 0 for "no limit".
/// The HTTP resilience pipeline does not accept an infinite timeout, so "no limit" is the most it allows: 12 hours for one try
/// (its circuit breaker window must be twice that) and 24 hours for the whole call.
/// </summary>
public sealed class LlmHttpOptions
{
    public const string SectionName = "Http";

    /// <summary>The longest value accepted for one try, in seconds, and what 0 means.</summary>
    public const int MaxAttemptSeconds = 12 * 3600;

    /// <summary>The longest value accepted for the whole call, in seconds, and what 0 means.</summary>
    public const int MaxTotalSeconds = 24 * 3600;

    /// <summary>One try of one call, in seconds. 0 means no limit. A failed try can be retried, so the call may take longer.</summary>
    public int AttemptTimeoutSeconds { get; set; } = 60;

    /// <summary>The whole call including retries, in seconds. 0 means no limit.</summary>
    public int TotalTimeoutSeconds { get; set; } = 150;

    /// <summary>
    /// What the HTTP pipeline is given. The pipeline refuses a total limit that is not longer than the per-try limit, and that
    /// would stop the app from starting, so a per-try limit is held below the total (it could never usefully exceed it) and
    /// both are held inside what the pipeline accepts. <c>/status</c> reports a configuration that needed this.
    /// </summary>
    public TimeSpan TotalTimeout =>
        TimeSpan.FromSeconds(TotalTimeoutSeconds <= 0 ? MaxTotalSeconds : Math.Clamp(TotalTimeoutSeconds, 2, MaxTotalSeconds));

    /// <inheritdoc cref="TotalTimeout"/>
    public TimeSpan AttemptTimeout
    {
        get
        {
            var attempt = TimeSpan.FromSeconds(AttemptTimeoutSeconds <= 0 ? MaxAttemptSeconds : Math.Clamp(AttemptTimeoutSeconds, 1, MaxAttemptSeconds));
            var total = TotalTimeout;
            return attempt < total ? attempt : total - TimeSpan.FromSeconds(1);
        }
    }
}

/// <summary>Root of the <c>Llm</c> configuration section.</summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>Timeouts for calls to a model, shared by every role. See <see cref="LlmHttpOptions"/>.</summary>
    public LlmHttpOptions Http { get; set; } = new();

    /// <summary>Role name (case-insensitive) to provider settings, e.g. <c>chat</c>, <c>assist</c>, <c>embedding</c>.</summary>
    public Dictionary<string, LlmProviderOptions> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
