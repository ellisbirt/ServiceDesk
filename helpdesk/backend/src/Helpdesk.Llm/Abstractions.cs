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

/// <summary>
/// The model declined to answer (Anthropic <c>stop_reason: refusal</c>). Any text it produced first is incomplete and
/// must not be shown as a reply.
/// </summary>
public sealed class LlmRefusalException(string message) : Exception(message);

/// <summary>The provider cannot honour a request feature (for example native web search on a non-Anthropic model).</summary>
public sealed class LlmCapabilityException(string message) : Exception(message);

/// <summary>
/// How much model a call needs. Each vendor provides the tiers; one that offers fewer models serves several tiers with
/// the same model (for example a single local model).
/// </summary>
public enum ModelTier
{
    /// <summary>Small, cheap and quick: short, routine work such as classification and simple questions.</summary>
    Fast,
    /// <summary>The default model of the role.</summary>
    Standard,
    /// <summary>The most capable model, for large or complex work.</summary>
    Deep
}

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
    /// <summary>Images and documents to show the model with the text. Providers without <see cref="LlmCapabilities.Vision"/> must not be sent images.</summary>
    public IReadOnlyList<ChatContentPart>? Parts { get; init; }
    /// <summary>Which of the vendor's models should serve the call. Providers ignore it; the router and tier decorator act on it.</summary>
    public ModelTier Tier { get; init; } = ModelTier.Standard;
}

[Flags]
public enum LlmCapabilities
{
    None = 0,
    Streaming = 1,
    StructuredOutput = 2,
    /// <summary>Server-side tools such as Anthropic's web search.</summary>
    ServerTools = 4,
    PromptCaching = 8,
    /// <summary>Can read images given with the request.</summary>
    Vision = 16,
    /// <summary>Can read PDF documents given natively with the request.</summary>
    Documents = 32
}

/// <summary>An image or document that is sent to the model next to the text of a request.</summary>
/// <param name="Kind"><see cref="ChatContentKinds.Image"/> or <see cref="ChatContentKinds.Document"/>.</param>
/// <param name="MediaType">For example <c>image/png</c> or <c>application/pdf</c>.</param>
/// <param name="Data">The file bytes.</param>
/// <param name="Name">The file name, for logs and prompts only.</param>
public sealed record ChatContentPart(string Kind, string MediaType, byte[] Data, string? Name = null);

public static class ChatContentKinds
{
    public const string Image = "image";
    public const string Document = "document";
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
    IChatProvider GetChat(string role, ModelTier tier = ModelTier.Standard);
    IEmbeddingProvider GetEmbedding(string role);
    /// <summary>The effective settings for a role (API key removed), or null when the role is not configured.</summary>
    LlmProviderOptions? Describe(string role);
}
