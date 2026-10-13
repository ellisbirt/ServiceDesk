using Helpdesk.Shared.Contracts;
using Helpdesk.Shared.Events;

namespace Helpdesk.Core.Interfaces;

public interface ITenantProvider
{
    string GetCurrentTenantId();
    string GetCurrentUserId();
}

public interface IGuardrailRouterService
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

public interface IPublicInferenceService
{
    /// <param name="progress">When supplied, the reply is streamed to it as it is generated.</param>
    Task<string> ExecutePublicInferenceAsync(
        string userInput, CancellationToken cancellationToken = default, IChatProgress? progress = null, string? userAgent = null);
    Task<string> ExecuteSearchInferenceAsync(
        string userInput, string searchContextMarkdown, CancellationToken cancellationToken = default,
        IChatProgress? progress = null, string? userAgent = null);
}

public interface IIntakeFormGenerator
{
    Task<IntakeForm> GenerateAsync(
        string userInput, string lane, CancellationToken cancellationToken = default, string? userAgent = null);
}

public sealed class IntakeFormGenerationException(string message, Exception? inner = null) : Exception(message, inner);

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
