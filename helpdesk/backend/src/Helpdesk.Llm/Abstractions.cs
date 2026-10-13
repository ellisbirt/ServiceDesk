using System.Text.Json.Nodes;

namespace Helpdesk.Llm;

/// <summary>
/// Receives incremental progress while a model call runs. Implementations must not throw for transient
/// delivery problems; progress is best effort.
/// </summary>
public interface ILlmProgress
{
    /// <summary>A fragment of the assistant's reply, in order.</summary>
    ValueTask OnTextAsync(string delta, CancellationToken cancellationToken);

    /// <summary>The upstream model asked us to wait (HTTP 429 Retry-After) before the call is retried.</summary>
    ValueTask OnRateLimitedAsync(TimeSpan retryAfter, int attempt, CancellationToken cancellationToken);
}

/// <summary>The upstream model rate limited the request and the allowed waiting budget is exhausted.</summary>
public class UpstreamRateLimitedException(TimeSpan? retryAfter, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>The provider cannot honour a request feature (for example native web search on a non-Anthropic model).</summary>
public sealed class LlmCapabilityException(string message) : Exception(message);

/// <summary>One single-turn model call. Callers wrap untrusted text before putting it in <see cref="UserContent"/>.</summary>
public sealed record ChatRequest
{
    public ChatRequest() { }

    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public ChatRequest(string systemPrompt, string userContent, double temperature = 0, int? MaxTokens = null,
        JsonObject? ResponseSchema = null, JsonArray? Tools = null)
    {
        SystemPrompt = systemPrompt;
        UserContent = userContent;
        Temperature = temperature;
        this.MaxTokens = MaxTokens;
        this.ResponseSchema = ResponseSchema;
        this.Tools = Tools;
    }

    public required string SystemPrompt { get; init; }
    public required string UserContent { get; init; }
    /// <summary>Ignored by models that reject sampling parameters.</summary>
    public double Temperature { get; init; }
    /// <summary>When null, the provider's configured <see cref="LlmProviderOptions.MaxTokens"/> is used.</summary>
    public int? MaxTokens { get; init; }
    /// <summary>JSON schema the reply must match. Providers use native structured output where they can.</summary>
    public JsonObject? ResponseSchema { get; init; }
    /// <summary>Provider-native server tools. Only providers with <see cref="LlmCapabilities.ServerTools"/> accept them.</summary>
    public JsonArray? Tools { get; init; }
}

[Flags]
public enum LlmCapabilities
{
    None = 0,
    Streaming = 1,
    StructuredOutput = 2,
    /// <summary>Server-side tools such as Anthropic's web search.</summary>
    ServerTools = 4,
    PromptCaching = 8
}

public interface IChatProvider
{
    /// <summary>Provider id from <see cref="LlmProviders"/>.</summary>
    string Provider { get; }
    string Model { get; }
    LlmCapabilities Capabilities { get; }

    /// <param name="progress">Optional; only told about rate-limit waits, the reply is returned whole.</param>
    Task<string> CompleteAsync(ChatRequest request, ILlmProgress? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Streams text to <paramref name="progress"/> as it arrives. The returned text equals the concatenated deltas.</summary>
    Task<string> StreamAsync(ChatRequest request, ILlmProgress progress, CancellationToken cancellationToken = default);
}

public interface IEmbeddingProvider
{
    string Provider { get; }
    string Model { get; }
    /// <summary>Vector length this provider produces; stored with each vector so mixed models can be detected.</summary>
    int Dimensions { get; }

    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default);
}

/// <summary>Resolves the configured provider for a named role (for example "chat", "assist" or "embedding").</summary>
public interface ILlmProviderRouter
{
    bool HasRole(string role);
    IChatProvider GetChat(string role);
    IEmbeddingProvider GetEmbedding(string role);
    /// <summary>The effective settings for a role (API key removed), or null when the role is not configured.</summary>
    LlmProviderOptions? Describe(string role);
}
