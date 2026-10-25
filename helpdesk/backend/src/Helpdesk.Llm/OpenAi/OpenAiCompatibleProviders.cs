using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Llm.OpenAi;

/// <summary>
/// Endpoint and authentication rules shared by the OpenAI-compatible family (OpenAI, Azure OpenAI, Grok, Ollama and
/// LM Studio). Azure uses deployment-scoped paths, an <c>api-version</c> query and the <c>api-key</c> header; the
/// others use <c>{endpoint}/{operation}</c> with a bearer token.
/// </summary>
internal sealed class OpenAiEndpoint
{
    public const string DefaultAzureApiVersion = "2024-10-21";

    private readonly LlmProviderOptions _options;
    private readonly string _provider;
    private readonly string _baseUrl;

    public OpenAiEndpoint(string provider, LlmProviderOptions options)
    {
        _provider = provider;
        _options = options;
        var endpoint = string.IsNullOrWhiteSpace(options.Endpoint) ? DefaultEndpoint(provider) : options.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException($"LLM provider '{provider}' requires an Endpoint.");
        _baseUrl = endpoint.TrimEnd('/');
    }

    public static string? DefaultEndpoint(string provider) => provider switch
    {
        LlmProviders.OpenAI => "https://api.openai.com/v1",
        LlmProviders.Grok => "https://api.x.ai/v1",
        LlmProviders.Ollama => "http://localhost:11434/v1",
        LlmProviders.LMStudio => "http://localhost:1234/v1",
        _ => null
    };

    public Uri UriFor(string operation) => _provider == LlmProviders.AzureOpenAI
        ? new Uri($"{_baseUrl}/openai/deployments/{Uri.EscapeDataString(_options.Model)}/{operation}?api-version={Uri.EscapeDataString(_options.ApiVersion ?? DefaultAzureApiVersion)}")
        : new Uri($"{_baseUrl}/{operation}");

    public HttpRequestMessage CreatePost(string operation, JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, UriFor(operation)) { Content = JsonContent.Create(body) };
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            if (_provider == LlmProviders.AzureOpenAI) request.Headers.TryAddWithoutValidation("api-key", _options.ApiKey);
            else request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_options.ApiKey}");
        }
        return request;
    }
}

/// <summary>Chat Completions API for the OpenAI-compatible family.</summary>
public sealed class OpenAiCompatibleChatProvider : IChatProvider
{
    private readonly HttpClient _http;
    private readonly LlmProviderOptions _options;
    private readonly ILogger _logger;
    private readonly OpenAiEndpoint _endpoint;

    public OpenAiCompatibleChatProvider(string provider, HttpClient http, LlmProviderOptions options, ILogger<OpenAiCompatibleChatProvider> logger)
    {
        Provider = provider;
        _http = http;
        _options = options;
        _logger = logger;
        _endpoint = new OpenAiEndpoint(provider, options);
    }

    public string Provider { get; }
    public string Model => _options.Model;
    public LlmCapabilities Capabilities => LlmCapabilities.Streaming | LlmCapabilities.StructuredOutput
        | (_options.SupportsVision ?? Provider is LlmProviders.OpenAI or LlmProviders.AzureOpenAI or LlmProviders.Grok
            ? LlmCapabilities.Vision
            : LlmCapabilities.None);

    public Task<string> CompleteAsync(ChatRequest request, ILlmProgress? progress = null, CancellationToken cancellationToken = default) =>
        RunAsync(request, progress, stream: false, cancellationToken);

    public Task<string> StreamAsync(ChatRequest request, ILlmProgress progress, CancellationToken cancellationToken = default) =>
        RunAsync(request, progress, stream: true, cancellationToken);

    private async Task<string> RunAsync(ChatRequest request, ILlmProgress? progress, bool stream, CancellationToken ct)
    {
        if (request.Tools is not null)
        {
            throw new LlmCapabilityException(
                $"{RateLimitedSender.DisplayName(Provider)} does not support provider-native server tools; remove them or use a provider that supports them.");
        }

        using var activity = LlmTelemetry.StartRequest(Provider, Model);
        var body = BuildBody(request, stream);
        var usage = TokenUsage.Empty;
        var hasUsage = false;
        try
        {
            using var response = await RateLimitedSender.SendAsync(
                _http, () => _endpoint.CreatePost("chat/completions", body), stream, Provider, _options, progress, ct);

            string text;
            if (stream)
            {
                (text, usage) = await ReadStreamAsync(response, progress!, ct);
            }
            else
            {
                var json = await response.Content.ReadFromJsonAsync<JsonNode>(ct)
                    ?? throw new InvalidOperationException($"Empty response from {RateLimitedSender.DisplayName(Provider)}.");
                var message = json["choices"] is JsonArray { Count: > 0 } choices ? choices[0]?["message"] : null;
                text = message?["content"]?.GetValue<string>() ?? "";
                var refusal = message?["refusal"]?.GetValue<string>();
                if (text.Length == 0 && !string.IsNullOrEmpty(refusal)) text = refusal;
                usage = ReadUsage(json["usage"]);
            }
            hasUsage = true;

            if (text.Length == 0)
                throw new InvalidOperationException($"{RateLimitedSender.DisplayName(Provider)} response contained no text content.");
            return text;
        }
        finally
        {
            if (hasUsage) usage.Record(Provider, _options, activity, _logger);
        }
    }

    /// <summary>Plain text, or text plus <c>image_url</c> parts when the request carries images. Documents are not sent: callers give these models extracted text.</summary>
    internal static JsonNode UserContent(ChatRequest request)
    {
        var images = request.Parts?.Where(p => p.Kind == ChatContentKinds.Image).ToList();
        if (images is not { Count: > 0 }) return request.UserContent;

        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = request.UserContent });
        foreach (var image in images)
        {
            content.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data)}" }
            });
        }
        return content;
    }

    private JsonObject BuildBody(ChatRequest request, bool stream)
    {
        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = UserContent(request) })
        };
        // OpenAI's newer models (and Azure deployments of them) only accept max_completion_tokens.
        var tokenParameter = Provider is LlmProviders.OpenAI or LlmProviders.AzureOpenAI ? "max_completion_tokens" : "max_tokens";
        body[tokenParameter] = request.MaxTokens ?? _options.MaxTokens;
        if (_options.SupportsTemperature ?? SupportsTemperature(_options.Model)) body["temperature"] = request.Temperature;
        if (request.ResponseSchema is not null)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "response",
                    ["schema"] = request.ResponseSchema.DeepClone(),
                    // Strict mode needs every property required and additionalProperties:false; only claim it when true.
                    ["strict"] = IsStrictCompatible(request.ResponseSchema)
                }
            };
        }
        if (stream)
        {
            body["stream"] = true;
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }
        return body;
    }

    /// <summary>Reasoning models (o-series, gpt-5) reject sampling parameters.</summary>
    internal static bool SupportsTemperature(string model) =>
        !Regex.IsMatch(model, @"^(o\d|gpt-5)", RegexOptions.IgnoreCase);

    internal static bool IsStrictCompatible(JsonNode? schema)
    {
        if (schema is not JsonObject obj) return true;
        if (obj["type"]?.GetValueKind() == System.Text.Json.JsonValueKind.String && obj["type"]!.GetValue<string>() == "object")
        {
            if (obj["additionalProperties"] is not JsonValue ap || ap.GetValueKind() != System.Text.Json.JsonValueKind.False) return false;
            var properties = obj["properties"] as JsonObject ?? [];
            var required = (obj["required"] as JsonArray ?? []).Select(r => r?.GetValue<string>()).ToHashSet();
            if (properties.Any(p => !required.Contains(p.Key))) return false;
            if (properties.Any(p => !IsStrictCompatible(p.Value))) return false;
        }
        return obj["items"] is not { } items || IsStrictCompatible(items);
    }

    private async Task<(string Text, TokenUsage Usage)> ReadStreamAsync(HttpResponseMessage response, ILlmProgress progress, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var text = new StringBuilder();
        var usage = TokenUsage.Empty;
        var finished = false;

        var idleTimeout = TimeSpan.FromSeconds(_options.StreamIdleTimeoutSeconds);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(idleTimeout);
        try
        {
            await foreach (var item in SseParser.Create(stream).EnumerateAsync(idle.Token))
            {
                idle.CancelAfter(idleTimeout);
                if (string.IsNullOrWhiteSpace(item.Data)) continue;
                if (item.Data.Trim() == "[DONE]")
                {
                    finished = true;
                    break;
                }
                if (JsonNode.Parse(item.Data) is not JsonNode chunk) continue;
                if (chunk["error"] is JsonNode error)
                    throw new HttpRequestException($"{RateLimitedSender.DisplayName(Provider)} stream error: {error["message"] ?? error}");

                if (chunk["usage"] is JsonObject usageNode) usage = ReadUsage(usageNode);
                var choice = chunk["choices"] is JsonArray { Count: > 0 } choices ? choices[0] : null;
                var piece = choice?["delta"]?["content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(piece))
                {
                    text.Append(piece);
                    await progress.OnTextAsync(piece, ct);
                }
                if (choice?["finish_reason"] is JsonValue reason && reason.GetValueKind() == System.Text.Json.JsonValueKind.String)
                    finished = true;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No data from {RateLimitedSender.DisplayName(Provider)} for {idleTimeout.TotalSeconds:0}s while streaming.");
        }

        if (!finished)
            throw new HttpRequestException($"{RateLimitedSender.DisplayName(Provider)} stream ended before the message completed.");
        return (text.ToString(), usage);
    }

    private static TokenUsage ReadUsage(JsonNode? usage)
    {
        var prompt = GetInt(usage, "prompt_tokens") ?? 0;
        var cached = GetInt(usage?["prompt_tokens_details"], "cached_tokens") ?? 0;
        // OpenAI reports cached tokens inside prompt_tokens; split them so Input means uncached input everywhere.
        return new TokenUsage(Math.Max(0, prompt - cached), GetInt(usage, "completion_tokens") ?? 0, 0, cached);
    }

    private static int? GetInt(JsonNode? node, string property) =>
        node?[property] is JsonValue value && value.TryGetValue<int>(out var result) ? result : null;
}

/// <summary>Embeddings API for the OpenAI-compatible family (OpenAI, Azure OpenAI, Ollama and LM Studio).</summary>
public sealed class OpenAiCompatibleEmbeddingProvider : IEmbeddingProvider
{
    /// <summary>Batch size per request; well under the documented 2048-input limit.</summary>
    private const int MaxBatch = 256;

    private readonly HttpClient _http;
    private readonly LlmProviderOptions _options;
    private readonly ILogger _logger;
    private readonly OpenAiEndpoint _endpoint;

    public OpenAiCompatibleEmbeddingProvider(string provider, HttpClient http, LlmProviderOptions options, ILogger<OpenAiCompatibleEmbeddingProvider> logger)
    {
        Provider = provider;
        _http = http;
        _options = options;
        _logger = logger;
        _endpoint = new OpenAiEndpoint(provider, options);
        Dimensions = options.Dimensions ?? KnownDimensions(options.Model)
            ?? throw new InvalidOperationException(
                $"Embedding model '{options.Model}' has no known vector size; set Dimensions in its Llm:Roles configuration.");
    }

    public string Provider { get; }
    public string Model => _options.Model;
    public int Dimensions { get; }

    public static int? KnownDimensions(string model) => model.ToLowerInvariant() switch
    {
        "text-embedding-3-small" or "text-embedding-ada-002" => 1536,
        "text-embedding-3-large" => 3072,
        var m when m.StartsWith("nomic-embed-text", StringComparison.Ordinal) => 768,
        var m when m.StartsWith("mxbai-embed-large", StringComparison.Ordinal) => 1024,
        var m when m.StartsWith("all-minilm", StringComparison.Ordinal) => 384,
        _ => null
    };

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        if (inputs.Count == 0) return [];
        using var activity = LlmTelemetry.StartRequest(Provider, Model, operation: "embeddings");
        var results = new List<float[]>(inputs.Count);
        var usage = TokenUsage.Empty;
        try
        {
            foreach (var batch in inputs.Chunk(MaxBatch))
            {
                var body = new JsonObject
                {
                    ["model"] = _options.Model,
                    ["input"] = new JsonArray(batch.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray())
                };
                // Only the text-embedding-3 family can shorten vectors; others reject the parameter.
                if (_options.Dimensions is { } dims && Model.StartsWith("text-embedding-3", StringComparison.OrdinalIgnoreCase))
                    body["dimensions"] = dims;

                using var response = await RateLimitedSender.SendAsync(
                    _http, () => _endpoint.CreatePost("embeddings", body), stream: false, Provider, _options, null, cancellationToken);
                var json = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken)
                    ?? throw new InvalidOperationException($"Empty embedding response from {RateLimitedSender.DisplayName(Provider)}.");
                var data = json["data"]?.AsArray()
                    ?? throw new InvalidOperationException("Embedding response contained no data.");

                var vectors = data
                    .Select((d, i) => (Index: d?["index"]?.GetValue<int>() ?? i, Vector: d!["embedding"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray()))
                    .OrderBy(x => x.Index)
                    .Select(x => x.Vector)
                    .ToList();
                if (vectors.Count != batch.Length)
                    throw new InvalidOperationException($"Expected {batch.Length} embeddings but received {vectors.Count}.");
                foreach (var vector in vectors)
                {
                    if (vector.Length != Dimensions)
                        throw new InvalidOperationException(
                            $"Embedding model '{Model}' returned {vector.Length} dimensions; configuration expects {Dimensions}.");
                }
                results.AddRange(vectors);
                usage = usage.Add(new TokenUsage(GetInt(json["usage"], "prompt_tokens") ?? 0, 0, 0, 0));
            }
            LlmTelemetry.EmbeddedInputs.Add(inputs.Count, LlmTelemetry.Tags(Provider, Model));
            return results;
        }
        finally
        {
            usage.Record(Provider, _options, activity, _logger);
        }
    }

    private static int? GetInt(JsonNode? node, string property) =>
        node?[property] is JsonValue value && value.TryGetValue<int>(out var result) ? result : null;
}
