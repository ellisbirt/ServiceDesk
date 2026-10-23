using Helpdesk.Shared.Contracts;
using Helpdesk.Shared.Events;

namespace Helpdesk.Core.Interfaces;

public interface ITenantProvider
{
    string GetCurrentTenantId();
    string GetCurrentUserId();
}

public interface IGuardrailRouterService : IGuardrailCoach
{
    /// <summary>
    /// Returns an action lane (POLICY_VIOLATION, PUBLIC_TECH, PUBLIC_SEARCH, PRIVATE_CORP or NO_MATCH).
    /// For POLICY_VIOLATION, ResponseContext holds the educational remediation text.
    /// Conversation history is untrusted context. Throws <see cref="GuardrailClassificationException"/>
    /// when no safe classification can be made (fail closed).
    /// </summary>
    Task<(string Lane, string ResponseContext)> ClassifyAndValidateAsync(
        string userInput, string tenantId, IReadOnlyList<ChatHistoryMessage>? conversationHistory = null,
        CancellationToken cancellationToken = default, IChatProgress? progress = null, string? userAgent = null);
}

public interface IGuardrailCoach
{
    /// <summary>
    /// Educational guidance on the correct way to go about a request. Used for requests the rules do not allow and
    /// when the main model declines to engage. Never throws for a model failure or refusal: it falls back to static text.
    /// </summary>
    Task<string> CoachAsync(
        string userInput, CancellationToken cancellationToken = default, IChatProgress? progress = null, string? userAgent = null);
}

public interface IPublicInferenceService
{
    /// <param name="progress">When supplied, the reply is streamed to it as it is generated.</param>
    /// <param name="knowledgeBase">Retrieved KB articles to ground the answer in, or null when none matched.</param>
    Task<string> ExecutePublicInferenceAsync(
        string userInput, CancellationToken cancellationToken = default, IChatProgress? progress = null, string? userAgent = null,
        IReadOnlyList<ChatHistoryMessage>? conversationHistory = null, IReadOnlyList<KbHit>? knowledgeBase = null);
    Task<string> ExecuteSearchInferenceAsync(
        string userInput, string searchContextMarkdown, CancellationToken cancellationToken = default,
        IChatProgress? progress = null, string? userAgent = null, IReadOnlyList<KbHit>? knowledgeBase = null);
}

/// <summary>One knowledge-base article retrieved for a question.</summary>
/// <param name="Number">Human-readable article number, for example KB0001001; the model cites it.</param>
/// <param name="Excerpt">The most useful text for answering: resolution steps when present, otherwise the summary and body start.</param>
/// <param name="Score">Reciprocal-rank-fusion score; only meaningful for ordering within one search.</param>
/// <param name="Match">How it was found: <see cref="KbMatch"/>.</param>
public sealed record KbHit(string Id, string Number, string Title, string Summary, string Excerpt, double Score, string Match);

public static class KbMatch
{
    public const string Semantic = "semantic";
    public const string Lexical = "lexical";
    public const string Both = "semantic+lexical";
}

public interface IKnowledgeBaseSearch
{
    /// <summary>
    /// Finds published, public KB articles of the tenant that are relevant to <paramref name="query"/>, best first.
    /// Never throws for backend failures: it returns an empty list instead, because the KB only ever improves an answer.
    /// The query must already be redacted.
    /// </summary>
    Task<IReadOnlyList<KbHit>> SearchAsync(string tenantId, string query, CancellationToken cancellationToken = default);
}

/// <summary>
/// The model's decision for one turn of a service-desk hand-off: ask the user for something that is still missing, or
/// raise the ticket. <see cref="Message"/> is always what the user is told.
/// </summary>
/// <param name="RaiseTicket">True when the conversation holds enough to hand over (or no more can usefully be gathered).</param>
/// <param name="Message">The next question, or a short confirmation once the ticket is raised.</param>
/// <param name="Title">Short ticket title; set when <paramref name="RaiseTicket"/> is true.</param>
/// <param name="Summary">What the user needs and the relevant facts; set when <paramref name="RaiseTicket"/> is true.</param>
/// <param name="ConversationSummary">A short chronological summary of the conversation for the operator: what was asked, what was suggested and what happened. Separate from <paramref name="Summary"/>.</param>
/// <param name="RecommendedAction">What the service desk should do, such as grant the user access instead of resetting a password.</param>
/// <param name="StepsTried">Troubleshooting already attempted, if any.</param>
public sealed record TicketInterview(
    bool RaiseTicket, string Message, string? Title, string? Summary, string? ConversationSummary, string? RecommendedAction,
    string? StepsTried);

public interface ITicketInterviewer
{
    /// <param name="conversation">The redacted conversation so far, ending with the user's latest message.</param>
    /// <param name="mustRaise">The interview has run its course: the model must raise the ticket now rather than ask more.</param>
    Task<TicketInterview> InterviewAsync(
        string conversation, CancellationToken cancellationToken = default, string? userAgent = null, bool mustRaise = false);
}

public sealed class TicketInterviewException(string message, Exception? inner = null) : Exception(message, inner);

public interface ILiveSearchService
{
    Task<string> GetLiveSearchResultsAsync(string query, CancellationToken cancellationToken = default);
}

public interface ITicketQueuePublisher
{
    /// <summary>Publishes a sanitised private-corp ticket event to the async processing queue.</summary>
    Task PublishAsync(ProcessPrivateCorpTicketEvent ticketEvent, CancellationToken cancellationToken = default);
}

public class GuardrailClassificationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Receives incremental progress while a chat request is processed asynchronously (SignalR).
/// Implementations must not throw for transient delivery problems; progress is best effort.
/// </summary>
public interface IChatProgress : Helpdesk.Llm.ILlmProgress;
