using System.Net;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Helpdesk.Core;
using Helpdesk.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.Ai;

public interface IAnthropicClient
{
    /// <param name="tools">Optional tool definitions (e.g. the server-side web search tool).</param>
    /// <param name="outputConfig">Optional Anthropic structured-output configuration.</param>
    /// <param name="progress">Optional; only told about rate-limit waits, the reply itself is returned whole.</param>
    Task<string> CompleteAsync(
        string systemPrompt, string userContent, double temperature, int maxTokens,
        JsonArray? tools = null, CancellationToken cancellationToken = default, JsonObject? outputConfig = null,
        IChatProgress? progress = null);

    /// <summary>
    /// Same as <see cref="CompleteAsync"/> but uses the streaming Messages API and forwards text to
    /// <paramref name="progress"/> as it arrives. The returned text equals the concatenated deltas.
    /// </summary>
    Task<string> StreamAsync(
        string systemPrompt, string userContent, double temperature, int maxTokens, IChatProgress progress,
        JsonArray? tools = null, CancellationToken cancellationToken = default);
}

public sealed class AnthropicClient(
    HttpClient http, IOptions<AnthropicOptions> options, ILogger<AnthropicClient> logger) : IAnthropicClient
{
    // A server-tool turn can pause mid-way; we resume it a bounded number of times.
    private const int MaxPauseContinuations = 3;

    private readonly AnthropicOptions _options = options.Value;
    private readonly record struct TokenUsage(int Input, int Output, int CacheWrite, int CacheRead)
    {
        public static TokenUsage Empty => new(0, 0, 0, 0);
        public TokenUsage Add(TokenUsage value) =>
            new(Input + value.Input, Output + value.Output, CacheWrite + value.CacheWrite, CacheRead + value.CacheRead);
    }

    public Task<string> CompleteAsync(
        string systemPrompt, string userContent, double temperature, int maxTokens,
        JsonArray? tools = null, CancellationToken cancellationToken = default, JsonObject? outputConfig = null,
        IChatProgress? progress = null) =>
        RunAsync(systemPrompt, userContent, temperature, maxTokens, tools, outputConfig, progress, stream: false, cancellationToken);

    public Task<string> StreamAsync(
        string systemPrompt, string userContent, double temperature, int maxTokens, IChatProgress progress,
        JsonArray? tools = null, CancellationToken cancellationToken = default) =>
        RunAsync(systemPrompt, userContent, temperature, maxTokens, tools, outputConfig: null, progress, stream: true, cancellationToken);

    private async Task<string> RunAsync(
        string systemPrompt, string userContent, double temperature, int maxTokens, JsonArray? tools,
        JsonObject? outputConfig, IChatProgress? progress, bool stream, CancellationToken ct)
    {
        var parentActivity = Activity.Current;
        var tenantId = FindTag(parentActivity, "helpdesk.tenant_id");
        var correlationId = FindTag(parentActivity, "helpdesk.correlation_id");
        using var activity = Telemetry.Source.StartActivity("anthropic.request");
        activity?.SetTag("gen_ai.system", "anthropic");
        activity?.SetTag("gen_ai.request.model", _options.Model);
        if (tenantId is not null) activity?.SetTag("helpdesk.tenant_id", tenantId);
        if (correlationId is not null) activity?.SetTag("helpdesk.correlation_id", correlationId);

        var messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = userContent });
        var text = new StringBuilder();
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var totalUsage = TokenUsage.Empty;
        var hasUsage = false;
        try
        {
            for (var turn = 0; turn <= MaxPauseContinuations; turn++)
            {
                var body = BuildBody(systemPrompt, messages, temperature, maxTokens, tools, outputConfig, stream);
                var (content, stopReason, usage) = stream
                    ? await StreamTurnAsync(body, progress!, ct)
                    : await SendTurnAsync(body, progress, ct);
                totalUsage = totalUsage.Add(usage);
                hasUsage = true;

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
            if (hasUsage) RecordUsage(totalUsage, tenantId, activity);
        }
    }

    private JsonObject BuildBody(
        string systemPrompt, JsonArray messages, double temperature, int maxTokens, JsonArray? tools,
        JsonObject? outputConfig, bool stream)
    {
        // The system prompt is sent as a cacheable prefix block so repeated chat turns reuse it.
        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["max_tokens"] = maxTokens,
            ["system"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = systemPrompt,
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" }
            }),
            ["messages"] = messages.DeepClone()
        };
        if (SupportsTemperature(_options.Model)) body["temperature"] = temperature;
        if (tools is not null) body["tools"] = tools.DeepClone();
        if (outputConfig is not null) body["output_config"] = outputConfig.DeepClone();
        if (stream) body["stream"] = true;
        return body;
    }

    private static bool SupportsTemperature(string model)
    {
        var version = Regex.Match(model, @"-(?<major>\d+)-(?<minor>\d+)(?:-|$)");
        if (!version.Success) return false;

        if (!int.TryParse(version.Groups["major"].Value, out var major) ||
            !int.TryParse(version.Groups["minor"].Value, out var minor))
            return false;

        // Claude 4.7 and newer reject sampling parameters; unknown aliases are kept safe too.
        return major < 4 || major == 4 && minor < 7;
    }

    private async Task<(JsonArray Content, string? StopReason, TokenUsage Usage)> SendTurnAsync(
        JsonObject body, IChatProgress? progress, CancellationToken ct)
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
        JsonObject body, IChatProgress progress, CancellationToken ct)
    {
        using var response = await PostAsync(body, stream: true, progress, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);

        var blocks = new SortedDictionary<int, JsonObject>();
        var buffers = new Dictionary<int, StringBuilder>(); // text, or partial tool-input JSON, per block
        string? stopReason = null;
        var finished = false;
        var usage = TokenUsage.Empty;

        // Pings count as activity, so a silent connection (not a slow model) is what trips this.
        var idleTimeout = TimeSpan.FromSeconds(_options.StreamIdleTimeoutSeconds);
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

    private void RecordUsage(TokenUsage usage, string? tenantId, Activity? activity)
    {
        Telemetry.AnthropicInputTokens.Add(usage.Input);
        Telemetry.AnthropicOutputTokens.Add(usage.Output);
        Telemetry.AnthropicCacheWriteTokens.Add(usage.CacheWrite);
        Telemetry.AnthropicCacheReadTokens.Add(usage.CacheRead);

        var cost = EstimateCost(usage);
        if (cost is { } estimatedCost)
        {
            Telemetry.AnthropicEstimatedCost.Record((double)estimatedCost);
            activity?.SetTag("gen_ai.usage.cost_usd", estimatedCost);
        }
        activity?.SetTag("gen_ai.usage.input_tokens", usage.Input + usage.CacheWrite + usage.CacheRead);
        activity?.SetTag("gen_ai.usage.output_tokens", usage.Output);
        logger.LogInformation(
            "Anthropic usage for Tenant: {TenantId}, Model: {Model}, InputTokens: {InputTokens}, CacheWriteTokens: {CacheWriteTokens}, CacheReadTokens: {CacheReadTokens}, OutputTokens: {OutputTokens}, EstimatedCostUsd: {EstimatedCostUsd}",
            tenantId ?? "unknown", _options.Model, usage.Input, usage.CacheWrite, usage.CacheRead, usage.Output, cost);
    }

    private decimal? EstimateCost(TokenUsage usage)
    {
        if (_options.InputCostPerMillionUsd is not { } input
            || _options.CacheWriteCostPerMillionUsd is not { } cacheWrite
            || _options.CacheReadCostPerMillionUsd is not { } cacheRead
            || _options.OutputCostPerMillionUsd is not { } output)
        {
            return null;
        }

        return (usage.Input * input + usage.CacheWrite * cacheWrite
            + usage.CacheRead * cacheRead + usage.Output * output) / 1_000_000m;
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

    private static string? FindTag(Activity? activity, string key)
    {
        for (var current = activity; current is not null; current = current.Parent)
        {
            if (current.GetTagItem(key) is { } value) return value.ToString();
        }
        return null;
    }

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

    /// <summary>
    /// POSTs the request, honouring the <c>Retry-After</c> header of HTTP 429 responses. The wait can far exceed
    /// a normal HTTP timeout, which is why callers on the hub path report it through <paramref name="progress"/>.
    /// The standard resilience handler is configured not to retry 429, so this loop owns it.
    /// </summary>
    private async Task<HttpResponseMessage> PostAsync(
        JsonObject body, bool stream, IChatProgress? progress, CancellationToken ct)
    {
        var maxWait = TimeSpan.FromSeconds(_options.RateLimitMaxWaitSeconds);

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/messages") { Content = JsonContent.Create(body) };
            var response = await http.SendAsync(
                request, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct);

            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                try
                {
                    response.EnsureSuccessStatusCode();
                    return response;
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }

            var retryAfter = ReadRetryAfter(response);
            response.Dispose();
            Telemetry.UpstreamRateLimited.Add(1);

            var wait = retryAfter ?? TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 30));
            if (attempt > _options.RateLimitMaxRetries || wait > maxWait)
            {
                throw new UpstreamRateLimitedException(retryAfter,
                    $"Anthropic rate limit: retry after {(retryAfter ?? wait).TotalSeconds:0}s, giving up after {attempt} attempt(s).");
            }

            // Jitter only on a real wait so many queued jobs do not all resume in the same instant.
            if (wait > TimeSpan.Zero) wait += TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
            Telemetry.UpstreamRateLimitWait.Record(wait.TotalSeconds);
            if (progress is not null) await progress.OnRateLimitedAsync(wait, attempt, ct);
            await Task.Delay(wait, ct);
        }
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header?.Date is { } date)
        {
            var until = date - DateTimeOffset.UtcNow;
            return until < TimeSpan.Zero ? TimeSpan.Zero : until;
        }
        return null;
    }
}
