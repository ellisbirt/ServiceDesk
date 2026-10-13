using System.Diagnostics;
using Helpdesk.Core.Interfaces;
using Helpdesk.Shared.Contracts;
using Helpdesk.Shared.Events;

namespace Helpdesk.Api.Chat;

/// <summary>
/// Raises the ticket for a pending escalation: the original request plus the follow-up form's answers, redacted
/// with the Queue policy and queued as one event.
/// </summary>
public sealed class EscalationSubmitter(
    EscalationTokens tokens,
    ITicketQueuePublisher queuePublisher,
    ISensitiveDataRedactor redactor,
    ILogger<EscalationSubmitter> logger)
{
    public const int MaxAnswers = 20;
    public const int MaxTitleLength = 200;
    public const int MaxLabelLength = 200;
    public const int MaxValueLength = 4000;
    public const int MaxTokenLength = 16_000;

    public static Dictionary<string, string[]>? Validate(EscalationRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.ConversationId == Guid.Empty) errors["conversationId"] = ["A conversation id is required."];
        if (string.IsNullOrWhiteSpace(request.EscalationToken) || request.EscalationToken.Length > MaxTokenLength)
            errors["escalationToken"] = ["An escalation token is required."];
        if (string.IsNullOrWhiteSpace(request.FormTitle) || request.FormTitle.Length > MaxTitleLength)
            errors["formTitle"] = [$"A form title of up to {MaxTitleLength} characters is required."];

        var answers = request.Answers ?? [];
        if (answers.Count > MaxAnswers)
        {
            errors["answers"] = [$"At most {MaxAnswers} answers are allowed."];
        }
        else
        {
            for (var i = 0; i < answers.Count; i++)
            {
                var answer = answers[i];
                if (answer is null || string.IsNullOrWhiteSpace(answer.Label) || answer.Label.Length > MaxLabelLength)
                    errors[$"answers[{i}].label"] = [$"A label of up to {MaxLabelLength} characters is required."];
                if (answer?.Value is null || answer.Value.Length > MaxValueLength)
                    errors[$"answers[{i}].value"] = [$"A value of up to {MaxValueLength} characters is required."];
            }
        }
        return errors.Count == 0 ? null : errors;
    }

    /// <summary>Queues the ticket, or returns null if the token is not valid for this caller and conversation.</summary>
    public async Task<EscalationResponse?> SubmitAsync(
        EscalationRequest request, string tenantId, string userId, CancellationToken ct)
    {
        if (tokens.Redeem(tenantId, userId, request.ConversationId, request.EscalationToken) is not { } pending)
        {
            logger.LogWarning("Rejected an escalation with an invalid or expired token for Tenant: {TenantId}", tenantId);
            return null;
        }

        string Clean(string text) => redactor.Redact(text, EgressDestination.Queue, tenantId).Text;

        // Unanswered optional fields add nothing to the ticket.
        var answers = request.Answers
            .Where(a => !string.IsNullOrWhiteSpace(a.Value))
            .Select(a => new TicketDetailAnswer { Label = Clean(a.Label.Trim()), Value = Clean(a.Value.Trim()) })
            .ToList();
        var details = answers.Count == 0 ? null : new TicketDetails { Title = Clean(request.FormTitle.Trim()), Answers = answers };

        var ticketEvent = new ProcessPrivateCorpTicketEvent
        {
            EventId = pending.EventId,
            TenantId = tenantId,
            UserId = userId,
            ConversationId = request.ConversationId,
            CorrelationId = Activity.Current?.GetTagItem("helpdesk.correlation_id")?.ToString()
                ?? Activity.Current?.TraceId.ToString()
                ?? Guid.NewGuid().ToString("N"),
            SanitizedPayload = details is null ? pending.Payload : pending.Payload + "\n\n" + details.ToText(),
            Subject = TicketSubject.FromText(pending.Payload),
            Details = details
        };
        await queuePublisher.PublishAsync(ticketEvent, ct);
        logger.LogInformation(
            "Private infrastructure ticket queued with {Answers} form answer(s) for Tenant: {TenantId}", answers.Count, tenantId);
        return new EscalationResponse { TicketEventId = ticketEvent.EventId };
    }
}
