namespace Helpdesk.Dlp;

/// <summary>Where text is going when it leaves the service. Policy can differ per destination.</summary>
public enum EgressDestination
{
    /// <summary>The language model (classifier, inference, compliance coach, agent assist).</summary>
    Model,
    /// <summary>A third-party web search provider, or the model's own web search tool.</summary>
    Search,
    /// <summary>The ticket queue read by the downstream worker.</summary>
    Queue,
    /// <summary>An embedding model (knowledge base article vectors).</summary>
    Embedding
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
