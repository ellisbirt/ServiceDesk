using Helpdesk.Infrastructure.Kb;
using Helpdesk.Infrastructure.Search;
using Helpdesk.Llm;
using Microsoft.Extensions.Configuration;

namespace Helpdesk.Infrastructure;

/// <summary>
/// Keeps the vendors of the model, the web search and the embeddings the same where the chat vendor offers them, so
/// switching <c>Llm:Roles:chat</c> does not also mean finding a second and third account. Anything set explicitly wins.
/// </summary>
public static class ProviderAffinity
{
    /// <summary>The embedding model used when the chat vendor is OpenAI and no embedding role is configured.</summary>
    public const string OpenAiEmbeddingModel = "text-embedding-3-small";
    public const int OpenAiEmbeddingDimensions = 1536;

    /// <summary>The vendor the intake chat will use, or null when it will run offline.</summary>
    public static string? ChatVendor(IConfiguration config)
    {
        var provider = config[$"{LlmOptions.SectionName}:Roles:{DependencyInjection.ChatRole}:Provider"];
        if (!string.IsNullOrWhiteSpace(provider))
        {
            var normalized = LlmProviders.Normalize(provider);
            return normalized == LlmProviders.Offline ? null : normalized;
        }

        var key = config[$"{Ai.AnthropicOptions.SectionName}:ApiKey"];
        if (string.IsNullOrWhiteSpace(key)) key = config["ANTHROPIC_API_KEY"];
        return string.IsNullOrWhiteSpace(key) ? null : LlmProviders.Anthropic;
    }

    /// <summary>
    /// The web search backend. In order: <c>Search:Provider</c> when set; a third-party provider that already has
    /// credentials (including the legacy <c>Search:ApiKey</c>, which means Tavily); the chat vendor's own search when it has one (Claude); otherwise Tavily, which runs as a stub until
    /// it is given a key.
    /// </summary>
    public static string SearchProvider(IConfiguration config)
    {
        var explicitProvider = config[$"{SearchOptions.SectionName}:Provider"];
        if (!string.IsNullOrWhiteSpace(explicitProvider)) return explicitProvider;

        foreach (var definition in SearchProviderCatalog.All)
        {
            var options = config.GetSection($"{SearchOptions.SectionName}:Providers:{definition.Name}")
                .Get<SearchProviderOptions>() ?? new();
            if (definition.IsConfigured(options)) return definition.Name;
        }

        // The shorthand key has always meant "the default provider's key", and the default was Tavily.
        if (!string.IsNullOrWhiteSpace(config[$"{SearchOptions.SectionName}:ApiKey"])) return SearchProviderCatalog.Tavily;

        return ChatVendor(config) == LlmProviders.Anthropic
            ? SearchProviderCatalog.NativeClaude
            : SearchProviderCatalog.Tavily;
    }

    /// <summary>The name of the role the knowledge base embeds questions with.</summary>
    public static string EmbeddingRoleName(IConfiguration config) =>
        config[$"{KbOptions.SectionName}:EmbeddingRole"] is { Length: > 0 } name ? name : new KbOptions().EmbeddingRole;

    /// <summary>
    /// An embedding role taken from the chat vendor, or null when none is configured and it cannot be derived. Only
    /// OpenAI has a model we can name without being told: Anthropic and Grok have no embeddings API, and Azure OpenAI,
    /// Ollama and LM Studio need a deployment or model name of the operator's choosing.
    /// </summary>
    public static LlmProviderOptions? DerivedEmbeddingRole(IConfiguration config)
    {
        if (config.GetSection($"{LlmOptions.SectionName}:Roles:{EmbeddingRoleName(config)}").Exists()) return null;
        if (ChatVendor(config) != LlmProviders.OpenAI) return null;

        var chat = config.GetSection($"{LlmOptions.SectionName}:Roles:{DependencyInjection.ChatRole}")
            .Get<LlmProviderOptions>() ?? new();
        return new LlmProviderOptions
        {
            Provider = LlmProviders.OpenAI,
            Model = OpenAiEmbeddingModel,
            Dimensions = OpenAiEmbeddingDimensions,
            Endpoint = chat.Endpoint,
            ApiKey = chat.ApiKey
        };
    }
}
