using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Llm.Anthropic;

/// <summary>
/// Anthropic Messages API. Supports streaming, structured output (<c>output_config</c>), server tools such as web
/// search (with <c>pause_turn</c> continuation and citation collection) and prompt caching of the system prompt.
/// </summary>
public sealed class AnthropicChatProvider(HttpClient http, LlmProviderOptions options, ILogger<AnthropicChatProvider> logger)
    : IChatProvider
{
    public const string DefaultEndpoint = "https://api.anthropic.com/";

    // A server-tool turn can pause mid-way; we resume it a bounded number of times.
    private const int MaxPauseContinuations = 3;

    private readonly Uri _messagesUri = BuildUri(options.Endpoint);

    public string Provider => LlmProviders.Anthropic;
    public string Model => options.Model;
    public LlmCapabilities Capabilities =>
        LlmCapabilities.Streaming | LlmCapabilities.StructuredOutput | LlmCapabilities.ServerTools | LlmCapabilities.PromptCaching;

    public Task<string> CompleteAsync(ChatRequest request, ILlmProgress? progress = null, CancellationToken cancellationToken = default) =>
        RunAsync(request, progress, stream: false, cancellationToken);

    public Task<string> StreamAsync(ChatRequest request, ILlmProgress progress, CancellationToken cancellationToken = default) =>
        RunAsync(request, progress, stream: true, cancellationToken);

    private static Uri BuildUri(string? endpoint)
    {
        var baseUri = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint;
        if (!baseUri.EndsWith('/')) baseUri += "/";
        return new Uri(new Uri(baseUri), "v1/messages");
    }

    private async Task<string> RunAsync(ChatRequest request, ILlmProgress? progress, bool stream, CancellationToken ct)
    {
        using var activity = LlmTelemetry.StartRequest(Provider, Model);

        var messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = request.UserContent });
        var text = new StringBuilder();
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var totalUsage = TokenUsage.Empty;
        var hasUsage = false;
        string? lastStopReason = null;
        try
        {
            for (var turn = 0; turn <= MaxPauseContinuations; turn++)
            {
                var body = BuildBody(request, messages, stream);
                var (content, stopReason, usage) = stream
                    ? await StreamTurnAsync(body, progress!, ct)
                    : await SendTurnAsync(body, progress, ct);
                totalUsage = totalUsage.Add(usage);
                hasUsage = true;
                lastStopReason = stopReason;

                foreach (var block in content)
                {
                    if (block?["type"]?.GetValue<string>() != "text") continue;
                    text.Append(block["text"]?.GetValue<string>());

                    foreach (var citation in block["citations"]?.AsArray() ?? [])
                    {
                        var url = citation?["url"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(url)) sources.TryAdd(url, citation?["title"]?.GetValue<string>() ?? url);
                    }
                }

                if (stopReason != "pause_turn") break;
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() });
            }

            // A refusal can arrive with part of an answer, which would read as a complete reply if returned.
            if (lastStopReason == "refusal") throw new LlmRefusalException("The model declined to answer.");

            if (text.Length == 0)
            {
                throw new InvalidOperationException("Anthropic response contained no text content.");
            }

            if (sources.Count > 0)
            {
                var appendix = new StringBuilder("\n\n**Sources**\n");
                foreach (var (url, title) in sources) appendix.Append($"- [{title}]({url})\n");
                text.Append(appendix);
                // Keep the streamed deltas identical to the final text.
                if (stream) await progress!.OnTextAsync(appendix.ToString(), ct);
            }
            return text.ToString();
        }
        finally
        {
            if (hasUsage) totalUsage.Record(Provider, options, activity, logger);
        }
    }

    private JsonObject BuildBody(ChatRequest request, JsonArray messages, bool stream)
    {
        // The system prompt is sent as a cacheable prefix block so repeated chat turns reuse it.
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["max_tokens"] = request.MaxTokens ?? options.MaxTokens,
            ["system"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = request.SystemPrompt,
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" }
            }),
            ["messages"] = messages.DeepClone()
        };
        if (options.SupportsTemperature ?? SupportsTemperature(options.Model)) body["temperature"] = request.Temperature;
        if (request.Tools is not null) body["tools"] = request.Tools.DeepClone();
        if (request.ResponseSchema is not null)
        {
            body["output_config"] = new JsonObject
            {
                ["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = request.ResponseSchema.DeepClone() }
            };
        }
        if (stream) body["stream"] = true;
        return body;
    }

    internal static bool SupportsTemperature(string model)
    {
        var version = Regex.Match(model, @"-(?<major>\d+)-(?<minor>\d+)(?:-|$)");
        if (!version.Success) return false;

        if (!int.TryParse(version.Groups["major"].Value, out var major) ||
            !int.TryParse(version.Groups["minor"].Value, out var minor))
            return false;

        // Claude 4.7 and newer reject sampling parameters; unknown aliases are kept safe too.
        return major < 4 || major == 4 && minor < 7;
    }

    private Task<HttpResponseMessage> PostAsync(JsonObject body, bool stream, ILlmProgress? progress, CancellationToken ct) =>
        RateLimitedSender.SendAsync(http, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _messagesUri) { Content = JsonContent.Create(body) };
            if (!string.IsNullOrEmpty(options.ApiKey)) request.Headers.TryAddWithoutValidation("x-api-key", options.ApiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", options.AnthropicVersion);
            return request;
        }, stream, Provider, options, progress, ct);

    private async Task<(JsonArray Content, string? StopReason, TokenUsage Usage)> SendTurnAsync(
        JsonObject body, ILlmProgress? progress, CancellationToken ct)
    {
        using var response = await PostAsync(body, stream: false, progress, ct);
        var json = await response.Content.ReadFromJsonAsync<JsonNode>(ct)
            ?? throw new InvalidOperationException("Empty response from Anthropic.");
        var content = json["content"]?.AsArray()
            ?? throw new InvalidOperationException("Anthropic response contained no content.");
        return (content, json["stop_reason"]?.GetValue<string>(), ReadUsage(json["usage"]));
    }

    /// <summary>
    /// Reads one streamed turn. Blocks are rebuilt from the events so a paused server-tool turn can be resumed
    /// exactly as with the non-streaming API.
    /// </summary>
    private async Task<(JsonArray Content, string? StopReason, TokenUsage Usage)> StreamTurnAsync(
        JsonObject body, ILlmProgress progress, CancellationToken ct)
    {
        using var response = await PostAsync(body, stream: true, progress, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);

        var blocks = new SortedDictionary<int, JsonObject>();
        var buffers = new Dictionary<int, StringBuilder>(); // text, or partial tool-input JSON, per block
        string? stopReason = null;
        var finished = false;
        var usage = TokenUsage.Empty;

        // Pings count as activity, so a silent connection (not a slow model) is what trips this.
        var idleTimeout = TimeSpan.FromSeconds(options.StreamIdleTimeoutSeconds);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(idleTimeout);

        try
        {
            await foreach (var item in SseParser.Create(stream).EnumerateAsync(idle.Token))
            {
                idle.CancelAfter(idleTimeout);
                if (string.IsNullOrWhiteSpace(item.Data) || JsonNode.Parse(item.Data) is not JsonNode evt) continue;

                switch (evt["type"]?.GetValue<string>())
                {
                    case "message_start":
                        usage = MergeUsage(usage, evt["message"]?["usage"]);
                        break;
                    case "content_block_start":
                    {
                        var index = evt["index"]!.GetValue<int>();
                        var block = evt["content_block"]!.DeepClone().AsObject();
                        blocks[index] = block;
                        buffers[index] = new StringBuilder();
                        var initial = block["type"]?.GetValue<string>() == "text" ? block["text"]?.GetValue<string>() : null;
                        if (!string.IsNullOrEmpty(initial))
                        {
                            buffers[index].Append(initial);
                            await progress.OnTextAsync(initial, ct);
                        }
                        break;
                    }
                    case "content_block_delta":
                    {
                        var index = evt["index"]!.GetValue<int>();
                        var delta = evt["delta"]!;
                        switch (delta["type"]?.GetValue<string>())
                        {
                            case "text_delta":
                                var piece = delta["text"]?.GetValue<string>() ?? "";
                                buffers[index].Append(piece);
                                if (piece.Length > 0) await progress.OnTextAsync(piece, ct);
                                break;
                            case "input_json_delta":
                                buffers[index].Append(delta["partial_json"]?.GetValue<string>());
                                break;
                            case "citations_delta" when delta["citation"] is JsonNode citation:
                                var citations = blocks[index]["citations"] as JsonArray ?? [];
                                citations.Add(citation.DeepClone());
                                blocks[index]["citations"] = citations;
                                break;
                        }
                        break;
                    }
                    case "content_block_stop":
                        FinishBlock(blocks[evt["index"]!.GetValue<int>()], buffers[evt["index"]!.GetValue<int>()]);
                        break;
                    case "message_delta":
                        stopReason = evt["delta"]?["stop_reason"]?.GetValue<string>() ?? stopReason;
                        usage = MergeUsage(usage, evt["usage"]);
                        break;
                    case "message_stop":
                        finished = true;
                        break;
                    case "error":
                        throw new HttpRequestException(
                            $"Anthropic stream error ({evt["error"]?["type"]}): {evt["error"]?["message"]}");
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No data from Anthropic for {idleTimeout.TotalSeconds:0}s while streaming.");
        }

        if (!finished) throw new HttpRequestException("Anthropic stream ended before the message completed.");

        foreach (var (index, block) in blocks) FinishBlock(block, buffers[index]);
        return (new JsonArray(blocks.Values.Select(b => (JsonNode?)b).ToArray()), stopReason, usage);
    }

    private static TokenUsage ReadUsage(JsonNode? usage) => new(
        GetInt(usage, "input_tokens") ?? 0,
        GetInt(usage, "output_tokens") ?? 0,
        GetInt(usage, "cache_creation_input_tokens") ?? 0,
        GetInt(usage, "cache_read_input_tokens") ?? 0);

    private static TokenUsage MergeUsage(TokenUsage current, JsonNode? usage) => new(
        GetInt(usage, "input_tokens") ?? current.Input,
        GetInt(usage, "output_tokens") ?? current.Output,
        GetInt(usage, "cache_creation_input_tokens") ?? current.CacheWrite,
        GetInt(usage, "cache_read_input_tokens") ?? current.CacheRead);

    private static int? GetInt(JsonNode? node, string property) =>
        node?[property] is JsonValue value && value.TryGetValue<int>(out var result) ? result : null;

    private static void FinishBlock(JsonObject block, StringBuilder buffer)
    {
        if (buffer.Length == 0) return;
        switch (block["type"]?.GetValue<string>())
        {
            case "text":
                block["text"] = buffer.ToString();
                break;
            case "server_tool_use" or "tool_use":
                block["input"] = JsonNode.Parse(buffer.ToString());
                break;
        }
        buffer.Clear();
    }
}
