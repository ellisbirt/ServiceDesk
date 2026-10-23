using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Helpdesk.Core;
using Helpdesk.Core.Interfaces;
using Helpdesk.Infrastructure.Ai;
using Helpdesk.Infrastructure.Kb;
using Helpdesk.Shared.Contracts;
using Helpdesk.Shared.Events;
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
    ITicketInterviewer ticketInterviewer,
    ITicketQueuePublisher queuePublisher,
    ILiveSearchService searchService,
    ISensitiveDataRedactor redactor,
    IConversationSigner signer,
    IKnowledgeBaseSearch knowledgeBase,
    IOptions<KbOptions> kbOptions,
    IOptions<ChatOptions> chatOptions,
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
                // The model leads the hand-off: it asks for whatever is missing, and raises the ticket itself once
                // it has enough. Nothing is queued before it decides.
                kbHits = await RetrieveKnowledgeAsync(request, tenantId, ct);
                response = await HandOffAsync(original, request, tenantId, userId, ct, userAgent, kbHits);
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

    /// <summary>Response metadata key set to "true" on the reply that raised the ticket.</summary>
    public const string TicketRaisedMetadataKey = "ticketRaised";

    /// <summary>Response metadata key holding the queued ticket event's id, set when a ticket was raised.</summary>
    public const string TicketEventIdMetadataKey = "ticketEventId";

    private async Task<HelpdeskApiResponse> HandOffAsync(
        ChatMessageRequest original, ChatMessageRequest request, string tenantId, string userId, CancellationToken ct,
        string? userAgent, IReadOnlyList<KbHit> kbHits)
    {
        // The model decides when it has enough; this only stops an interview that never ends.
        var userTurns = request.ConversationHistory.Count(m => m.Role == "user") + 1;
        var mustRaise = userTurns >= chatOptions.Value.MaxInterviewTurns;
        var interview = await ticketInterviewer.InterviewAsync(Transcript(request), ct, userAgent, mustRaise);
        if (mustRaise && !interview.RaiseTicket)
        {
            logger.LogWarning(
                "The interviewer kept asking after {Turns} user messages; raising a minimal ticket for Tenant: {TenantId}",
                userTurns, tenantId);
            interview = MinimalTicket(request);
        }
        if (!interview.RaiseTicket)
        {
            logger.LogInformation("Gathering details for a service-desk ticket for Tenant: {TenantId}", tenantId);
            return Reply(request, ActionLanes.PrivateCorp, interview.Message + KnowledgeBaseReply.Articles(kbHits));
        }

        string Clean(string text) => Sanitize(text, EgressDestination.Queue, tenantId);

        // The operator gets the model's summaries rather than the transcript. They are built from the Model-redacted
        // conversation and redacted again here for the queue, so a secret never reaches the ticket.
        var summary = Clean(interview.Summary!);
        var conversationSummary = interview.ConversationSummary is { } history ? Clean(history) : null;
        var answers = new List<TicketDetailAnswer> { new() { Label = "Summary", Value = summary } };
        if (conversationSummary is not null)
            answers.Add(new TicketDetailAnswer { Label = "Conversation so far", Value = conversationSummary });
        if (interview.RecommendedAction is { } action)
            answers.Add(new TicketDetailAnswer { Label = "Recommended action", Value = Clean(action) });
        if (interview.StepsTried is { } tried)
            answers.Add(new TicketDetailAnswer { Label = "Steps already tried", Value = Clean(tried) });
        var details = new TicketDetails { Title = Clean(interview.Title!), Answers = answers };

        var ticketEvent = new ProcessPrivateCorpTicketEvent
        {
            // Derived from the conversation position, so a retried request queues the same event rather than a second one.
            EventId = EventIdFor(tenantId, userId, original.ConversationId, original.ConversationHistory.Count),
            TenantId = tenantId,
            UserId = userId,
            ConversationId = original.ConversationId,
            CorrelationId = Activity.Current?.GetTagItem("helpdesk.correlation_id")?.ToString()
                ?? Activity.Current?.TraceId.ToString()
                ?? Guid.NewGuid().ToString("N"),
            SanitizedPayload = details.ToText(),
            Subject = TicketSubject.FromText(details.Title),
            Summary = summary,
            ConversationSummary = conversationSummary,
            Details = details
        };
        await queuePublisher.PublishAsync(ticketEvent, ct);
        logger.LogInformation("Service-desk ticket queued after conversation for Tenant: {TenantId}", tenantId);

        var reply = Reply(request, ActionLanes.PrivateCorp, interview.Message + KnowledgeBaseReply.Articles(kbHits));
        reply.Metadata[TicketRaisedMetadataKey] = "true";
        reply.Metadata[TicketEventIdMetadataKey] = ticketEvent.EventId.ToString();
        return reply;
    }

    private const int MaxTranscriptMessages = 12;

    /// <summary>
    /// Used only when the model ignores an instruction to raise the ticket. Built from the Model-redacted messages, so
    /// nothing is summarised: the operator gets the start of each message instead.
    /// </summary>
    private static TicketInterview MinimalTicket(ChatMessageRequest request)
    {
        var userMessages = request.ConversationHistory.Where(m => m.Role == "user").Select(m => m.Content)
            .Append(request.MessageText).ToList();
        static string Brief(string text) => text.Length <= 300 ? text : text[..300] + "…";
        return new TicketInterview(
            true, Prompts.TicketRaisedWithoutInterview, TicketSubject.FromText(userMessages[0]),
            "The user needs help from the service desk. The assistant could not complete its questions, so details may be missing.",
            string.Join("\n", userMessages.TakeLast(MaxTranscriptMessages).Select(m => $"- {Brief(m)}")), null, null);
    }

    private static string Transcript(ChatMessageRequest request) =>
        string.Join("\n", request.ConversationHistory.TakeLast(MaxTranscriptMessages)
            .Select(m => $"{m.Role}: {m.Content}")
            .Append($"user: {request.MessageText}"));

    private static Guid EventIdFor(string tenantId, string userId, Guid conversationId, int historyCount)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}\n{userId}\n{conversationId:N}\n{historyCount}"));
        return new Guid(hash.AsSpan(0, 16));
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
