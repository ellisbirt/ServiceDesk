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

    /// <summary>Each retrieved article is wrapped separately: its content was written by people and is data, not instructions.</summary>
    public static string KnowledgeBase(IReadOnlyList<KbHit>? hits) =>
        hits is not { Count: > 0 }
            ? ""
            : string.Join("\n", hits.Select(h => Wrap("kb_article", $"{h.Number}: {h.Title}\n{h.Excerpt}"))) + "\n\n";
}

public sealed class AnthropicGuardrailRouterService(
    IChatProvider client,
    IOptions<AnthropicOptions> options,
    ILogger<AnthropicGuardrailRouterService> logger) : IGuardrailRouterService
{
    private readonly AnthropicOptions _options = options.Value;
    private static readonly JsonObject ClassifierSchema = new()
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
            ["reasoning"] = new JsonObject { ["type"] = "string" },
            ["complexity"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("simple", "standard", "complex")
            }
        },
        ["required"] = new JsonArray("lane", "reasoning", "complexity"),
        ["additionalProperties"] = false
    };

    /// <summary>The classifier is short, structured and routine work, so it runs on the Fast tier.</summary>
    private const ModelTier ClassifierTier = ModelTier.Fast;

    public async Task<Classification> ClassifyAndValidateAsync(
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
            return new Classification(ActionLanes.PolicyViolation, Prompts.ComplianceFallback);
        }

        var (lane, complexity) = await ClassifyAsync(userInput, history, progress, cancellationToken, userAgent);
        logger.LogInformation(
            "Guardrail classified message as {Lane} ({Complexity}) for Tenant: {TenantId}", lane, complexity, tenantId);

        if (lane != ActionLanes.PolicyViolation)
        {
            return new Classification(lane, string.Empty, complexity);
        }

        return new Classification(lane, await CoachAsync(userInput, cancellationToken, progress, userAgent), complexity);
    }

    private const int CoachAttempts = 2;

    public async Task<string> CoachAsync(
        string userInput, CancellationToken cancellationToken = default, IChatProgress? progress = null, string? userAgent = null)
    {
        // Refusals are intermittent (the same request is often answered on the next attempt), so try once more.
        for (var attempt = 1; attempt <= CoachAttempts; attempt++)
        {
            try
            {
                var coaching = await client.CompleteAsync(
                    new ChatRequest(
                        Prompts.ComplianceCoach, PromptInput.WithUserAgent(PromptInput.Wrap("user_query", userInput), userAgent),
                        _options.CoachTemperature, _options.InferenceMaxTokens),
                    progress, cancellationToken);
                return coaching.Trim();
            }
            catch (LlmRefusalException)
            {
                logger.LogWarning("Compliance coach call was refused (attempt {Attempt} of {Attempts}).", attempt, CoachAttempts);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The block itself is already decided; a coach failure must not weaken it.
                logger.LogError(ex, "Compliance coach call failed; using static remediation text.");
                return Prompts.ComplianceFallback;
            }
        }

        // Still declined: the static text explains the right way to go about it.
        return Prompts.ComplianceFallback;
    }

    private async Task<(string Lane, RequestComplexity Complexity)> ClassifyAsync(
        string userInput, IReadOnlyList<ChatHistoryMessage> conversationHistory, IChatProgress? progress,
        CancellationToken ct, string? userAgent)
    {
        var raw = await RequestClassificationAsync(userInput, conversationHistory, progress, ct, userAgent, ClassifierTier);
        // A model that will not even classify the request is treated as having blocked it.
        if (raw is null) return (ActionLanes.PolicyViolation, RequestComplexity.Standard);

        try
        {
            return ParseClassification(raw);
        }
        catch (GuardrailClassificationException) when (ClassifierTier != ModelTier.Standard)
        {
            // The small model's answer was unusable: ask the standard model once before failing closed.
            logger.LogWarning("The fast classifier returned unusable output; retrying on the standard model.");
            raw = await RequestClassificationAsync(userInput, conversationHistory, progress, ct, userAgent, ModelTier.Standard);
            return raw is null ? (ActionLanes.PolicyViolation, RequestComplexity.Standard) : ParseClassification(raw);
        }
    }

    /// <summary>The model's raw reply, or null when it refused to classify.</summary>
    private async Task<string?> RequestClassificationAsync(
        string userInput, IReadOnlyList<ChatHistoryMessage> conversationHistory, IChatProgress? progress,
        CancellationToken ct, string? userAgent, ModelTier tier)
    {
        try
        {
            return await client.CompleteAsync(
                new ChatRequest(
                    Prompts.GuardrailRouter,
                    PromptInput.WithUserAgent(
                        $"{PromptInput.Wrap("conversation_history", JsonSerializer.Serialize(conversationHistory))}\n\n{PromptInput.Wrap("user_query", userInput)}",
                        userAgent),
                    0.0, _options.RouterMaxTokens, ResponseSchema: ClassifierSchema) { Tier = tier },
                progress, ct);
        }
        catch (LlmRefusalException)
        {
            Telemetry.PrefilterBlocks.Add(1, new KeyValuePair<string, object?>("rule", "model-refusal"));
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new GuardrailClassificationException("Classification service call failed.", ex);
        }
    }

    public static string ParseLane(string raw) => ParseClassification(raw).Lane;

    /// <summary>
    /// Strictly parses the classifier's reply: a known lane, a reasoning string and, when given, a known complexity.
    /// Anything else, including extra properties, fails closed.
    /// </summary>
    public static (string Lane, RequestComplexity Complexity) ParseClassification(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Any(p => p.Name is not ("lane" or "reasoning" or "complexity"))
                || !root.TryGetProperty("lane", out var laneElement)
                || !root.TryGetProperty("reasoning", out var reasoningElement)
                || laneElement.ValueKind != JsonValueKind.String
                || reasoningElement.ValueKind != JsonValueKind.String)
            {
                throw new GuardrailClassificationException("Classifier returned output that does not match the required schema.");
            }

            var complexity = RequestComplexity.Standard;
            if (root.TryGetProperty("complexity", out var complexityElement))
            {
                complexity = complexityElement.ValueKind == JsonValueKind.String
                    ? complexityElement.GetString() switch
                    {
                        "simple" => RequestComplexity.Simple,
                        "standard" => RequestComplexity.Standard,
                        "complex" => RequestComplexity.Complex,
                        _ => throw new GuardrailClassificationException("Classifier returned an unknown complexity.")
                    }
                    : throw new GuardrailClassificationException("Classifier returned output that does not match the required schema.");
            }

            var lane = laneElement.GetString();
            if (lane is not null && ActionLanes.All.Contains(lane))
            {
                return (lane, complexity);
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
    IChatProvider client, IOptions<AnthropicOptions> options, IOptions<SearchOptions> searchOptions) : IPublicInferenceService
{
    private readonly AnthropicOptions _options = options.Value;
    // Native search needs a provider with server-side tools (Anthropic); validation reports a mismatch at startup.
    private readonly bool _nativeSearch = SearchProviderCatalog.IsNativeClaude(searchOptions.Value.Provider)
        && client.Capabilities.HasFlag(LlmCapabilities.ServerTools);

    public Task<string> ExecutePublicInferenceAsync(
        string userInput, CancellationToken cancellationToken = default, IChatProgress? progress = null,
        string? userAgent = null, IReadOnlyList<ChatHistoryMessage>? conversationHistory = null,
        IReadOnlyList<KbHit>? knowledgeBase = null, ModelTier tier = ModelTier.Standard, AttachmentContext? attachments = null)
    {
        var (files, parts) = AttachmentPrompt.For(client, attachments);
        return ReplyAsync(AttachmentPrompt.System(Prompts.WithKnowledgeBase(Prompts.PublicTech, knowledgeBase), attachments),
            PromptInput.WithUserAgent(
                $"{PromptInput.KnowledgeBase(knowledgeBase)}{files}{(conversationHistory is { Count: > 0 } ? PromptInput.Wrap("conversation_history", JsonSerializer.Serialize(conversationHistory)) + "\n\n" : "")}{PromptInput.Wrap("user_query", userInput)}",
                userAgent),
            tools: null, progress, tier, parts, cancellationToken);
    }

    public Task<string> ExecuteSearchInferenceAsync(
        string userInput, string searchContextMarkdown, CancellationToken cancellationToken = default,
        IChatProgress? progress = null, string? userAgent = null, IReadOnlyList<KbHit>? knowledgeBase = null,
        ModelTier tier = ModelTier.Standard, AttachmentContext? attachments = null)
    {
        var (files, parts) = AttachmentPrompt.For(client, attachments);
        if (_nativeSearch)
        {
            // Claude performs the search itself via the server-side web search tool.
            var tools = new JsonArray(new JsonObject
            {
                ["type"] = _options.WebSearchToolType,
                ["name"] = "web_search",
                ["max_uses"] = _options.WebSearchMaxUses
            });
            return ReplyAsync(AttachmentPrompt.System(Prompts.WithKnowledgeBase(Prompts.PublicSearchNative, knowledgeBase), attachments),
                PromptInput.WithUserAgent($"{PromptInput.KnowledgeBase(knowledgeBase)}{files}{PromptInput.Wrap("user_query", userInput)}", userAgent),
                tools, progress, tier, parts, cancellationToken);
        }

        return ReplyAsync(
            AttachmentPrompt.System(Prompts.WithKnowledgeBase(Prompts.PublicSearch, knowledgeBase), attachments),
            PromptInput.WithUserAgent(
                $"{PromptInput.KnowledgeBase(knowledgeBase)}{files}{PromptInput.Wrap("search_results", searchContextMarkdown)}\n\n{PromptInput.Wrap("user_query", userInput)}",
                userAgent),
            tools: null, progress, tier, parts, cancellationToken);
    }

    /// <summary>Streams the reply when a progress sink is supplied, otherwise returns it in one response.</summary>
    // A refusal (LlmRefusalException) is left to the pipeline, which answers with educational guidance instead.
    private Task<string> ReplyAsync(
        string system, string user, JsonArray? tools, IChatProgress? progress, ModelTier tier, IReadOnlyList<ChatContentPart>? parts,
        CancellationToken ct) =>
        progress is null
            ? client.CompleteAsync(Request(system, user, tools, tier, parts), cancellationToken: ct)
            : client.StreamAsync(Request(system, user, tools, tier, parts), progress, ct);

    private ChatRequest Request(string system, string user, JsonArray? tools, ModelTier tier, IReadOnlyList<ChatContentPart>? parts) =>
        new(system, user, _options.InferenceTemperature, _options.InferenceMaxTokens, Tools: tools) { Tier = tier, Parts = parts };
}
