using System.Diagnostics;
using Helpdesk.Core;
using Helpdesk.Core.Interfaces;
using Helpdesk.Infrastructure.Ai;
using Helpdesk.Infrastructure.Kb;
using Helpdesk.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace Helpdesk.Api.Chat;

/// <summary>The classifier returned a lane the API does not know how to process.</summary>
public sealed class UnsupportedLaneException(string lane) : Exception($"Unsupported lane '{lane}'.");

/// <summary>
/// Classify, then route. Shared by the synchronous REST endpoint and the SignalR job processor so both enforce
/// the same fail-closed ordering: nothing downstream (inference, search, queueing) runs before classification succeeds.
/// </summary>
public sealed class ChatPipeline(
    IGuardrailRouterService guardrailService,
    IPublicInferenceService inferenceService,
    IIntakeFormGenerator intakeFormGenerator,
    ILiveSearchService searchService,
    ISensitiveDataRedactor redactor,
    IConversationSigner signer,
    EscalationTokens escalationTokens,
    IKnowledgeBaseSearch knowledgeBase,
    IOptions<KbOptions> kbOptions,
    ILogger<ChatPipeline> logger)
{
    public async Task<HelpdeskApiResponse> RunAsync(
        ChatMessageRequest submitted, string tenantId, string userId, IChatProgress? progress, CancellationToken ct,
        string? userAgent = null)
    {
        // The server keeps no conversation state: the client sends the transcript back. Only the part of it that
        // carries valid signatures is trusted (so an "assistant" turn can never be forged), and that check is
        // made on the text as sent, before redaction changes it.
        var verified = signer.Verify(tenantId, userId, submitted.ConversationId, submitted.ConversationHistory);
        ReportDropped(verified, tenantId);
        var original = submitted with { ConversationHistory = verified.Messages };

        var response = await RouteAsync(original, tenantId, userId, progress, ct, userAgent);

        // Sign the finished turn so the client can send it back as trusted history next time.
        var (userSignature, assistantSignature) = signer.SignTurn(
            tenantId, userId, original.ConversationId, verified.Messages, original.MessageText, response.ResponseText);
        return response with { UserSignature = userSignature, AssistantSignature = assistantSignature };
    }

    private void ReportDropped(VerifiedHistory verified, string tenantId)
    {
        if (verified.Dropped == 0) return;

        Telemetry.HistoryVerificationFailures.Add(1, new KeyValuePair<string, object?>("reason", verified.Reason ?? "unknown"));
        Telemetry.HistoryMessagesDropped.Add(verified.Dropped);
        logger.LogWarning(
            "Dropped {Dropped} conversation history message(s) that could not be verified ({Reason}) for Tenant: {TenantId}",
            verified.Dropped, verified.Reason, tenantId);
    }

    private async Task<HelpdeskApiResponse> RouteAsync(
        ChatMessageRequest original, string tenantId, string userId, IChatProgress? progress, CancellationToken ct,
        string? userAgent)
    {
        // Everything downstream (model, web search, ticket queue) only ever sees the redacted request.
        var request = Redact(original, tenantId);

        var (lane, responseContext) = await guardrailService.ClassifyAndValidateAsync(
            request.MessageText, tenantId, request.ConversationHistory, ct, progress, userAgent);
        Telemetry.Classifications.Add(1, new KeyValuePair<string, object?>("lane", lane));
        Activity.Current?.SetTag("helpdesk.lane", lane);

        HelpdeskApiResponse response;
        IReadOnlyList<KbHit> kbHits = [];
        switch (lane.ToUpperInvariant())
        {
            case ActionLanes.NoMatch:
                logger.LogInformation("Guardrail declined an out-of-scope or ambiguous message for Tenant: {TenantId}", tenantId);
                response = Reply(request, ActionLanes.NoMatch, Prompts.NoMatchFallback);
                break;

            case ActionLanes.PolicyViolation:
                logger.LogWarning("Security Guardrail Intercepted Policy Breach for Tenant: {TenantId}", tenantId);
                response = Reply(request, ActionLanes.PolicyViolation, responseContext);
                break;

            case ActionLanes.PublicTech:
                kbHits = await RetrieveKnowledgeAsync(request, tenantId, ct);
                response = Reply(request, ActionLanes.PublicTech,
                    await inferenceService.ExecutePublicInferenceAsync(
                        request.MessageText, ct, progress, userAgent, request.ConversationHistory, kbHits));
                break;

            case ActionLanes.PublicSearch:
                // A third party (or the model's own web search) sees this text, so apply the stricter Search policy.
                var searchText = Sanitize(request.MessageText, EgressDestination.Search, tenantId);
                var searchMarkdown = await searchService.GetLiveSearchResultsAsync(searchText, ct);
                // The KB is internal, so it sees the Model-redacted conversation, not the Search-sanitised copy.
                kbHits = await RetrieveKnowledgeAsync(request, tenantId, ct);
                response = Reply(request, ActionLanes.PublicSearch,
                    await inferenceService.ExecuteSearchInferenceAsync(searchText, searchMarkdown, ct, progress, userAgent, kbHits));
                break;

            case ActionLanes.PrivateCorp:
                // From the original text, not the model's copy: the queue has its own policy, which may keep
                // values (such as a contact number) that the model never needed.
                var queuePayload = Sanitize(original.MessageText, EgressDestination.Queue, tenantId);
                // Nothing is queued yet: the ticket is raised only once the follow-up form is submitted
                // (POST /api/v1/chat/escalate), with the request and the answers together.
                logger.LogInformation("Private infrastructure request awaiting follow-up form for Tenant: {TenantId}", tenantId);
                // Articles that may already solve it are offered next to the form; submitting the form is still the agreed path.
                kbHits = await RetrieveKnowledgeAsync(request, tenantId, ct);
                response = Reply(request, ActionLanes.PrivateCorp, Prompts.EscalationPending + KnowledgeBaseReply.Articles(kbHits)) with
                {
                    IntakeForm = await TryGenerateFormAsync(
                        FormContext(request), lane, tenantId, ct, userAgent)
                        ?? FallbackForm,
                    Metadata = new()
                    {
                        [EscalationTokenMetadataKey] = escalationTokens.Issue(tenantId, userId, request.ConversationId, queuePayload)
                    }
                };
                break;

            default:
                logger.LogError("Guardrail returned unsupported lane {Lane}", lane);
                throw new UnsupportedLaneException(lane);
        }

        if (kbHits.Count > 0) response.Metadata[KnowledgeBaseMetadataKey] = string.Join(",", kbHits.Select(h => h.Number));
        return response;
    }

    /// <summary>Response metadata key listing the numbers of the KB articles the reply was given, comma separated.</summary>
    public const string KnowledgeBaseMetadataKey = "kbArticles";

    /// <summary>
    /// Finds KB articles for the request. The earlier user messages are part of the query so a short follow-up such as
    /// "it's still not working" keeps its subject. Never fails the reply: <see cref="IKnowledgeBaseSearch"/> returns
    /// nothing when the KB is unavailable.
    /// </summary>
    private Task<IReadOnlyList<KbHit>> RetrieveKnowledgeAsync(ChatMessageRequest request, string tenantId, CancellationToken ct)
    {
        var earlier = request.ConversationHistory
            .Where(m => m.Role == "user")
            .TakeLast(Math.Max(0, kbOptions.Value.HistoryMessages))
            .Select(m => m.Content);
        return knowledgeBase.SearchAsync(tenantId, string.Join("\n", earlier.Append(request.MessageText)), ct);
    }

    /// <summary>Response metadata key holding the pending escalation's token, which the form submission must return.</summary>
    public const string EscalationTokenMetadataKey = "escalationToken";

    /// <summary>Used when no tailored form can be generated, so the request can still be sent.</summary>
    public static readonly IntakeForm FallbackForm = new()
    {
        Title = "Request details",
        SubmitLabel = "Send to the service desk",
        Fields =
        [
            new IntakeFormField
            {
                Id = "issue_details",
                Label = "What is the issue?",
                Description = "Describe what happened, when it started, who or what is affected, and any steps already tried.",
                InputType = "textarea"
            }
        ]
    };

    // A form failure falls back to a generic form rather than failing the reply.
    private async Task<IntakeForm?> TryGenerateFormAsync(
        string text, string lane, string tenantId, CancellationToken ct, string? userAgent)
    {
        try
        {
            return await intakeFormGenerator.GenerateAsync(text, lane, ct, userAgent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Follow-up form could not be generated for an escalated request for Tenant: {TenantId}", tenantId);
            return null;
        }
    }

    private static string FormContext(ChatMessageRequest request)
    {
        if (request.ConversationHistory.Count == 0) return request.MessageText;

        var context = string.Join("\n", request.ConversationHistory.TakeLast(6)
            .Select(message => $"{message.Role}: {message.Content}"));
        return $"{context}\nuser: {request.MessageText}";
    }

    private ChatMessageRequest Redact(ChatMessageRequest request, string tenantId)
    {
        var found = new Dictionary<string, int>();
        string Clean(string text) => Sanitize(text, EgressDestination.Model, tenantId, found);

        var redacted = request with
        {
            MessageText = Clean(request.MessageText),
            ConversationHistory = request.ConversationHistory.Select(m => m with { Content = Clean(m.Content) }).ToList()
        };
        LogRedactions(found, EgressDestination.Model, tenantId);
        return redacted;
    }

    /// <summary>
    /// Redacts text for one destination. With <paramref name="found"/> the counts are collected for the caller to
    /// log once; without it they are logged straight away.
    /// </summary>
    private string Sanitize(string text, EgressDestination destination, string tenantId, Dictionary<string, int>? found = null)
    {
        var result = redactor.Redact(text, destination, tenantId);
        if (result.Categories is null) return result.Text;

        var target = found ?? new Dictionary<string, int>();
        foreach (var (category, n) in result.Categories) target[category] = target.GetValueOrDefault(category) + n;
        if (found is null) LogRedactions(target, destination, tenantId);
        return result.Text;
    }

    // Counts by category only, never the values.
    private void LogRedactions(Dictionary<string, int> found, EgressDestination destination, string tenantId)
    {
        if (found.Count == 0) return;
        logger.LogInformation(
            "Redacted {Count} value(s) ({Categories}) before sending to {Destination} for Tenant: {TenantId}",
            found.Values.Sum(), string.Join(", ", found.Select(c => $"{c.Key}={c.Value}")), destination, tenantId);
    }

    private static HelpdeskApiResponse Reply(ChatMessageRequest request, string lane, string text) => new()
    {
        ConversationId = request.ConversationId,
        ActionLane = lane,
        ResponseText = text
    };
}
