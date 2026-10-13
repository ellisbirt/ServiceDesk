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

    /// <summary>The classifier's confidence in the PRIVATE_CORP lane (0-1), when the classifier reports one.</summary>
    [JsonPropertyName("laneConfidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LaneConfidence { get; init; }

    [JsonPropertyName("timestampUtc")]
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
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
