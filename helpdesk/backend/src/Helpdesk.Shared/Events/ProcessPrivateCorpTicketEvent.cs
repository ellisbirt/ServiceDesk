using System.Text.Json.Serialization;

namespace Helpdesk.Shared.Events;

/// <summary>
/// A private-corp request handed from the intake to the downstream ticket worker (AgentDesk).
/// Additive changes keep <see cref="CurrentSchemaVersion"/>; consumers must ignore unknown fields and treat new optional
/// fields as possibly absent. Bump the version only for breaking changes.
/// </summary>
public record ProcessPrivateCorpTicketEvent
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("eventId")]
    public Guid EventId { get; init; } = Guid.NewGuid();

    [JsonPropertyName("tenantId")]
    public required string TenantId { get; init; }

    [JsonPropertyName("userId")]
    public required string UserId { get; init; }

    [JsonPropertyName("conversationId")]
    public required Guid ConversationId { get; init; }

    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }

    [JsonPropertyName("sanitizedPayload")]
    public required string SanitizedPayload { get; init; }

    /// <summary>A short, redacted one-line subject for the ticket. Optional; derive one from the payload when absent.</summary>
    [JsonPropertyName("subject")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Subject { get; init; }

    /// <summary>What the requester needs now, in a sentence or two. Optional; the same text is inside <see cref="SanitizedPayload"/>.</summary>
    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; init; }

    /// <summary>
    /// A short, redacted, chronological summary of the chat for the operator (what was asked, tried and said), kept
    /// apart from <see cref="Summary"/>. Optional.
    /// </summary>
    [JsonPropertyName("conversationSummary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConversationSummary { get; init; }

    /// <summary>The classifier's confidence in the PRIVATE_CORP lane (0-1), when the classifier reports one.</summary>
    [JsonPropertyName("laneConfidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LaneConfidence { get; init; }

    /// <summary>
    /// Structured, redacted answers from the follow-up form the requester completed before the ticket was raised.
    /// <see cref="SanitizedPayload"/> carries the same answers as plain text for consumers that ignore this field.
    /// </summary>
    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TicketDetails? Details { get; init; }

    [JsonPropertyName("timestampUtc")]
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>Answers the requester gave in the follow-up form before the ticket was raised.</summary>
public sealed record TicketDetails
{
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("answers")]
    public required IReadOnlyList<TicketDetailAnswer> Answers { get; init; }

    /// <summary>Plain-text rendering, one "Label: value" block per answer.</summary>
    public string ToText() =>
        Title + "\n\n" + string.Join("\n\n", Answers.Select(a => $"{a.Label}:\n{a.Value}"));
}

public sealed record TicketDetailAnswer
{
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }
}

public static class TicketSubject
{
    public const int MaxLength = 120;

    /// <summary>A single-line subject from free text: first non-empty line, whitespace collapsed, truncated on a word boundary.</summary>
    public static string FromText(string? text, int maxLength = MaxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(no subject)";
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        line = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length <= maxLength) return line;
        var cut = line.LastIndexOf(' ', maxLength - 1);
        return (cut > maxLength / 2 ? line[..cut] : line[..(maxLength - 1)]).TrimEnd() + "\u2026";
    }
}
