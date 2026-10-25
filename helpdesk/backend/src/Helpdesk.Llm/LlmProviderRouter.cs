using System.Collections.Concurrent;
using Helpdesk.Llm.Anthropic;
using Helpdesk.Llm.Offline;
using Helpdesk.Llm.OpenAi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helpdesk.Llm;

/// <summary>
/// Resolves providers by role from <see cref="LlmOptions"/>. Providers are created lazily and cached per role, so a
/// role whose key is missing only fails when it is first used.
/// </summary>
public sealed class LlmProviderRouter(
    IOptions<LlmOptions> options,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory) : ILlmProviderRouter
{
    public const string HttpClientName = "Helpdesk.Llm";

    private readonly ConcurrentDictionary<string, Lazy<IChatProvider>> _chat = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<IEmbeddingProvider>> _embedding = new(StringComparer.OrdinalIgnoreCase);

    public bool HasRole(string role) => options.Value.Roles.ContainsKey(role);

    public IChatProvider GetChat(string role, ModelTier tier = ModelTier.Standard) =>
        _chat.GetOrAdd($"{role}|{tier}", _ => new Lazy<IChatProvider>(() => CreateChat(Resolve(role).ForTier(tier)))).Value;

    public IEmbeddingProvider GetEmbedding(string role) =>
        _embedding.GetOrAdd(role, r => new Lazy<IEmbeddingProvider>(() => CreateEmbedding(Resolve(r)))).Value;

    public LlmProviderOptions? Describe(string role)
    {
        if (!options.Value.Roles.TryGetValue(role, out var configured)) return null;
        return WithEnvironmentDefaults(configured).Redacted();
    }

    private LlmProviderOptions Resolve(string role)
    {
        if (!options.Value.Roles.TryGetValue(role, out var configured))
            throw new InvalidOperationException($"No LLM role '{role}' is configured. Add Llm:Roles:{role}.");
        var resolved = WithEnvironmentDefaults(configured);
        if (string.IsNullOrWhiteSpace(resolved.Model) && resolved.Provider != LlmProviders.Offline)
            throw new InvalidOperationException($"Llm:Roles:{role}:Model is required for provider '{resolved.Provider}'.");
        if (RequiresKey(resolved.Provider) && string.IsNullOrEmpty(resolved.ApiKey))
            throw new InvalidOperationException(
                $"Llm:Roles:{role} uses {RateLimitedSender.DisplayName(resolved.Provider)} but no API key is configured " +
                $"(set ApiKey or {KeyVariable(resolved.Provider)}).");
        return resolved;
    }

    internal static LlmProviderOptions WithEnvironmentDefaults(LlmProviderOptions configured)
    {
        var options = configured.Clone();
        options.Provider = LlmProviders.Normalize(options.Provider);
        if (!LlmProviders.All.Contains(options.Provider))
            throw new InvalidOperationException(
                $"Unknown LLM provider '{configured.Provider}'. Expected one of: {string.Join(", ", LlmProviders.All)}.");

        if (string.IsNullOrEmpty(options.ApiKey) && KeyVariable(options.Provider) is { } variable)
            options.ApiKey = Environment.GetEnvironmentVariable(variable);
        if (options.Provider == LlmProviders.AzureOpenAI)
        {
            options.Endpoint ??= Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
            options.ApiVersion ??= Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION");
        }
        return options;
    }

    private static bool RequiresKey(string provider) =>
        provider is LlmProviders.Anthropic or LlmProviders.OpenAI or LlmProviders.AzureOpenAI or LlmProviders.Grok;

    private static string? KeyVariable(string provider) => provider switch
    {
        LlmProviders.Anthropic => "ANTHROPIC_API_KEY",
        LlmProviders.OpenAI => "OPENAI_API_KEY",
        LlmProviders.AzureOpenAI => "AZURE_OPENAI_API_KEY",
        LlmProviders.Grok => "XAI_API_KEY",
        _ => null
    };

    private IChatProvider CreateChat(LlmProviderOptions o) => o.Provider switch
    {
        LlmProviders.Offline => new OfflineChatProvider(o),
        LlmProviders.Anthropic => new AnthropicChatProvider(
            httpClientFactory.CreateClient(HttpClientName), o, loggerFactory.CreateLogger<AnthropicChatProvider>()),
        _ => new OpenAiCompatibleChatProvider(
            o.Provider, httpClientFactory.CreateClient(HttpClientName), o, loggerFactory.CreateLogger<OpenAiCompatibleChatProvider>())
    };

    private IEmbeddingProvider CreateEmbedding(LlmProviderOptions o) => o.Provider switch
    {
        LlmProviders.Offline => new OfflineEmbeddingProvider(o),
        LlmProviders.Anthropic or LlmProviders.Grok => throw new LlmCapabilityException(
            $"{RateLimitedSender.DisplayName(o.Provider)} does not offer an embeddings API; use openai, azure_openai, ollama, lm_studio or offline."),
        _ => new OpenAiCompatibleEmbeddingProvider(
            o.Provider, httpClientFactory.CreateClient(HttpClientName), o, loggerFactory.CreateLogger<OpenAiCompatibleEmbeddingProvider>())
    };
}

public static class LlmServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="LlmOptions"/> (bound from <paramref name="section"/> when given), the shared resilient
    /// HTTP client and <see cref="ILlmProviderRouter"/>.
    /// </summary>
    public static IServiceCollection AddLlmProviders(this IServiceCollection services, IConfiguration? section = null)
    {
        var builder = services.AddOptions<LlmOptions>();
        if (section is not null) builder.Bind(section);

        services.AddHttpClient(LlmProviderRouter.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .AddHelpdeskResilience(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(150), callerHandlesRateLimits: true);
        services.AddSingleton<LlmProviderRouter>();
        services.AddSingleton<ILlmProviderRouter>(sp => sp.GetRequiredService<LlmProviderRouter>());
        return services;
    }
}
