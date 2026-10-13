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

/// <summary>Where text is going when it leaves the service. Policy can differ per destination.</summary>
public enum EgressDestination
{
    /// <summary>The language model (classifier, inference, compliance coach).</summary>
    Model,
    /// <summary>A third-party web search provider, or the model's own web search tool.</summary>
    Search,
    /// <summary>The ticket queue read by the downstream worker.</summary>
    Queue
}

public enum DlpCategory { Secret, Card, GovernmentId, Iban, Email, Phone }

public enum DlpAction { Redact, Allow }

/// <param name="Text">The input with sensitive values replaced by <c>[REDACTED:...]</c> markers.</param>
/// <param name="Count">How many values were replaced. Never log the values themselves.</param>
/// <param name="Categories">Replacements per category name; safe to log and meter.</param>
public readonly record struct RedactionResult(string Text, int Count, IReadOnlyDictionary<string, int>? Categories = null);

/// <summary>
/// Removes sensitive values from text before it leaves the service. What is removed depends on the destination
/// and, optionally, the tenant. Secrets and card numbers are always removed.
/// </summary>
public interface ISensitiveDataRedactor
{
    RedactionResult Redact(string text, EgressDestination destination = EgressDestination.Model, string? tenantId = null);
}

public class GuardrailClassificationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Receives incremental progress while a chat request is processed asynchronously (SignalR).
/// Implementations must not throw for transient delivery problems; progress is best effort.
/// </summary>
public interface IChatProgress
{
    /// <summary>A fragment of the assistant's reply, in order.</summary>
    ValueTask OnTextAsync(string delta, CancellationToken cancellationToken);

    /// <summary>The upstream model asked us to wait (HTTP 429 Retry-After) before the call is retried.</summary>
    ValueTask OnRateLimitedAsync(TimeSpan retryAfter, int attempt, CancellationToken cancellationToken);
}

/// <summary>The upstream model rate limited the request and the allowed waiting budget is exhausted.</summary>
public class UpstreamRateLimitedException(TimeSpan? retryAfter, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
