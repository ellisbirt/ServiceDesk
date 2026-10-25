using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Helpdesk.Core;
using Helpdesk.Core.Interfaces;
using Helpdesk.Infrastructure.Ai;
using Helpdesk.Infrastructure.Kb;
using Helpdesk.Llm;
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
    IAttachmentService attachmentService,
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

        var attachments = await ResolveAttachmentsAsync(original, tenantId, userId, ct);
        var response = await RouteAsync(original, attachments, tenantId, userId, progress, ct, userAgent);

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

    /// <summary>
    /// The files this request refers to. A reference counts only if its token is genuine for this tenant, user and
    /// conversation; the file's real details come from storage, never from the reference.
    /// </summary>
    private async Task<ResolvedAttachments> ResolveAttachmentsAsync(
        ChatMessageRequest original, string tenantId, string userId, CancellationToken ct)
    {
        if (!attachmentService.Enabled) return ResolvedAttachments.None;

        var submitted = original.Attachments ?? [];
        var current = AttachmentTokens.ValidIds(signer, tenantId, userId, original.ConversationId, submitted);
        if (current.Count < submitted.Select(r => r.Id).Distinct().Count())
            logger.LogWarning("Ignored attachment reference(s) with an invalid token for Tenant: {TenantId}", tenantId);
        if (current.Count > attachmentService.Limits.MaxFilesPerMessage)
            throw new AttachmentException("too_many_files", $"A message can have at most {attachmentService.Limits.MaxFilesPerMessage} files.");

        var earlier = AttachmentTokens.ValidIds(
            signer, tenantId, userId, original.ConversationId, original.ConversationHistory.SelectMany(m => m.Attachments ?? []));
        return await attachmentService.ResolveAsync(tenantId, userId, original.ConversationId, current, earlier, ct);
    }

    /// <summary>What the classifier is told about files, so "see the attached log" is read in context.</summary>
    private static string ClassifierInput(string messageText, ResolvedAttachments attachments) =>
        attachments.Current.Count == 0
            ? messageText
            : $"{messageText}\n\n[The user attached: {string.Join(", ", attachments.Current.Select(a => $"{Brief(a.FileName)} ({a.Kind})"))}]";

    private static string Brief(string name) => new string(name.Where(c => !char.IsControl(c)).Take(60).ToArray());

    private async Task<HelpdeskApiResponse> RouteAsync(
        ChatMessageRequest original, ResolvedAttachments attachments, string tenantId, string userId, IChatProgress? progress,
        CancellationToken ct, string? userAgent)
    {
        // Everything downstream (model, web search, ticket queue) only ever sees the redacted request.
        var request = Redact(original, tenantId);

        var classification = await guardrailService.ClassifyAndValidateAsync(
            ClassifierInput(request.MessageText, attachments), tenantId, request.ConversationHistory, ct, progress, userAgent);
        var (lane, responseContext) = classification;
        Telemetry.Classifications.Add(1, new KeyValuePair<string, object?>("lane", lane));
        Activity.Current?.SetTag("helpdesk.lane", lane);

        HelpdeskApiResponse response;
        IReadOnlyList<KbHit> kbHits = [];
        try
        {
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
                var techTier = ChooseTier(lane, classification.Complexity, request, kbHits, searchContext: null, attachments.Model);
                response = Reply(request, ActionLanes.PublicTech,
                    await inferenceService.ExecutePublicInferenceAsync(
                        request.MessageText, ct, progress, userAgent, request.ConversationHistory, kbHits, techTier, attachments.Model));
                break;

            case ActionLanes.PublicSearch:
                // A third party (or the model's own web search) sees this text, so apply the stricter Search policy.
                var searchText = Sanitize(request.MessageText, EgressDestination.Search, tenantId);
                var searchMarkdown = await searchService.GetLiveSearchResultsAsync(searchText, ct);
                // The KB is internal, so it sees the Model-redacted conversation, not the Search-sanitised copy.
                kbHits = await RetrieveKnowledgeAsync(request, tenantId, ct);
                var searchTier = ChooseTier(lane, classification.Complexity, request, kbHits, searchMarkdown, attachments.Model);
                response = Reply(request, ActionLanes.PublicSearch,
                    await inferenceService.ExecuteSearchInferenceAsync(
                        searchText, searchMarkdown, ct, progress, userAgent, kbHits, searchTier, attachments.Model));
                break;

            case ActionLanes.PrivateCorp:
                // The model leads the hand-off: it asks for whatever is missing, and raises the ticket itself once
                // it has enough. Nothing is queued before it decides.
                kbHits = await RetrieveKnowledgeAsync(request, tenantId, ct);
                response = await HandOffAsync(original, request, attachments, tenantId, userId, ct, userAgent, kbHits);
                break;

            default:
                logger.LogError("Guardrail returned unsupported lane {Lane}", lane);
                throw new UnsupportedLaneException(lane);
        }
        }
        catch (LlmRefusalException)
        {
            // The model declined this request. The user still gets educational guidance on the right way to go about
            // it (a reframed request, then static text), and nothing is queued.
            logger.LogWarning("The model declined a {Lane} request for Tenant: {TenantId}; giving guidance instead", lane, tenantId);
            kbHits = [];
            response = Reply(request, ActionLanes.PolicyViolation,
                await guardrailService.CoachAsync(request.MessageText, ct, progress, userAgent));
        }

        if (kbHits.Count > 0) response.Metadata[KnowledgeBaseMetadataKey] = string.Join(",", kbHits.Select(h => h.Number));
        return response;
    }

    /// <summary>
    /// Picks the model tier for an answer from the question's size and the classifier's complexity rating, and records it.
    /// The size counts everything the model will read: the message, the history, KB articles and search results.
    /// </summary>
    private ModelTier ChooseTier(
        string lane, RequestComplexity complexity, ChatMessageRequest request, IReadOnlyList<KbHit> kbHits, string? searchContext,
        AttachmentContext files)
    {
        var tokens = files.EstimatedTokens + InferenceRoutePolicy.EstimateTokens(
            request.ConversationHistory.Select(m => m.Content)
                .Append(request.MessageText)
                .Concat(kbHits.Select(h => h.Title + h.Excerpt))
                .Append(searchContext));
        var userTurns = request.ConversationHistory.Count(m => m.Role == "user") + 1;
        var decision = InferenceRoutePolicy.Choose(new InferenceRouteContext(lane, complexity, tokens, userTurns));

        Telemetry.InferenceRoutes.Add(1,
            new KeyValuePair<string, object?>("lane", lane),
            new KeyValuePair<string, object?>("tier", decision.Tier.ToString()),
            new KeyValuePair<string, object?>("reason", decision.Reason));
        Activity.Current?.SetTag("helpdesk.inference.tier", decision.Tier.ToString());
        logger.LogInformation(
            "Routed a {Lane} question to the {Tier} tier ({Reason}; about {Tokens} input tokens, {Turns} user turn(s))",
            lane, decision.Tier, decision.Reason, tokens, userTurns);
        return decision.Tier;
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

    /// <summary>Response metadata key set to "true" on a reply that shows a ticket draft and waits for the user to confirm it.</summary>
    public const string TicketPendingMetadataKey = "ticketPending";

    /// <summary>
    /// Whether the user is answering a ticket draft. The server keeps no state, so this reads the history: the last
    /// message must be an assistant message that ends with the confirmation prompt. Only verified (signed) history gets
    /// here, so the user cannot fake it, and a draft the user has moved on from (a later turn followed it) no longer counts.
    /// </summary>
    internal static bool IsProposalPending(IReadOnlyList<ChatHistoryMessage> verifiedHistory) =>
        verifiedHistory is [.., { Role: "assistant" } last] && last.Content.TrimEnd().EndsWith(Prompts.TicketConfirmPrompt, StringComparison.Ordinal);

    private async Task<HelpdeskApiResponse> HandOffAsync(
        ChatMessageRequest original, ChatMessageRequest request, ResolvedAttachments attachments, string tenantId, string userId,
        CancellationToken ct, string? userAgent, IReadOnlyList<KbHit> kbHits)
    {
        // The model decides when it has enough; this only stops an interview that never ends.
        var userTurns = request.ConversationHistory.Count(m => m.Role == "user") + 1;
        var mustRaise = userTurns >= chatOptions.Value.MaxInterviewTurns;
        // The interviewer reads every attached file as a short excerpt: enough to make the ticket specific without paying for
        // the whole file on every turn. (Every file counts as "earlier" here, so none is sent natively.)
        var excerpts = attachments.All.Count == 0
            ? AttachmentContext.Empty
            : (await attachmentService.ResolveAsync(
                tenantId, userId, original.ConversationId, [], [.. attachments.All.Select(a => a.Id)], ct)).Model;
        var conversation = Transcript(request, attachments.All) + (excerpts.IsEmpty ? "" : "\n\nAttached files:\n" + excerpts.Text);
        // A draft is waiting for the user's answer, so this turn confirms, amends or drops it. It is not the turn to cut
        // the interview short.
        var proposalPending = IsProposalPending(original.ConversationHistory);
        var interview = await ticketInterviewer.InterviewAsync(conversation, ct, userAgent, mustRaise && !proposalPending, proposalPending);
        if (mustRaise && !proposalPending && !interview.RaiseTicket)
        {
            logger.LogWarning(
                "The interviewer kept asking after {Turns} user messages; proposing a minimal ticket for Tenant: {TenantId}",
                userTurns, tenantId);
            interview = MinimalTicket(request);
        }
        if (!interview.RaiseTicket)
        {
            logger.LogInformation("Gathering details for a service-desk ticket for Tenant: {TenantId}", tenantId);
            return Reply(request, ActionLanes.PrivateCorp, interview.Message + KnowledgeBaseReply.Articles(kbHits));
        }

        // The one gate before anything is queued: the user was shown a draft and has agreed to it. The model's own say-so
        // is not enough, and neither is a "confirmed" flag on a turn that did not follow a draft.
        if (!(proposalPending && interview.UserConfirmed))
        {
            logger.LogInformation("Proposed a service-desk ticket for confirmation for Tenant: {TenantId}", tenantId);
            var proposal = Reply(request, ActionLanes.PrivateCorp, ProposalText(interview, attachments.All));
            proposal.Metadata[TicketPendingMetadataKey] = "true";
            return proposal;
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
        if (attachments.All.Count > 0)
        {
            answers.Add(new TicketDetailAnswer
            {
                Label = "Attachments",
                Value = Clean(string.Join("\n", attachments.All.Select(a => $"{a.FileName} ({a.Kind}, {a.Size} bytes)")))
            });
        }
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
            Details = details,
            // The files already sit in the container shared with the ticket worker, which records them against the ticket.
            Attachments = attachments.All.Count == 0 ? null : [.. attachments.All.Select(a => new TicketAttachment
            {
                Id = a.Id,
                FileName = a.FileName,
                ContentType = a.ContentType,
                Size = a.Size,
                Sha256 = a.Sha256,
                BlobPath = a.BlobPath,
                ScanStatus = a.ScanStatus
            })]
        };
        await queuePublisher.PublishAsync(ticketEvent, ct);
        // Marked only after the ticket is queued, so a failed publish leaves the files to expire normally.
        await attachmentService.MarkRaisedAsync(
            tenantId, original.ConversationId, [.. attachments.All.Select(a => a.Id)], ticketEvent.EventId, ct);
        logger.LogInformation("Service-desk ticket queued after conversation for Tenant: {TenantId}", tenantId);

        var reply = Reply(request, ActionLanes.PrivateCorp, interview.Message + KnowledgeBaseReply.Articles(kbHits));
        reply.Metadata[TicketRaisedMetadataKey] = "true";
        reply.Metadata[TicketEventIdMetadataKey] = ticketEvent.EventId.ToString();
        return reply;
    }

    /// <summary>
    /// What the user is asked to approve: the model's lead-in and everything that goes to the operator (summary, conversation
    /// summary, suggested action, steps tried, files), then the confirmation prompt. The whole text is kept inside the history
    /// message limit, which is what the client sends it back under. The confirmation prompt is always last:
    /// <see cref="IsProposalPending"/> finds the draft by it.
    /// </summary>
    private static string ProposalText(TicketInterview draft, IReadOnlyList<AttachmentInfo> files)
    {
        static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

        var leadIn = Clip(draft.Message, 400);
        var details = new List<string> { $"**{Clip(draft.Title!, 120)}**", Clip(draft.Summary!, 1100) };
        if (draft.ConversationSummary is { } history) details.Add($"The conversation so far:\n{Clip(history, 1000)}");
        if (draft.RecommendedAction is { } action) details.Add($"Suggested action: {Clip(action, 500)}");
        if (draft.StepsTried is { } tried) details.Add($"Already tried: {Clip(tried, 300)}");
        if (files.Count > 0) details.Add("Attached: " + string.Join(", ", files.Select(f => Brief(f.FileName))));

        var quoted = string.Join("\n", string.Join("\n\n", details).Split('\n').Select(line => ("> " + line).TrimEnd()));
        var room = ChatRequestValidator.MaxMessageLength - leadIn.Length - Prompts.TicketConfirmPrompt.Length - 8;
        return $"{leadIn}\n\n{Clip(quoted, room)}\n\n{Prompts.TicketConfirmPrompt}";
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
            true, Prompts.TicketProposedWithoutInterview, TicketSubject.FromText(userMessages[0]),
            "The user needs help from the service desk. The assistant could not complete its questions, so details may be missing.",
            string.Join("\n", userMessages.TakeLast(MaxTranscriptMessages).Select(m => $"- {Brief(m)}")), null, null);
    }

    private static string Transcript(ChatMessageRequest request, IReadOnlyList<AttachmentInfo> files)
    {
        // Names come from storage, not from the references the client sent.
        string Attached(IReadOnlyList<AttachmentRef>? refs) =>
            refs is not { Count: > 0 }
                ? ""
                : " [attached: " + string.Join(", ", refs.Select(r => files.FirstOrDefault(f => f.Id == r.Id)).Where(f => f is not null)
                    .Select(f => Brief(f!.FileName))) + "]";

        return string.Join("\n", request.ConversationHistory.TakeLast(MaxTranscriptMessages)
            .Select(m => $"{m.Role}: {m.Content}{Attached(m.Attachments)}")
            .Append($"user: {request.MessageText}{Attached(request.Attachments)}"));
    }

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
