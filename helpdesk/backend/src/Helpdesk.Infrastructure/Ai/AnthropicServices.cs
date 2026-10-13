using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Core;
using Helpdesk.Infrastructure.Search;
using Helpdesk.Core.Interfaces;
using Helpdesk.Shared.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.Ai;

internal static class PromptInput
{
    // The unpredictable boundary token prevents untrusted content from forging its enclosing delimiters.
    public static string Wrap(string tag, string text)
    {
        var boundary = Guid.NewGuid().ToString("N");
        return $"[BEGIN UNTRUSTED {tag} {boundary}]\n{text}\n[END UNTRUSTED {tag} {boundary}]";
    }

    public static string WithUserAgent(string content, string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return content;

        var safeUserAgent = new string(userAgent
            .Where(character => !char.IsControl(character))
            .Take(512)
            .ToArray());
        return $"{content}\n\n{Wrap("client_context", $"User-Agent: {safeUserAgent}")}";
    }
}

public sealed class AnthropicGuardrailRouterService(
    IAnthropicClient client,
    IOptions<AnthropicOptions> options,
    ILogger<AnthropicGuardrailRouterService> logger) : IGuardrailRouterService
{
    private readonly AnthropicOptions _options = options.Value;
    private static readonly JsonObject ClassifierOutputConfig = new()
    {
        ["format"] = new JsonObject
        {
            ["type"] = "json_schema",
            ["schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["lane"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray(
                            ActionLanes.PolicyViolation,
                            ActionLanes.PublicTech,
                            ActionLanes.PublicSearch,
                            ActionLanes.PrivateCorp,
                            ActionLanes.NoMatch)
                    },
                    ["reasoning"] = new JsonObject { ["type"] = "string" }
                },
                ["required"] = new JsonArray("lane", "reasoning"),
                ["additionalProperties"] = false
            }
        }
    };

    public async Task<(string Lane, string ResponseContext)> ClassifyAndValidateAsync(
        string userInput, string tenantId, IReadOnlyList<ChatHistoryMessage>? conversationHistory = null,
        CancellationToken cancellationToken = default, IChatProgress? progress = null, string? userAgent = null)
    {
        var history = conversationHistory ?? [];
        if (GuardrailPolicyPrefilter.FindPolicyRule(userInput) is { } rule)
        {
            Telemetry.PrefilterBlocks.Add(1, new KeyValuePair<string, object?>("rule", rule));
            logger.LogWarning(
                "Policy pre-filter blocked request with rule {Rule} for Tenant: {TenantId}",
                rule, tenantId);
            return (ActionLanes.PolicyViolation, Prompts.ComplianceFallback);
        }

        var lane = await ClassifyAsync(userInput, history, progress, cancellationToken, userAgent);
        logger.LogInformation("Guardrail classified message as {Lane} for Tenant: {TenantId}", lane, tenantId);

        if (lane != ActionLanes.PolicyViolation)
        {
            return (lane, string.Empty);
        }

        try
        {
            var coaching = await client.CompleteAsync(
                Prompts.ComplianceCoach, PromptInput.WithUserAgent(PromptInput.Wrap("user_query", userInput), userAgent),
                _options.CoachTemperature, _options.InferenceMaxTokens, cancellationToken: cancellationToken, progress: progress);
            return (lane, coaching.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The block itself is already decided; a coach failure must not weaken it.
            logger.LogError(ex, "Compliance coach call failed; using static remediation text.");
            return (lane, Prompts.ComplianceFallback);
        }
    }

    private async Task<string> ClassifyAsync(
        string userInput, IReadOnlyList<ChatHistoryMessage> conversationHistory, IChatProgress? progress,
        CancellationToken ct, string? userAgent)
    {
        string raw;
        try
        {
            raw = await client.CompleteAsync(
                Prompts.GuardrailRouter,
                PromptInput.WithUserAgent(
                    $"{PromptInput.Wrap("conversation_history", JsonSerializer.Serialize(conversationHistory))}\n\n{PromptInput.Wrap("user_query", userInput)}",
                    userAgent),
                temperature: 0.0, _options.RouterMaxTokens,
                cancellationToken: ct, outputConfig: ClassifierOutputConfig, progress: progress);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new GuardrailClassificationException("Classification service call failed.", ex);
        }

        return ParseLane(raw);
    }

    public static string ParseLane(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty("lane", out var laneElement)
                || !root.TryGetProperty("reasoning", out var reasoningElement)
                || laneElement.ValueKind != JsonValueKind.String
                || reasoningElement.ValueKind != JsonValueKind.String)
            {
                throw new GuardrailClassificationException("Classifier returned output that does not match the required schema.");
            }

            var lane = laneElement.GetString();
            if (lane is not null && ActionLanes.All.Contains(lane))
            {
                return lane;
            }
            throw new GuardrailClassificationException("Classifier returned an unknown lane.");
        }
        catch (JsonException ex)
        {
            throw new GuardrailClassificationException("Classifier returned malformed JSON instead of structured output.", ex);
        }
    }
}

public sealed class AnthropicPublicInferenceService(
    IAnthropicClient client, IOptions<AnthropicOptions> options, IOptions<SearchOptions> searchOptions) : IPublicInferenceService
{
    private readonly AnthropicOptions _options = options.Value;
    private readonly bool _nativeSearch = SearchProviderCatalog.IsNativeClaude(searchOptions.Value.Provider);

    public Task<string> ExecutePublicInferenceAsync(
        string userInput, CancellationToken cancellationToken = default, IChatProgress? progress = null,
        string? userAgent = null) =>
        ReplyAsync(Prompts.PublicTech,
            PromptInput.WithUserAgent(PromptInput.Wrap("user_query", userInput), userAgent),
            tools: null, progress, cancellationToken);

    public Task<string> ExecuteSearchInferenceAsync(
        string userInput, string searchContextMarkdown, CancellationToken cancellationToken = default,
        IChatProgress? progress = null, string? userAgent = null)
    {
        if (_nativeSearch)
        {
            // Claude performs the search itself via the server-side web search tool.
            var tools = new JsonArray(new JsonObject
            {
                ["type"] = _options.WebSearchToolType,
                ["name"] = "web_search",
                ["max_uses"] = _options.WebSearchMaxUses
            });
            return ReplyAsync(Prompts.PublicSearchNative,
                PromptInput.WithUserAgent(PromptInput.Wrap("user_query", userInput), userAgent),
                tools, progress, cancellationToken);
        }

        return ReplyAsync(
            Prompts.PublicSearch,
            PromptInput.WithUserAgent(
                $"{PromptInput.Wrap("search_results", searchContextMarkdown)}\n\n{PromptInput.Wrap("user_query", userInput)}",
                userAgent),
            tools: null, progress, cancellationToken);
    }

    /// <summary>Streams the reply when a progress sink is supplied, otherwise returns it in one response.</summary>
    private Task<string> ReplyAsync(
        string system, string user, JsonArray? tools, IChatProgress? progress, CancellationToken ct) =>
        progress is null
            ? client.CompleteAsync(system, user, _options.InferenceTemperature, _options.InferenceMaxTokens, tools, ct)
            : client.StreamAsync(system, user, _options.InferenceTemperature, _options.InferenceMaxTokens, progress, tools, ct);
}
