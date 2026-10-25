using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Core;
using Helpdesk.Infrastructure.Search;
using Helpdesk.Core.Interfaces;
using Helpdesk.Shared.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.Ai;

public sealed class PublicInferenceService(
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
